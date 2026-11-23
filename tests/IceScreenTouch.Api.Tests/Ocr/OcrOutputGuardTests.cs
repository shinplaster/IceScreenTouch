// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;

namespace IceScreenTouch.Api.Tests.Ocr;

public class OcrOutputGuardTests
{
    private static OcrWord Word(string text, double x, double y, double w, double h, double? confidence = null) => new(text, new OcrRect(x, y, w, h), confidence);

    [Fact]
    public void BoxesThatStickOutAreCutAndBrokenOnesDropped()
    {
        var line = new OcrLine("a b c d e", new OcrRect(0, 0, 10, 10),
        [
            Word("a", -5, -5, 20, 20), // sticks out: cut
            Word("b", 10, 10, 0, 5), // no width
            Word("c", double.NaN, 0, 5, 5),
            Word("d", 0, 0, double.PositiveInfinity, 5),
            Word("e", 200, 200, 5, 5), // entirely outside
        ], null);
        var result = OcrOutputGuard.Sanitize(new OcrRecognition("ja", [line]), 100, 50);
        var kept = Assert.Single(result.Lines);
        var word = Assert.Single(kept.Words);
        Assert.Equal(("a", new OcrRect(0, 0, 15, 15)), (word.Text, word.Box));
        Assert.Equal(new OcrRect(0, 0, 15, 15), kept.Box);
    }

    [Fact]
    public void ALineWhoseWordsAreAllBrokenIsDroppedButALineOnlyEngineIsKept()
    {
        var broken = new OcrLine("x", new OcrRect(0, 0, 10, 10), [Word("x", 0, 0, -1, 5)], null);
        var lineOnly = new OcrLine("Settings", new OcrRect(5, 5, 50, 10), [], null);
        var noBox = new OcrLine("Nothing", new OcrRect(5, 5, 0, 10), [], null);
        var noText = new OcrLine("  ", new OcrRect(5, 5, 10, 10), [], null);
        var result = OcrOutputGuard.Sanitize(new OcrRecognition("ja", [broken, lineOnly, noBox, noText]), 100, 100);
        Assert.Equal("Settings", Assert.Single(result.Lines).Text);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AConfidenceOutsideZeroToOneBecomesNull(double value)
    {
        var line = new OcrLine("a", new OcrRect(0, 0, 10, 10), [Word("a", 0, 0, 5, 5, value)], value);
        var kept = Assert.Single(OcrOutputGuard.Sanitize(new OcrRecognition("ja", [line]), 100, 100).Lines);
        Assert.Null(kept.Confidence);
        Assert.Null(kept.Words[0].Confidence);
    }

    [Fact]
    public void CountsAndLengthsAreLimited()
    {
        var many = Enumerable.Range(0, 600).Select(i => new OcrLine("w", new OcrRect(0, i, 10, 1), [.. Enumerable.Range(0, 20).Select(_ => Word("w", 0, i, 1, 1))], null)).ToList();
        var result = OcrOutputGuard.Sanitize(new OcrRecognition("ja", many), 100, 1000);
        Assert.True(result.Lines.Count <= OcrOutputGuard.MaxLines);
        Assert.Equal(OcrOutputGuard.MaxWords, result.Lines.Sum(l => l.Words.Count));

        var longText = new string('あ', 999) + "😀" + "x";
        var line = new OcrLine(longText, new OcrRect(0, 0, 10, 10), [Word(longText, 0, 0, 5, 5)], null);
        var kept = Assert.Single(OcrOutputGuard.Sanitize(new OcrRecognition("ja", [line]), 100, 100).Lines);
        Assert.Equal(999, kept.Words[0].Text.Length); // never half a surrogate pair
    }

    [Fact]
    public void AMalformedLanguageBecomesUndeterminedAndNullPartsAreSkipped()
    {
        var result = OcrOutputGuard.Sanitize(new OcrRecognition("ja\nx", [null!, new OcrLine(null!, new OcrRect(0, 0, 5, 5), null!, null)]), 10, 10);
        Assert.Equal("und", result.Language);
        Assert.Empty(result.Lines);
        Assert.Empty(OcrOutputGuard.Sanitize(null, 10, 10).Lines);
    }

    [Fact]
    public void TheEnginesDescriptionIsKeptWithinLimits()
    {
        var hints = new OcrImageHints(1_000_000, 100, 1_000_000, 1_000_000, 100_000);
        var info = new OcrEngineInfo("good-id", "Name\u0007 " + new string('n', 100), "1.0\r\n", OcrEngineKind.External, new OcrCapabilities(true, true, false, hints));
        var safe = OcrOutputGuard.Sanitize(info)!;
        Assert.Equal(64, safe.Name.Length);
        Assert.DoesNotContain('\u0007', safe.Name);
        Assert.Equal("1.0", safe.Version);
        Assert.Equal(new OcrImageHints(4096, 4, 4096, PictureLimits.MaxSide, 256), safe.Capabilities.Image);

        Assert.Equal(1, OcrOutputGuard.Sanitize(info with { Capabilities = info.Capabilities with { Image = new OcrImageHints(10, double.NaN, null, 0, 0) } })!.Capabilities.Image.MaxUpscale);
    }

    [Theory]
    [InlineData("Windows")]
    [InlineData("")]
    [InlineData("a_b")]
    [InlineData("0123456789012345678901234567890123")]
    public void AnEngineWithAMalformedIdIsNotUsed(string id) =>
        Assert.Null(OcrOutputGuard.Sanitize(new OcrEngineInfo(id, "n", "v", OcrEngineKind.BuiltIn, FakeOcrEngine.Capabilities())));

    [Theory]
    [InlineData("ja", true)]
    [InlineData("en-US", true)]
    [InlineData("zh-Hant-TW", true)]
    [InlineData("a-b-c-d-e-f", true)] // well formed here; the engine decides
    [InlineData("-ja", false)]
    [InlineData("ja-", false)]
    [InlineData("ja_JP", false)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789", false)] // 36
    public void LanguageTagsAreCheckedForTheirShape(string tag, bool expected) =>
        Assert.Equal(expected, OcrNames.IsWellFormedLanguageTag(tag));
}
