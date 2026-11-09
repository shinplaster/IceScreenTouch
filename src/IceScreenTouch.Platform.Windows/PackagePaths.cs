// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Maps paths for programs outside the package container (Explorer, ShellExecute). A packaged app's writes under
/// %LOCALAPPDATA% that create new files or folders are redirected to
/// %LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Local\..., and only the app itself sees them at the original path.
/// </summary>
public static class PackagePaths
{
    /// <summary>
    /// The path to hand to a program outside the container: the redirected copy of <paramref name="path"/> when it
    /// is under <paramref name="localAppData"/>, the app is packaged and the redirected path exists; otherwise
    /// <paramref name="path"/> itself (not packaged, outside LocalAppData, or the folder already existed before the
    /// install, in which case Windows does not redirect and the original is real).
    /// </summary>
    public static string ForExplorer(string path, string localAppData, string? familyName, Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(familyName) || string.IsNullOrEmpty(localAppData) || string.IsNullOrEmpty(path))
        {
            return path;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(localAppData));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full.Length <= root.Length
            || !full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            || full[root.Length] != Path.DirectorySeparatorChar)
        {
            return path;
        }

        var relative = full[(root.Length + 1)..];
        var redirected = Path.Combine(root, "Packages", familyName, "LocalCache", "Local", relative);
        return exists(redirected) ? redirected : path;
    }

    /// <summary>
    /// <see cref="ForExplorer(string, string, string?, Func{string, bool})"/> for this process: the real
    /// LocalAppData, this package's family name and the file system.
    /// </summary>
    public static string ForExplorer(string path) => ForExplorer(
        path,
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        PackageIdentity.FamilyName,
        candidate => Directory.Exists(candidate) || File.Exists(candidate));
}
