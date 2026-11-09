// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;

namespace IceScreenTouch.App;

/// <summary>
/// What the About tab shows: the version (one source, <c>Directory.Build.props</c>; the SDK appends the git commit),
/// the license and the third-party notices (both embedded from the repository root), the links.
/// </summary>
public static class AppInfo
{
    /// <summary>The product name as shown to the user.</summary>
    public const string ProductName = "IceScreen Touch";

    /// <summary>The source code and the releases.</summary>
    public const string RepositoryUrl = "https://github.com/shinplaster/IceScreenTouch";

    /// <summary>Where users report problems.</summary>
    public const string IssuesUrl = RepositoryUrl + "/issues";

    private const string LicenseResource = "IceScreenTouch.App.LICENSE.txt";
    private const string NoticesResource = "IceScreenTouch.App.THIRD-PARTY-NOTICES.txt";

    private static readonly Assembly Self = typeof(AppInfo).Assembly;

    // Read once, when first needed (the notices are about 430 KB).
    private static readonly Lazy<string> License = new(() => MitPart(ReadResource(LicenseResource)));
    private static readonly Lazy<string> Notices = new(() => ReadResource(NoticesResource));
    private static readonly Lazy<IReadOnlyList<string>> ComponentList = new(() =>
        [.. Notices.Value.Split('\n').Select(l => l.TrimEnd('\r')).TakeWhile(l => !l.StartsWith('=')).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..])]);

    /// <summary>The full version, such as <c>0.1.0+e51464e...</c> (SemVer with the commit as build metadata).</summary>
    public static string InformationalVersion { get; } =
        Self.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>The version without the build metadata, such as <c>0.1.0</c>.</summary>
    public static string Version { get; } = SplitVersion(InformationalVersion).Version;

    /// <summary>The short commit (7 characters), or null when the build had no git commit.</summary>
    public static string? Commit { get; } = SplitVersion(InformationalVersion).Commit;

    /// <summary>The copyright line.</summary>
    public static string Copyright { get; } = Self.GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright ?? string.Empty;

    /// <summary>
    /// The MIT license of IceScreen Touch: LICENSE up to its separator line. What follows is the GPLv3 exception for
    /// the UxPlay patches of the PoC, which are not part of the app.
    /// </summary>
    public static string LicenseText => License.Value;

    /// <summary>The third-party notices (THIRD-PARTY-NOTICES.txt, written by tools/publish.ps1).</summary>
    public static string NoticesText => Notices.Value;

    /// <summary>The list of bundled components: the part of the notices before the first license text.</summary>
    public static IReadOnlyList<string> Components => ComponentList.Value;

    /// <summary>
    /// The version, the HID report map, the settings format, .NET and the OS, in English: what a problem report
    /// needs (the "copy" button of the About tab).
    /// </summary>
    public static string Diagnostics => string.Join(Environment.NewLine,
        $"{ProductName} {InformationalVersion}",
        $"HID report map: version {HidReports.ReportMapVersion} ({HidReports.KeyboardReportMap.Length} bytes)",
        $"Settings format: version {AppSettings.CurrentVersion}",
        $".NET {Environment.Version}, {RuntimeInformation.ProcessArchitecture}",
        $"OS: {RuntimeInformation.OSDescription}",
        $"UI language: {CultureInfo.CurrentUICulture.Name}");

    /// <summary>Splits <c>version+metadata</c>; the metadata is a commit when it starts with 7 hex digits.</summary>
    internal static (string Version, string? Commit) SplitVersion(string informational)
    {
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus < 0)
        {
            return (informational, null);
        }

        var metadata = informational[(plus + 1)..];
        var commit = metadata.Length >= 7 && metadata[..7].All(char.IsAsciiHexDigit) ? metadata[..7].ToLowerInvariant() : null;
        return (informational[..plus], commit);
    }

    /// <summary>The text before the first line of dashes (the whole text when there is none), trimmed.</summary>
    internal static string MitPart(string license)
    {
        var lines = license.ReplaceLineEndings("\n").Split('\n');
        var end = Array.FindIndex(lines, l => l.StartsWith("---", StringComparison.Ordinal));
        return string.Join('\n', end < 0 ? lines : lines[..end]).Trim();
    }

    private static string ReadResource(string name)
    {
        using var stream = Self.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"missing embedded resource {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
