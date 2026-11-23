// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;
using IceScreenTouch.Core;
using SkiaSharp;

namespace IceScreenTouch.Api.Tests.Ocr;

/// <summary>
/// The checks every OCR engine must pass (docs/API-DESIGN.md, 16.10.5): it reads known English and Japanese text drawn with Skia where it was
/// drawn, answers "no such language" without throwing, honours cancellation, keeps its boxes inside the image and survives repeated calls. A
/// project that has an engine derives from this class; <see cref="OcrLanguageFactAttribute"/> (one per test project) skips a check when the
/// machine lacks the language or a font to draw it.
/// </summary>
/// <remarks>This file is compiled into IceScreenTouch.Api.Tests and linked into IceScreenTouch.Windows.Tests.</remarks>
public abstract class OcrEngineConformanceTests
{
    /// <summary>The engine under test.</summary>
    protected abstract IOcrEngine CreateEngine();

    [OcrLanguageFact("en-US")]
    public async Task ReadsEnglishTextWhereItWasDrawn()
    {
        var engine = CreateEngine();
        var (frame, drawn) = OcrDrawing.Draw(SKTypeface.Default, ("Settings", 60, 80), ("General", 60, 200));
        var reading = await ReadAsync(engine, frame, "en-US");
        AssertFound(reading, "Settings", drawn[0]);
        AssertFound(reading, "General", drawn[1]);
    }

    [OcrLanguageFact("ja")]
    public async Task ReadsJapaneseTextWhereItWasDrawn()
    {
        var engine = CreateEngine();
        // A few rows of a settings screen: Windows reads short phrases more reliably with other text around them.
        var (frame, drawn) = OcrDrawing.Draw(OcrDrawing.JapaneseTypeface()!, ("一般", 60, 40), ("画面表示と明るさ", 60, 80), ("プライバシー", 60, 120), ("バッテリー", 60, 160), ("サウンドと触覚", 60, 200));
        var reading = await ReadAsync(engine, frame, "ja");
        AssertFound(reading, "表示と明るさ", drawn[1], partial: true);
        AssertFound(reading, "プライバシー", drawn[2]);
        AssertFound(reading, "バッテリー", drawn[3]);
    }

    [OcrLanguageFact("en-US")]
    public async Task AnOpaqueVersionOfAPictureWithoutAlphaIsRead()
    {
        // The picture's alpha is 0 and its rows are padded, as a receiver's window may give it; the host makes it packed and opaque.
        var (drawnFrame, drawn) = OcrDrawing.Draw(SKTypeface.Default, ("Bluetooth", 40, 100));
        var stride = (drawnFrame.Width * 4) + 64;
        var bytes = new byte[stride * drawnFrame.Height];
        for (var y = 0; y < drawnFrame.Height; y++)
        {
            drawnFrame.Bgra.AsSpan(y * drawnFrame.Stride, drawnFrame.Width * 4).CopyTo(bytes.AsSpan(y * stride));
            for (var x = 0; x < drawnFrame.Width; x++)
            {
                bytes[(y * stride) + (x * 4) + 3] = 0;
            }
        }

        var frame = new ApiPictureFrame(bytes, drawnFrame.Width, drawnFrame.Height, stride, false, Rotation.R0);
        AssertFound(await ReadAsync(CreateEngine(), frame, "en-US"), "Bluetooth", drawn[0]);
    }

    [OcrLanguageFact]
    public async Task AMissingLanguageIsNotAvailableAndNeverThrows()
    {
        var engine = CreateEngine();
        Assert.Null(await engine.ResolveLanguageAsync("zz-Qqqq", default));
        Assert.Null(await engine.ResolveLanguageAsync("a-b-c-d-e-f", default));
        Assert.Null(await engine.ResolveLanguageAsync("x-1", default));
        var languages = await engine.GetLanguagesAsync(default);
        Assert.All(languages.Available, l => Assert.True(OcrNames.IsWellFormedLanguageTag(l), l));
        if (languages.Default is { } d)
        {
            Assert.Contains(d, languages.Available, StringComparer.OrdinalIgnoreCase);
        }
    }

