// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;

namespace IceScreenTouch.Api.Tests.Ocr;

public class TextMatcherTests
{
    private static readonly TextCompareOptions Plain = new(false, false);
    private static readonly TextCompareOptions Loose = new(true, false);

    // The spike's counts (SPIKE.md 2.2): exact and contains, with the Japanese recognizer, at the product's scale (x-1: real size, then a
    // 1300 px short side) and at twice the material's size.
    [Theory]
    [InlineData("ja-dark-mirror-uxplay__ja__x-1.00", 8, 8)]
    [InlineData("en-dark-mirror-uxplay__ja__x-1.00", 8, 8)]
    [InlineData("en-dark-typing__ja__x-1.00", 3, 3)]
    [InlineData("ja-dark-typing__ja__x-1.00", 1, 2)]
    [InlineData("ja-dark-mirror-uxplay__ja__x2.00", 8, 8)]
    [InlineData("en-dark-mirror-uxplay__ja__x2.00", 9, 9)]
    [InlineData("en-dark-typing__ja__x2.00", 3, 3)]
    [InlineData("ja-dark-typing__ja__x2.00", 1, 2)]
    public void SpikeRecordingsGiveTheSpikesCounts(string fixture, int exact, int contains)
    {
        var lines = SpikeFixtures.Lines(fixture);
        var truth = SpikeFixtures.Truth(SpikeFixtures.Image(fixture));
        Assert.Equal(exact, truth.Count(t => TextMatcher.Find(lines, t, TextMatchMode.Exact, Plain).Count > 0));
        Assert.Equal(contains, truth.Count(t => TextMatcher.Find(lines, t, TextMatchMode.Contains, Plain).Count > 0));
    }

    [Theory]
    [InlineData("en-dark-typing__ja__x0.76")] // lceScreen
    [InlineData("en-dark-typing__ja__x-1.00")]
    [InlineData("ja-dark-typing__ja__x-1.00")] // キーポード
    [InlineData("ja-dark-typing__ja__x2.00")]
    public void LooseFindsEverySentenceOfTheNotes(string fixture)
    {
        var lines = SpikeFixtures.Lines(fixture);
        var truth = SpikeFixtures.Truth(SpikeFixtures.Image(fixture));
        Assert.Equal(truth.Length, truth.Count(t => TextMatcher.Find(lines, t, TextMatchMode.Contains, Loose).Count > 0));
    }

    [Fact]
    public void MisreadsNeedLoose()
    {
        var lines = SpikeFixtures.Lines("en-dark-typing__ja__x0.76");
        Assert.Empty(TextMatcher.Find(lines, "IceScreen", TextMatchMode.Contains, Plain));
        Assert.Single(TextMatcher.Find(lines, "IceScreen", TextMatchMode.Contains, Loose));
    }

    [Fact]
    public void TheSearchIconReadAsQIsLeftOutOfAnExactMatch()
    {
        foreach (var fixture in new[] { "ja-dark-mirror-uxplay__ja__x-1.00", "en-dark-mirror-uxplay__ja__x-1.00" })
        {
            var match = Assert.Single(TextMatcher.Find(SpikeFixtures.Lines(fixture), "検索", TextMatchMode.Exact, Plain));
            Assert.Equal("検索", match.Text);
            Assert.StartsWith("Q", match.LineText, StringComparison.Ordinal);
            Assert.True(match.WordLevel);
        }
    }

    [Fact]
    public void ExactDoesNotMatchPartOfALine()
    {
        IReadOnlyList<TextLine> lines =
        [
            OcrBuild.Line(100, 30, ("画", 10, 28), ("面", 40, 28), ("表", 70, 28), ("示", 100, 28), ("と", 130, 28), ("設", 160, 28), ("定", 190, 28)),
            OcrBuild.Line(200, 30, ("設", 10, 28), ("定", 40, 28)),
            OcrBuild.Line(300, 30, ("<", 10, 14), ("設", 40, 28), ("定", 70, 28)), // the back button: an isolated character
        ];
        var matches = TextMatcher.Find(lines, "設定", TextMatchMode.Exact, Plain);
        Assert.Equal([1, 2], matches.Select(m => m.LineIndex));
        Assert.All(matches, m => Assert.Equal("設定", m.Text));

        Assert.Equal(3, TextMatcher.Find(lines, "設定", TextMatchMode.Contains, Plain).Count);
    }

    [Fact]
    public void OnlyAFarSingleNonKanjiCharacterIsIsolated()
    {
        // Gap of 0.38 x height (30) = 11.4.
        var near = OcrBuild.Line(0, 30, ("Q", 0, 20), ("検", 31, 28), ("索", 60, 28)); // gap 11: not isolated
        var far = OcrBuild.Line(0, 30, ("Q", 0, 20), ("検", 32, 28), ("索", 61, 28)); // gap 12: isolated
        var kanji = OcrBuild.Line(0, 30, ("大", 0, 28), ("検", 60, 28), ("索", 89, 28)); // far, but a kanji
        var two = OcrBuild.Line(0, 30, ("Qx", 0, 20), ("検", 60, 28), ("索", 89, 28)); // far, but two characters
        Assert.Empty(TextMatcher.Find([near], "検索", TextMatchMode.Exact, Plain));
        Assert.Single(TextMatcher.Find([far], "検索", TextMatchMode.Exact, Plain));
        Assert.Empty(TextMatcher.Find([kanji], "検索", TextMatchMode.Exact, Plain));
        Assert.Empty(TextMatcher.Find([two], "検索", TextMatchMode.Exact, Plain));
    }

