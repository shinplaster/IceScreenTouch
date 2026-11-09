// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

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
}
