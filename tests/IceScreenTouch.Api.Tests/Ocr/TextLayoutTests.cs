// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;

namespace IceScreenTouch.Api.Tests.Ocr;

public class TextLayoutTests
{
    [Theory]
    [InlineData(new[] { "PC", "の", "キ", "ー", "ボ", "ー", "ド" }, "PC のキーボード")]
    [InlineData(new[] { "IceScreen", "Touch", "で", "、" }, "IceScreen Touch で、")]
    [InlineData(new[] { "22", ":", "23" }, "22 : 23")]
    [InlineData(new[] { "Wi-Fi" }, "Wi-Fi")]
    [InlineData(new[] { "Ｆｕｌｌ", "幅" }, "Ｆｕｌｌ幅")] // full-width letters are wide
    public void WordsAreJoinedWithSpacesExceptBetweenWideCharacters(string[] words, string expected) =>
        Assert.Equal(expected, TextLayout.Join(words));

    [Fact]
    public void LinesAreSortedIntoRowsThenLeftToRight()
    {
        // Two labels on a row whose tops differ a little, a label of the next row, and one far below that came first.
        var right = OcrBuild.Line(102, 30, ("天気", 300, 60));
        var left = OcrBuild.Line(100, 30, ("メモ", 100, 60));
        var next = OcrBuild.Line(200, 30, ("写真", 100, 60));
        var bottom = OcrBuild.Line(900, 30, ("検索", 200, 60));
        var ordered = TextLayout.Order([bottom, right, next, left]);
        Assert.Equal(["メモ", "天気", "写真", "検索"], ordered.Select(l => l.Text));
    }

    [Fact]
    public void ALineHalfItsHeightBelowTheRowStartsANewRow()
    {
        var first = OcrBuild.Line(100, 20, ("B", 300, 20));
        var lower = OcrBuild.Line(110, 20, ("A", 100, 20)); // exactly half its height below: a new row
        Assert.Equal(["B", "A"], TextLayout.Order([lower, first]).Select(l => l.Text));

        var near = OcrBuild.Line(109, 20, ("A", 100, 20)); // less than half: the same row, so left first
        Assert.Equal(["A", "B"], TextLayout.Order([first, near]).Select(l => l.Text));
    }

    [Fact]
    public void RowsAreMeasuredFromTheirFirstLineSoASlopeCannotChain()
    {
        // Each line is a little lower than the one before; chained, they would all be one row.
        var lines = Enumerable.Range(0, 5).Select(i => OcrBuild.Line(100 + (i * 6), 20, ($"w{i}", 500 - (i * 100), 40))).ToList();
        var ordered = TextLayout.Order(lines).Select(l => l.Text).ToList();
        Assert.Equal(["w1", "w0", "w3", "w2", "w4"], ordered);
    }

    [Fact]
    public void WordsAreSortedLeftToRightAndTheLowestConfidenceIsTheLines()
    {
        var words = new[]
        {
            new TextWord("b", new OcrRect(50, 0, 10, 10), 0.9),
            new TextWord("a", new OcrRect(10, 0, 10, 10), 0.5),
        };
        var line = TextLayout.CreateLine("x", new OcrRect(10, 0, 50, 10), words, null);
        Assert.Equal("a b", line.Text);
        Assert.Equal(0.5, line.Confidence);
        Assert.True(line.HasWordBoxes);
    }

    [Fact]
    public void ALineWithoutWordsIsOneWordWithTheLineBox()
    {
        var box = new OcrRect(1, 2, 30, 10);
        var line = TextLayout.CreateLine("Settings", box, [], 0.7);
        Assert.False(line.HasWordBoxes);
        var word = Assert.Single(line.Words);
        Assert.Equal(("Settings", box, 0.7), (word.Text, word.Box, word.Confidence));
    }
}
