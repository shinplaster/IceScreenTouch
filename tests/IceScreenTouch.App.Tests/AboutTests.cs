// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using IceScreenTouch.App.ViewModels;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Tests;

public sealed class AboutTests
{
    [Theory]
    [InlineData("0.1.0+e51464e0123456789abcdef0123456789abcdef", "0.1.0", "e51464e")]
    [InlineData("1.2.3-beta.1+ABCDEF0", "1.2.3-beta.1", "abcdef0")]
    [InlineData("0.1.0", "0.1.0", null)]
    [InlineData("0.1.0+local", "0.1.0", null)]
    [InlineData("0.1.0+abc", "0.1.0", null)]
    public void SplitsTheVersionFromTheCommit(string informational, string version, string? commit)
    {
        Assert.Equal((version, commit), AppInfo.SplitVersion(informational));
    }

    [Fact]
    public void TheVersionComesFromDirectoryBuildProps()
    {
        // SemVer X.Y.Z (the SDK adds the commit as build metadata in a git checkout).
        Assert.Matches(@"^\d+\.\d+\.\d+$", AppInfo.Version);
        Assert.StartsWith(AppInfo.Version, AppInfo.InformationalVersion, StringComparison.Ordinal);
        Assert.StartsWith("Copyright (c) ", AppInfo.Copyright, StringComparison.Ordinal);
    }