    [OcrLanguageFact("en-US")]
    public async Task CancellationReachesTheEngine()
    {
        var engine = CreateEngine();
        var tag = (await engine.ResolveLanguageAsync("en-US", default))!;
        var (frame, _) = OcrDrawing.Draw(SKTypeface.Default, ("Cancel", 40, 100));
        var request = new OcrRequest(OcrDrawing.Packed(frame), frame.Width, frame.Height, tag, null);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.RecognizeAsync(request, cts.Token));
    }

    [OcrLanguageFact("en-US")]
    public async Task BoxesStayInsideTheImageAndRepeatedCallsWork()
    {
        var engine = CreateEngine();
        var tag = (await engine.ResolveLanguageAsync("en-US", default))!;

        // Text cut by the right and bottom edges.
        var (frame, _) = OcrDrawing.Draw(SKTypeface.Default, ("Edge of the picture", 420, 290));
        var request = new OcrRequest(OcrDrawing.Packed(frame), frame.Width, frame.Height, tag, null);
        for (var i = 0; i < 3; i++)
        {
            var recognition = await engine.RecognizeAsync(request, default);
            foreach (var box in recognition.Lines.SelectMany(l => l.Words.Select(w => w.Box).Append(l.Box)))
            {
                Assert.True(box.X >= -1 && box.Y >= -1 && box.Right <= frame.Width + 1 && box.Bottom <= frame.Height + 1, $"box {box} outside {frame.Width}x{frame.Height}");
            }
        }
    }

    private static async Task<OcrReading> ReadAsync(IOcrEngine engine, ApiPictureFrame frame, string language)
    {
        var service = new OcrService([engine]);
        var target = await service.ResolveAsync(engine.Info.Id, language, default);
        using var turn = (await service.EnterTurnAsync(default))!;
        return await service.RecognizeAsync(turn, target, frame, null, default);
    }

    private static void AssertFound(OcrReading reading, string text, OcrRect drawn, bool partial = false)
    {
        var matches = TextMatcher.Find(reading.Lines, text, TextMatchMode.Contains, new TextCompareOptions(false, false));
        var match = matches.Count > 0 ? matches[0] : null;
        Assert.True(match is not null, $"'{text}' not found in [{string.Join(" | ", reading.Lines.Select(l => l.Text))}]");

        // The centre is within half the drawn height of the drawn centre (for part of a line: the line is on the drawn row).
        if (partial)
        {
            Assert.True(Math.Abs(match.Box.CenterY - drawn.CenterY) <= drawn.Height / 2, $"{text}: found at {match.Box}, drawn at {drawn}");
            return;
        }

        Assert.True(Math.Abs(match.Box.CenterX - drawn.CenterX) <= drawn.Height && Math.Abs(match.Box.CenterY - drawn.CenterY) <= drawn.Height / 2, $"{text}: found at {match.Box}, drawn at {drawn}");
    }
}

/// <summary>Draws text with Skia for the conformance checks.</summary>
public static class OcrDrawing
{
    /// <summary>The picture's size (a small phone screen region).</summary>
    public const int Width = 600;

    /// <summary>The picture's height.</summary>
    public const int Height = 300;

    /// <summary>The text size: about a phone label at the size a receiver shows it (the host enlarges it before the engine reads it).</summary>
    public const float FontSize = 16;

    /// <summary>A typeface that has kanji and kana, or null when the machine has none.</summary>
    public static SKTypeface? JapaneseTypeface()
    {
        var typeface = SKFontManager.Default.MatchCharacter('設');
        return typeface is not null && typeface.ContainsGlyphs("画面表示と明るさプライバシー") ? typeface : null;
    }

    /// <summary>Black text on white at the given baselines; returns the picture and the drawn boxes.</summary>
    public static (ApiPictureFrame Frame, OcrRect[] Boxes) Draw(SKTypeface typeface, params (string Text, float X, float Baseline)[] texts)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.White);
        using var font = new SKFont(typeface, FontSize);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        List<OcrRect> boxes = [];
        foreach (var (text, x, baseline) in texts)
        {
            surface.Canvas.DrawText(text, x, baseline, font, paint);
            _ = font.MeasureText(text, out var bounds);
            boxes.Add(new OcrRect(x + bounds.Left, baseline + bounds.Top, bounds.Width, bounds.Height));
        }

        var bytes = new byte[Width * Height * 4];
        using (var pixmap = surface.PeekPixels())
        {
            pixmap.GetPixelSpan().CopyTo(bytes);
        }

        return (new ApiPictureFrame(bytes, Width, Height, Width * 4, false, Rotation.R0), [.. boxes]);
    }

    /// <summary>The frame's pixels packed and opaque, as an engine is given them.</summary>
    public static byte[] Packed(ApiPictureFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var bytes = new byte[frame.Width * frame.Height * 4];
        for (var y = 0; y < frame.Height; y++)
        {
            frame.Bgra.AsSpan(y * frame.Stride, frame.Width * 4).CopyTo(bytes.AsSpan(y * frame.Width * 4));
        }

        for (var i = 3; i < bytes.Length; i += 4)
        {
            bytes[i] = 255;
        }

        return bytes;
    }
}
