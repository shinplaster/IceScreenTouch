// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;

namespace IceScreenTouch.Api.Tests.Ocr;

public class TextNormalizerTests
{
    private static readonly TextCompareOptions Plain = new(Loose: false, CaseSensitive: false);
    private static readonly TextCompareOptions Loose = new(Loose: true, CaseSensitive: false);
    private static readonly TextCompareOptions LooseCase = new(Loose: true, CaseSensitive: true);
    private static readonly TextCompareOptions Case = new(Loose: false, CaseSensitive: true);

    [Theory]
    [InlineData("ＡＢＣ１２３", "ABC123")] // full-width letters and digits
    [InlineData("ﾎﾞﾀﾝ", "ボタン")] // half-width kana become full-width (composed)
    [InlineData("PC keyboard", "PCKEYBOARD")] // white space removed, upper case
    [InlineData("メ モ", "メモ")] // the Japanese recognizer's spaces
    [InlineData("a　b\tc", "ABC")] // ideographic space and tab
    public void PlainNormalizationIsNfkcWithoutWhiteSpace(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.Normalize(input, Plain));

    [Fact]
    public void CaseSensitiveKeepsTheCase() =>
        Assert.Equal("Wi-Fi", TextNormalizer.Normalize("Wi-Fi", Case));

    [Theory]
    [InlineData("キーボード", "キーポード")] // the spike's misread
    [InlineData("ボタン", "ホタン")]
    [InlineData("ポ", "ホ")]
    [InlineData("ゲーム", "ケ一ム")] // voiced mark and long vowel read as the kanji one
    [InlineData("しゃしん", "シヤシン")] // hiragana and small kana
    [InlineData("ロック", "口ツク")] // ロ / 口, small ッ
    [InlineData("カメラ", "力メラ")]
    [InlineData("エラー", "工ラ一")]
    [InlineData("ニュース", "二ユ―ス")]
    [InlineData("タブ", "夕フ")]
    [InlineData("トップ", "卜ツフ")]
    [InlineData("ヘルプ", "へルフ")]
    [InlineData("Wi-Fi", "Wiーfi")] // the hyphen and the long vowel mark
    [InlineData("ㇰ", "ク")] // small katakana extension
    [InlineData("ゝ", "ヽ")]
    public void LooseFoldsWhatTheRecognizerConfuses(string searched, string read) =>
        Assert.Equal(TextNormalizer.Normalize(searched, Loose), TextNormalizer.Normalize(read, Loose));

    [Theory]
    [InlineData("IceScreen", "lceScreen")] // the spike's misread
    [InlineData("I", "1")]
    [InlineData("I", "|")]
    [InlineData("O", "0")]
    public void LooseFoldsLookAlikeLatinInBothCaseModes(string searched, string read)
    {
        Assert.Equal(TextNormalizer.Normalize(searched, Loose), TextNormalizer.Normalize(read, Loose));
        Assert.Equal(TextNormalizer.Normalize(searched, LooseCase), TextNormalizer.Normalize(read, LooseCase));
    }

    [Fact]
    public void WithoutTheCaseOptionLowerCaseIMeetsTheLookAlikesToo() =>
        Assert.Equal(TextNormalizer.Normalize("icescreen", Loose), TextNormalizer.Normalize("lceScreen", Loose));

    [Fact]
    public void LookAlikesAreNotFoldedWithoutLoose()
    {
        Assert.NotEqual(TextNormalizer.Normalize("IceScreen", Plain), TextNormalizer.Normalize("lceScreen", Plain));
        Assert.NotEqual(TextNormalizer.Normalize("キーボード", Plain), TextNormalizer.Normalize("キーポード", Plain));
    }

    [Fact]
    public void TheLookAlikeTableIsDefinedOnNfkcForms()
    {
        // The full-width hyphen-minus becomes '-' under NFKC, which the table folds with the long vowel mark.
        Assert.Equal('-', "－".Normalize(System.Text.NormalizationForm.FormKC)[0]);
        Assert.Equal(TextNormalizer.Normalize("－", Loose), TextNormalizer.Normalize("ー", Loose));
    }

    [Theory]
    [InlineData("ガギグゲゴ", "カキクケコ")]
    [InlineData("パピプペポ", "ハヒフヘホ")]
    [InlineData("ヴ", "ウ")]
    [InlineData("が", "か")]
    public void VoicedMarksAreRemovedThroughNfd(string input, string expected) =>
        Assert.Equal(expected, TextNormalizer.RemoveVoicedMarks(input));

    [Fact]
    public void ADetachedVoicedMarkDisappearsWithLoose()
    {
        // ゛ (U+309B) is a space and a combining mark under NFKC: the space goes, and loose drops the mark.
        Assert.Equal("ホ", TextNormalizer.Normalize("ホ゛", Loose));
    }

    [Fact]
    public void WordsKeepTheirOwnerForEveryCharacter()
    {
        var normalized = TextNormalizer.NormalizeWords(["Ｗｉ", "ﾌｧｲ", "x y"], Plain);
        Assert.Equal("WIファイXY", normalized.Text);
        Assert.Equal([0, 0, 1, 1, 1, 2, 2], normalized.Owners);
    }

    [Fact]
    public void ABrokenSurrogateDoesNotThrow() =>
        Assert.Equal("A�B", TextNormalizer.Normalize("a\uD800b", Plain));
}

public class TextNormalizerCharacterTests
{
    [Theory]
    [InlineData("a￾b")]
    [InlineData("a￿b")]
    [InlineData("a﷐b")]
    [InlineData("􏿿")] // U+10FFFF
    public void NoncharactersBecomeTheReplacementCharacter(string text) =>
        Assert.Contains('�', TextNormalizer.Normalize(text, new TextCompareOptions(Loose: true, CaseSensitive: false)));
}
