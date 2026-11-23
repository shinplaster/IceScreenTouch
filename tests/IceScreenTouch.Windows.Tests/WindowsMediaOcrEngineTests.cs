// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;
using IceScreenTouch.Api.Tests.Ocr;
using IceScreenTouch.Platform.Windows;
using SkiaSharp;

namespace IceScreenTouch.Windows.Tests;

/// <summary>The Windows.Media.Ocr engine against the conformance checks, and what is particular to it.</summary>
public class WindowsMediaOcrEngineTests : OcrEngineConformanceTests
{
    protected override IOcrEngine CreateEngine() => new WindowsMediaOcrEngine();

    [OcrLanguageFact]
    public void ItDescribesItselfAsTheBuiltInWindowsEngine()
    {
        var info = new WindowsMediaOcrEngine().Info;
        Assert.Equal(("windows", "Windows.Media.Ocr", OcrEngineKind.BuiltIn), (info.Id, info.Name, info.Kind));
        Assert.Matches(@"^10\.0\.\d+$", info.Version);
        Assert.Equal((true, false, false), (info.Capabilities.WordBoxes, info.Capabilities.Confidence, info.Capabilities.Region));
        var hints = info.Capabilities.Image;
        Assert.Equal((1300, 3.0, 2600, 64), (hints.TargetShortSide, hints.MaxUpscale, hints.MaxShortSide, hints.MinDimension));
        Assert.True(hints.MaxDimension >= 2600);
        Assert.Equal(info, OcrOutputGuard.Sanitize(info)); // within the host's limits as it is
    }

    [OcrLanguageFact("ja")]
    public async Task ARegionalTagResolvesAndTheRecognizerNamesItsLanguage()
    {
        var engine = new WindowsMediaOcrEngine();
        var tag = await engine.ResolveLanguageAsync("ja-JP", default);
        Assert.NotNull(tag);
        var (frame, _) = OcrDrawing.Draw(OcrDrawing.JapaneseTypeface()!, ("画面表示と明るさ", 60, 100));
        var recognition = await engine.RecognizeAsync(new OcrRequest(OcrDrawing.Packed(frame), frame.Width, frame.Height, tag, null), default);
        Assert.Equal("ja", recognition.Language);
    }

    [OcrLanguageFact("en-US")]
    public async Task TheDefaultLanguageIsOneOfTheInstalledOnes()
    {
        var engine = new WindowsMediaOcrEngine();
        var languages = await engine.GetLanguagesAsync(default);
        Assert.NotEmpty(languages.Available);
        Assert.NotNull(languages.Default);
        Assert.Equal(languages.Default, await engine.ResolveLanguageAsync(null, default));
    }

    [OcrLanguageFact("en-US")]
    public async Task ALanguageThatIsNotInstalledFailsTheCallAsUnavailable()
    {
        var engine = new WindowsMediaOcrEngine();
        var request = new OcrRequest(new byte[64 * 64 * 4], 64, 64, "zz", null);
        var ex = await Assert.ThrowsAsync<OcrEngineException>(() => engine.RecognizeAsync(request, default));
        Assert.Equal(OcrFailure.LanguageUnavailable, ex.Failure);
    }

    [OcrLanguageFact("en-US")]
    public async Task ASmallRegionIsReadThroughThePadding()
    {
        // A word cut out tightly: the host enlarges it (3 x at most) and pads it to the engine's smallest side.
        var (frame, drawn) = OcrDrawing.Draw(SKTypeface.Default, ("Bluetooth", 40, 100));
        var service = new OcrService([new WindowsMediaOcrEngine()]);
        var target = await service.ResolveAsync(null, "en-US", default);
        using var turn = (await service.EnterTurnAsync(default))!;
        var region = new OcrRect(drawn[0].X - 2, drawn[0].Y - 2, drawn[0].Width + 4, drawn[0].Height + 4);
        var reading = await service.RecognizeAsync(turn, target, frame, region, default);
        Assert.Contains(reading.Lines, l => l.Text.Contains("Bluetooth", StringComparison.Ordinal));
    }
}

public class WindowsOcrUnskewTests
{
    [Fact]
    public void NoAngleLeavesTheBoxAsItIs()
    {
        var box = new OcrRect(10, 20, 30, 40);
        Assert.Equal(box, WindowsMediaOcrEngine.Unskew(0, 600, 300)(box));
        Assert.Equal(box, WindowsMediaOcrEngine.Unskew(double.NaN, 600, 300)(box));
    }

    [Fact]
    public void ABoxOfTheDeskewedImageIsTurnedBackByTheAngle()
    {
        // Measured: "Settings" drawn at (60, 68, 57 x 16) in a 600 x 300 image; Windows said TextAngle 4.5 and gave (55, 84, 56 x 16).
        var back = WindowsMediaOcrEngine.Unskew(4.5, 600, 300)(new OcrRect(55, 84, 56, 16));
        Assert.InRange(back.X, 58, 62);
        Assert.InRange(back.Y, 63, 69);
        Assert.InRange(back.Right, 115, 119);
        Assert.InRange(back.Bottom, 83, 87);
    }
}

public class WindowsOcrLanguageTests
{
    [OcrLanguageFact("en-US")]
    public async Task VariantsOfALanguageResolveToTheInstalledOne()
    {
        // Each variant would otherwise be a recognizer of its own (one per tag a client sends).
        var engine = new WindowsMediaOcrEngine();
        var installed = (await engine.GetLanguagesAsync(default)).Available;
        foreach (var tag in new[] { "en-US", "en-GB", "en-x-abc123", "en-US-x-q1w2" })
        {
            var resolved = await engine.ResolveLanguageAsync(tag, default);
            Assert.NotNull(resolved);
            Assert.Contains(resolved, installed, StringComparer.OrdinalIgnoreCase);
        }
    }
}
