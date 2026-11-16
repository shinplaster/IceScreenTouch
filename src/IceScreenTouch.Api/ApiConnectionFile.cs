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

    private static int s_labelFailureLogged;

    private readonly Action<string>? _secure;
    private readonly Action<string>? _log;

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
    /// <param name="log">Told once when the integrity label could not be put on the file (the file stays protected by its DACL).</param>
    public ApiConnectionFile(string directory, Action<string>? secure = null, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _secure = secure;
        _log = log;
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
            if (_secure is { } hook)
            {
                using (File.Create(temp))
                {
                }

                hook(temp);

                // A client or a virus scanner may hold the file for a moment (a sharing violation): try again a few times.
                Retry(() => File.WriteAllBytes(temp, bytes));
            }
            else
            {
                // Created with the current-user-only ACL in the same call: there is no moment at which another account could
                // open the file, and the token goes in through the handle that created it.
                using var stream = CreateProtected(temp, _log);
                stream.Write(bytes);
            }

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

    /// <summary>The default restriction: the current user only (on Windows also the Medium integrity label, see <see cref="CreateProtected"/>).</summary>
    public static void RestrictToCurrentUser(string path, Action<string>? log = null)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(path);
            LabelOrReport(IntegrityLabel.TrySet(path), log);
        }
        else if (!OperatingSystem.IsBrowser() && !OperatingSystem.IsWasi())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>
    /// Creates a NEW file (fails if it exists) that only the current user can open, in one step: on Windows with a protected
    /// DACL that has the current user only (inheritance off) given to the create call itself, on Unix with mode 0600. Nothing
    /// is ever readable by another account, not even for a moment. On Windows the file also gets the Medium mandatory integrity
    /// label with No-Read-Up and No-Write-Up, before the caller writes anything, so that a Low-integrity process of the same user
    /// cannot read it either; if the label cannot be set the file stays protected by its DACL and <paramref name="log"/> is told
    /// once. Returns the stream, open for writing.
    /// </summary>
    public static FileStream CreateProtected(string path, Action<string>? log = null)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateProtectedWindows(path, log);
        }

        return new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
    }

    /// <summary>
    /// True when only the current user can read the file: on Windows the current user owns it and its DACL is protected and
    /// names only the current user; on Unix no group or other permission is set. The integrity label is not part of the test: a file
    /// without it (made before the label existed, or where it could not be set) is still trusted, see <see cref="EnsureIntegrityLabel"/>. A file that does not pass is not to be trusted
    /// with a secret (it may have been planted or copied by another account).
    /// </summary>
    /// <exception cref="IOException">The file cannot be examined for another reason than permission.</exception>
    public static bool IsProtectedToCurrentUser(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return IsProtectedWindows(path);
            }

            if (OperatingSystem.IsBrowser() || OperatingSystem.IsWasi())
            {
                return true;
            }

            return (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) == 0;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // not even the ACL can be read: not ours
        }
    }

    [SupportedOSPlatform("windows")]
    private static FileStream CreateProtectedWindows(string path, Action<string>? log)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("the current Windows user has no SID");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
        LabelOrReport(IntegrityLabel.TrySet(stream.SafeFileHandle), log); // still empty: the label is there before any secret is
        return stream;
    }

    // A label that could not be set is no reason to stop: the DACL still keeps other accounts out. Said once (log, when there is one).
    private static void LabelOrReport(string? error, Action<string>? log)
    {
        if (error is not null && log is not null && Interlocked.Exchange(ref s_labelFailureLogged, 1) == 0)
        {
            log($"[api] the integrity label could not be put on a secret file ({error}); it stays protected by its permissions only");
        }
    }

    /// <summary>
    /// The mandatory integrity label of the file as SDDL (for example <c>S:(ML;;NWNR;;;ME)</c>; an empty string when the file has none),
    /// or null when it is not Windows or the label cannot be read.
    /// </summary>
    public static string? GetIntegrityLabel(string path) => OperatingSystem.IsWindows() ? IntegrityLabel.Read(path) : null;

    /// <summary>
    /// Puts the Medium integrity label (No-Read-Up and No-Write-Up) on a protected file whose label is missing or is anything else
    /// (a file made before the label existed, or one whose label was changed). Does nothing where the label is already exactly that or
    /// on other systems; a failure is reported once through <paramref name="log"/> and is no error.
    /// </summary>
    public static void EnsureIntegrityLabel(string path, Action<string>? log = null)
    {
        // An unreadable label (null) is left alone: it cannot be told what to correct.
        if (OperatingSystem.IsWindows() && IntegrityLabel.Read(path) is { } label && !IntegrityLabel.IsExpected(label))
        {
            LabelOrReport(IntegrityLabel.TrySet(path), log);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsProtectedWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("the current Windows user has no SID");
        var security = new FileInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || owner != user || !security.AreAccessRulesProtected)
        {
            return false;
        }

        return security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .All(rule => rule.IdentityReference == user);
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