    [Fact]
    public void BothEndsCanBeLeftOutAndTheWholeLineIsTriedFirst()
    {
        var line = OcrBuild.Line(0, 30, ("<", 0, 10), ("Back", 40, 60), (">", 140, 10));
        var both = Assert.Single(TextMatcher.Find([line], "Back", TextMatchMode.Exact, Plain));
        Assert.Equal("Back", both.Text);
        Assert.Equal(new OcrRect(40, 0, 60, 30), both.Box);

        // "I Agree": the I is far from Agree, but the whole line is compared first, so it still matches as a whole.
        var agree = OcrBuild.Line(0, 30, ("I", 0, 8), ("Agree", 30, 80));
        Assert.Equal("I Agree", Assert.Single(TextMatcher.Find([agree], "I Agree", TextMatchMode.Exact, Plain)).Text);
        Assert.Equal("Agree", Assert.Single(TextMatcher.Find([agree], "Agree", TextMatchMode.Exact, Plain)).Text);
    }

    [Fact]
    public void ATwoWordLineOfTwoIsolatedCharactersIsNeverEmptied()
    {
        var line = OcrBuild.Line(0, 30, ("<", 0, 10), (">", 100, 10));
        Assert.Single(TextMatcher.Find([line], "<", TextMatchMode.Exact, Plain)); // one end left out
        Assert.Single(TextMatcher.Find([line], "<>", TextMatchMode.Exact, Plain)); // the whole line
    }

    [Fact]
    public void ContainsFindsEveryOccurrenceAndMapsToTheWords()
    {
        var line = OcrBuild.Line(0, 20, ("ab", 0, 20), ("cab", 30, 30), ("x", 70, 10));
        var matches = TextMatcher.Find([line], "ab", TextMatchMode.Contains, Plain);
        Assert.Equal(2, matches.Count);
        Assert.Equal(("ab", new OcrRect(0, 0, 20, 20)), (matches[0].Text, matches[0].Box));
        Assert.Equal(("cab", new OcrRect(30, 0, 30, 20)), (matches[1].Text, matches[1].Box));

        var across = Assert.Single(TextMatcher.Find([line], "bca", TextMatchMode.Contains, Plain));
        Assert.Equal(("ab cab", new OcrRect(0, 0, 60, 20)), (across.Text, across.Box));
    }

    [Fact]
    public void ALineWithoutWordBoxesMatchesAsAWholeLine()
    {
        var box = new OcrRect(10, 10, 200, 30);
        var line = TextLayout.CreateLine("Wi-Fi  MyNetwork  >", box, [], 0.8);
        var contained = Assert.Single(TextMatcher.Find([line], "Wi-Fi", TextMatchMode.Contains, Plain));
        Assert.False(contained.WordLevel);
        Assert.Equal(box, contained.Box);
        Assert.Equal(0.8, contained.Confidence);

        // No isolated-character variants without words: only the whole line.
        Assert.Empty(TextMatcher.Find([line], "Wi-FiMyNetwork", TextMatchMode.Exact, Plain));
    }

    [Fact]
    public void TheConfidenceIsTheLowestOfTheMatchedWords()
    {
        var words = new[]
        {
            new TextWord("Open", new OcrRect(0, 0, 40, 10), 0.9),
            new TextWord("Settings", new OcrRect(50, 0, 80, 10), 0.6),
        };
        var line = TextLayout.CreateLine("", new OcrRect(0, 0, 130, 10), words, null);
        Assert.Equal(0.6, Assert.Single(TextMatcher.Find([line], "Open Settings", TextMatchMode.Exact, Plain)).Confidence);
        Assert.Equal(0.9, Assert.Single(TextMatcher.Find([line], "Open", TextMatchMode.Contains, Plain)).Confidence);
    }

    [Fact]
    public void FragmentsSplitARowAtLargeGaps()
    {
        // A settings row read as one line: "Wi-Fi", then far to the right "MyNetwork" and ">".
        var line = OcrBuild.Line(0, 30, ("Wi-Fi", 40, 60), ("MyNetwork", 400, 120), (">", 540, 10));
        Assert.Empty(TextMatcher.Find([line], "Wi-Fi", TextMatchMode.Exact, Plain));
        var match = Assert.Single(TextMatcher.Find([line], "Wi-Fi", TextMatchMode.Exact, Plain, splitFragments: true));
        Assert.Equal(new OcrRect(40, 0, 60, 30), match.Box);
        Assert.Equal(line.Text, match.LineText);
        Assert.Equal(["Wi-Fi", "MyNetwork >"], TextMatcher.Fragments(line).Select(f => f.Text));
    }

    [Fact]
    public void AnEmptyNormalizedQueryMatchesNothing() =>
        Assert.Empty(TextMatcher.Find([OcrBuild.Line(0, 10, ("a", 0, 10))], " 　", TextMatchMode.Contains, Plain));
}
