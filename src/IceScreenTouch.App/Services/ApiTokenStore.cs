// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text;
using IceScreenTouch.Api;

namespace IceScreenTouch.App.Services;

/// <summary>
/// Keeps the bearer token of the automation API in its own file (<c>api-token</c> in the app's data folder), apart from
/// settings.json (which users copy into bug reports). The token is created the first time the API is switched on and kept
/// until the user regenerates it.
/// <para>
/// The file is created with a current-user-only DACL in the same call that creates it (<see cref="ApiConnectionFile.CreateProtected"/>;
/// the token never sits in a file other accounts can open) and replaced atomically. A file that is read back is trusted only
/// when the current user owns it and nobody else is on its ACL (<see cref="ApiConnectionFile.IsProtectedToCurrentUser"/>):
/// otherwise it may have been planted or copied, and a new token replaces it. A file that cannot be READ right now (a sharing
/// violation, a disk error) is never replaced: the start fails instead, because replacing it would log every script out for a
/// hiccup. Only content that is clearly not a token (empty, wrong format) is replaced. The token is never logged.
/// </para>
/// </summary>
public sealed class ApiTokenStore
{
    /// <summary>The file name.</summary>
    public const string FileName = "api-token";

    private const int ReadAttempts = 5;

    private readonly Action<string>? _log;
    private readonly Action<string>? _secure;

    /// <summary>Creates the handle; nothing is read or written yet.</summary>
    /// <param name="directory">The data folder (created when missing).</param>
    /// <param name="log">Receives a line when the file had to be replaced (never the token).</param>
    /// <param name="secure">
    /// Tests only: restricts the temporary file in place of the one-step protected creation (and the check of the file's
    /// owner and ACL on reading is skipped). If it throws nothing is published.
    /// </param>
    public ApiTokenStore(string directory, Action<string>? log = null, Action<string>? secure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = directory;
        _log = log;
        _secure = secure;
    }

    /// <summary>The data folder.</summary>
    public string Directory { get; }

    /// <summary>The full path of the token file.</summary>
    public string Path => System.IO.Path.Combine(Directory, FileName);

    /// <summary>
    /// The token in the file; a new one is created (and written) when there is no file, or the file is empty, does not hold a
    /// token as <see cref="ApiToken.Generate"/> makes it, is not owned by the current user or is open to other accounts.
    /// </summary>
    /// <exception cref="IOException">The file exists but cannot be read (after a few tries), or cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public string GetOrCreate()
    {
        DeleteLeftovers();
        if (File.Exists(Path))
        {
            var text = ReadText();
            if (text is null)
            {
                _log?.Invoke("[api] the token file could not be opened (access denied); a new token was created");
            }
            else if (!ApiToken.IsValid(text))
            {
                _log?.Invoke("[api] the token file was empty or not a token; a new token was created");
            }
            else if (_secure is not null || ApiConnectionFile.IsProtectedToCurrentUser(Path))
            {
                if (_secure is null)
                {
                    ApiConnectionFile.EnsureIntegrityLabel(Path, _log); // a file of an earlier version has none
                }

                return text;
            }
            else
            {
                _log?.Invoke("[api] the token file was not private to the current user; a new token was created");
            }
        }

        return Write(ApiToken.Generate());
    }

    /// <summary>Replaces the token with a new one (the old one stops working as soon as the server takes the new one).</summary>
    /// <exception cref="IOException">The file cannot be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file cannot be written.</exception>
    public string Regenerate()
    {
        DeleteLeftovers();
        return Write(ApiToken.Generate());
    }

    // The trimmed text, or null when access is denied (then the file is not ours to trust). An I/O error (another program
    // holds the file for a moment) is tried again a few times and then thrown: the file may well hold the right token.
    private string? ReadText()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(Path, Encoding.UTF8).Trim();
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
            catch (IOException) when (attempt < ReadAttempts - 1)
            {
                Thread.Sleep(10 << attempt);
            }
        }
    }

    private string Write(string token)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var temp = System.IO.Path.Combine(Directory, $"{FileName}.{Guid.NewGuid():N}.tmp");
        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(token);
        try
        {
            if (_secure is { } hook)
            {
                using (File.Create(temp))
                {
                }

                hook(temp);
                File.WriteAllBytes(temp, bytes);
            }
            else
            {
                using var stream = ApiConnectionFile.CreateProtected(temp, _log);
                stream.Write(bytes);
            }

            File.Move(temp, Path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }

        return token;
    }

    // Temporary files of a write that was cut short (a crash between create and move).
    private void DeleteLeftovers()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var leftover in System.IO.Directory.GetFiles(Directory, $"{FileName}.*.tmp"))
        {
            TryDelete(leftover);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original error (or nothing) is what matters.
        }
    }
}