    [Fact]
    public void LicenseAndNoticesAreEmbedded()
    {
        Assert.StartsWith("MIT License", AppInfo.LicenseText, StringComparison.Ordinal);
        Assert.Contains("third-party notices", AppInfo.NoticesText, StringComparison.Ordinal);

        // The list part of THIRD-PARTY-NOTICES.txt (tools/publish.ps1 writes it from the bundle).
        Assert.Contains(AppInfo.Components, c => c.StartsWith("Avalonia (Avalonia ", StringComparison.Ordinal));
        Assert.Contains(AppInfo.Components, c => c.StartsWith(".NET Runtime ", StringComparison.Ordinal));
        Assert.DoesNotContain(AppInfo.Components, c => c.Contains("====", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLicenseIsTheMitPartWithoutThePocException()
    {
        var text = AppInfo.LicenseText;

        Assert.StartsWith("MIT License", text, StringComparison.Ordinal);
        Assert.EndsWith("SOFTWARE.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("poc", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GPL", text, StringComparison.Ordinal);
        Assert.Equal("a\nb", AppInfo.MitPart("a\r\nb\r\n\r\n----\r\nException"));
        Assert.Equal("a", AppInfo.MitPart("a\n"));
    }

    [Fact]
    public void TheAboutIconIsTheLargestFrame()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "icon.ico");
        var png = IconFrames.LargestPng(File.ReadAllBytes(path));

        Assert.NotNull(png);
        // PNG IHDR: width and height (big endian) at bytes 16 and 20.
        Assert.Equal(256, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(256, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Null(IconFrames.LargestPng([1, 2, 3]));
    }

    [Fact]
    public void DiagnosticsNameTheVersionsAProblemReportNeeds()
    {
        var text = AppInfo.Diagnostics;

        Assert.Contains(AppInfo.InformationalVersion, text, StringComparison.Ordinal);
        Assert.Contains($"HID report map: version {HidReports.ReportMapVersion} ({HidReports.KeyboardReportMap.Length} bytes)", text, StringComparison.Ordinal);
        Assert.Contains("Settings format: version 2", text, StringComparison.Ordinal);
        Assert.Contains("OS: ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTabShowsTheVersionsInTheUserLanguage()
    {
        var about = new AboutViewModel();

        Assert.Equal("IceScreen Touch", about.ProductName);
        Assert.StartsWith($"バージョン {AppInfo.Version}", about.VersionText, StringComparison.Ordinal);
        Assert.Equal("HID の構成: バージョン 3（263 バイト）。バージョンが変わると、端末のペアリングのやり直しが要ります。", about.HidMapText);
        Assert.Equal("設定ファイル: バージョン 2", about.SettingsVersionText);
        Assert.NotEmpty(about.Components);
        Assert.Equal($"部品の一覧（{about.Components.Count} 個）", about.ComponentsHeader);
        Assert.False(about.Copied);
    }

    [Theory]
    [InlineData("en", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.md#installing-an-airplay-receiver")]
    [InlineData("en-US", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.md#installing-an-airplay-receiver")]
    [InlineData("fr", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.md#installing-an-airplay-receiver")]
    [InlineData("", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.md#installing-an-airplay-receiver")]
    [InlineData("ja", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.ja.md#%E5%8F%97%E4%BF%A1%E3%82%BD%E3%83%95%E3%83%88%E3%82%92%E5%85%A5%E3%82%8C%E3%82%8B")]
    [InlineData("ja-JP", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.ja.md#%E5%8F%97%E4%BF%A1%E3%82%BD%E3%83%95%E3%83%88%E3%82%92%E5%85%A5%E3%82%8C%E3%82%8B")]
    public void TheReceiverGuideOpensInTheAppLanguage(string culture, string expected)
    {
        Assert.Equal(expected, AppInfo.ReceiverGuideUri(CultureInfo.GetCultureInfo(culture)).AbsoluteUri);
    }

    [Theory]
    [InlineData("en", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.md")]
    [InlineData("en-US", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.md")]
    [InlineData("fr", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.md")]
    [InlineData("", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.md")]
    [InlineData("ja", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.ja.md")]
    [InlineData("ja-JP", "https://github.com/shinplaster/IceScreenTouch/blob/main/docs/USER-GUIDE.ja.md")]
    public void TheUserGuideOpensInTheAppLanguage(string culture, string expected)
    {
        Assert.Equal(expected, AppInfo.UserGuideUri(CultureInfo.GetCultureInfo(culture)).AbsoluteUri);
    }

    [Fact]
    public void TheGuideLinksAreTheGuideFilesOfTheRepository()
    {
        // The About tab and the receiver list open the same two files on the main branch.
        foreach (var culture in new[] { "en", "ja" })
        {
            var top = AppInfo.UserGuideUri(CultureInfo.GetCultureInfo(culture));
            var chapter = AppInfo.ReceiverGuideUri(CultureInfo.GetCultureInfo(culture));
            Assert.StartsWith(AppInfo.RepositoryUrl + "/blob/main/docs/USER-GUIDE.", top.AbsoluteUri, StringComparison.Ordinal);
            Assert.Equal(top.AbsoluteUri, chapter.GetLeftPart(UriPartial.Path));
            Assert.Empty(top.Fragment);
            Assert.True(File.Exists(Path.Combine(RepositoryRoot(), "docs", top.Segments[^1])));
        }
    }

    [Theory]
    [InlineData("en", "USER-GUIDE.md")]
    [InlineData("ja", "USER-GUIDE.ja.md")]
    public void TheReceiverGuideAnchorIsAChapterOfTheGuide(string culture, string file)
    {
        // GitHub's anchor of a heading: lower case, spaces as hyphens, punctuation other than hyphens dropped.
        var uri = AppInfo.ReceiverGuideUri(CultureInfo.GetCultureInfo(culture));
        Assert.EndsWith("/docs/" + file, uri.GetLeftPart(UriPartial.Path), StringComparison.Ordinal);
        var anchor = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        var headings = File.ReadAllLines(Path.Combine(RepositoryRoot(), "docs", file))
            .Where(l => l.StartsWith("## ", StringComparison.Ordinal))
            .Select(l => new string([.. l[3..].Trim().ToLowerInvariant().Replace(' ', '-').Where(c => char.IsLetterOrDigit(c) || c == '-')]));
        Assert.Contains(anchor, headings);
    }

    // The nearest folder above the test assembly that has IceScreenTouch.slnx.
    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "IceScreenTouch.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("IceScreenTouch.slnx was not found above " + AppContext.BaseDirectory);
    }
}
