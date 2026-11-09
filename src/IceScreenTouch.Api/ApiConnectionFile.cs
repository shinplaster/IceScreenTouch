// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace IceScreenTouch.Api;

/// <summary>
/// The connection file <c>api.json</c> (<c>{ "port": 47821, "token": "...", "pid": 1234 }</c>) that scripts read to find the API
/// (docs/API-DESIGN.md, chapter 4). It exists only while the API is on. It is written atomically (a temporary file in
/// the same directory, restricted to the current user before the token goes in, then moved over the old file).
/// </summary>
public sealed class ApiConnectionFile
{
    /// <summary>The file name.</summary>
    public const string FileName = "api.json";

    private readonly Action<string>? _secure;

    /// <summary>Creates the handle; nothing is written yet.</summary>
    /// <param name="directory">
    /// Where the file goes (the app passes <c>%LOCALAPPDATA%\IceScreenTouch</c>; the MSIX redirection is automatic).
    /// Created when missing.
    /// </param>
    /// <param name="secure">
    /// Restricts the file at the given path to the current user. It is called on the temporary file before the token is
    /// written; if it throws nothing is published. When null: on Windows the DACL becomes the current user only
    /// (inheritance off), on Unix the mode becomes 0600, elsewhere nothing happens.
    /// </param>
    public ApiConnectionFile(string directory, Action<string>? secure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _secure = secure;
    }

    /// <summary>The directory of the file.</summary>
    public string Directory { get; }

    /// <summary>The full path of <c>api.json</c>.</summary>
    public string Path => System.IO.Path.Combine(Directory, FileName);

    /// <summary>Writes (or replaces) the file. Throws <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> on failure; nothing half-written is left.</summary>
    public void Write(int port, string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        System.IO.Directory.CreateDirectory(Directory);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ApiConnectionInfo(port, token, Environment.ProcessId),ApiJson.Default.ApiConnectionInfo);
        var temp = System.IO.Path.Combine(Directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (File.Create(temp))
            {
            }

            if (_secure is { } hook)
            {
                hook(temp);
            }
            else
            {
                RestrictToCurrentUser(temp);
            }

            // A client or a virus scanner may hold the file for a moment (a sharing violation): try again a few times.
            Retry(() => File.WriteAllBytes(temp, bytes));
            Retry(() => File.Move(temp, Path, overwrite: true));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Deletes <c>api.json</c> and any leftover temporary files of it, trying a few times when a file is held open for a
    /// moment. Returns true when none is left.
    /// </summary>
    public bool Delete()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return true;
        }

        var gone = TryDeleteWithRetry(Path);
        foreach (var leftover in System.IO.Directory.GetFiles(Directory, $"{FileName}.*.tmp"))
        {
            gone &= TryDeleteWithRetry(leftover);
        }

        return gone;
    }

    private static bool TryDeleteWithRetry(string path)
    {
        try
        {
            Retry(() => File.Delete(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return !File.Exists(path);
    }

    /// <summary>Runs <paramref name="action"/>, repeating it (10, 20, 40, 80 ms apart) while it fails with an I/O error.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(10 << attempt);
            }
        }
    }

    /// <summary>The default restriction: the current user only.</summary>
    public static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(path);
        }
        else if (!OperatingSystem.IsBrowser() && !OperatingSystem.IsWasi())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("the current Windows user has no SID");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort for the temporary file of a failed write: the original error is the one to report.
        }
    }
}
