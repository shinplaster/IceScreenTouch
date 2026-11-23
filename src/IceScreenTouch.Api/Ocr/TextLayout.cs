// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text;

namespace IceScreenTouch.Api.Ocr;

/// <summary>A word of a read line, in pixels of the picture.</summary>
/// <param name="Text">The word as the engine read it.</param>
/// <param name="Box">Its box (for a line without word boxes: the line's box).</param>
/// <param name="Confidence">0..1, or null.</param>
public sealed record TextWord(string Text, OcrRect Box, double? Confidence);

/// <summary>A read line, in pixels of the picture, in reading order.</summary>
/// <param name="Text">The line's text (words joined, see <see cref="TextLayout.Join"/>).</param>
/// <param name="Box">The line's box.</param>
/// <param name="Words">Its words, left to right. A line the engine gave without words has one word: the whole line.</param>
/// <param name="Confidence">The engine's confidence of the line, else the lowest of its words, else null.</param>
/// <param name="HasWordBoxes">The words have their own boxes (false: the only word is the line, <c>boxLevel: line</c>).</param>
public sealed record TextLine(string Text, OcrRect Box, IReadOnlyList<TextWord> Words, double? Confidence, bool HasWordBoxes);

/// <summary>
/// The reading order and the text of lines (docs/API-DESIGN.md, 16.4): lines sorted by their top into rows, rows top to bottom, a row left
/// to right; words joined by a space, except between two wide (kana, kanji, full-width) characters.
/// </summary>
public static class TextLayout
{
    /// <summary>
    /// Builds the lines in reading order. A line joins the current row when its top is less than half its own height below the top of the row's
    /// first line; otherwise it starts a new row. (The rule is applied while walking the lines sorted by top, never as a comparison
    /// function, which would not be transitive.)
    /// </summary>
    public static IReadOnlyList<TextLine> Order(IEnumerable<TextLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        List<TextLine> result = [];
        List<TextLine> row = [];
        double rowTop = 0;
        foreach (var line in lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X))
        {
            if (row.Count > 0 && line.Box.Y - rowTop >= line.Box.Height / 2)
            {
                result.AddRange(row.OrderBy(l => l.Box.X));
                row.Clear();
            }

            if (row.Count == 0)
            {
                rowTop = line.Box.Y;
            }

            row.Add(line);
        }

        result.AddRange(row.OrderBy(l => l.Box.X));
        return result;
    }

    /// <summary>A line of the picture from an engine's line whose boxes are already in pixels of the picture.</summary>
    /// <param name="engineText">The engine's text of the line (used when it has no words).</param>
    /// <param name="box">The line's box.</param>
    /// <param name="words">The words (may be empty).</param>
    /// <param name="confidence">The engine's confidence of the line.</param>
    public static TextLine CreateLine(string engineText, OcrRect box, IReadOnlyList<TextWord> words, double? confidence)
    {
        ArgumentNullException.ThrowIfNull(words);
        if (words.Count == 0)
        {
            return new TextLine(engineText, box, [new TextWord(engineText, box, confidence)], confidence, HasWordBoxes: false);
        }

        var ordered = words.OrderBy(w => w.Box.X).ToList();
        var lowest = ordered.All(w => w.Confidence is not null) ? ordered.Min(w => w.Confidence) : null;
        return new TextLine(Join(ordered.Select(w => w.Text)), box, ordered, confidence ?? lowest, HasWordBoxes: true);
    }

    /// <summary>
    /// The words joined by a space, except between two wide characters (<c>PC の キ ー ボ ー ド</c> becomes <c>PC のキーボード</c>):
    /// the space is left out when the last character of one word and the first of the next are both kana, kanji or full-width symbols.
    /// </summary>
    public static string Join(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var builder = new StringBuilder();
        Rune? last = null;
        foreach (var word in words)
        {
            if (string.IsNullOrEmpty(word))
            {
                continue;
            }

            var first = FirstRune(word);
            if (last is { } previous && !(IsWide(previous) && IsWide(first)))
            {
                _ = builder.Append(' ');
            }

            _ = builder.Append(word);
            last = LastRune(word);
        }

        return builder.ToString();
    }

    /// <summary>A kana, a kanji, or a full-width or CJK symbol (the scripts that write no space between words).</summary>
    public static bool IsWide(Rune rune)
    {
        var v = rune.Value;
        return v is (>= 0x3000 and <= 0x303F) // CJK symbols and punctuation
            or (>= 0x3040 and <= 0x30FF) // hiragana, katakana
            or (>= 0x31F0 and <= 0x31FF) // katakana phonetic extensions
            or (>= 0x3400 and <= 0x4DBF) // CJK extension A
            or (>= 0x4E00 and <= 0x9FFF) // CJK unified ideographs
            or (>= 0xF900 and <= 0xFAFF) // CJK compatibility ideographs
            or (>= 0xFF00 and <= 0xFFEF) // half-width and full-width forms
            or (>= 0x20000 and <= 0x3FFFF); // CJK extensions B and later
    }

    /// <summary>A kanji (CJK ideograph).</summary>
    public static bool IsKanji(Rune rune)
    {
        var v = rune.Value;
        return v is (>= 0x3400 and <= 0x4DBF) or (>= 0x4E00 and <= 0x9FFF) or (>= 0xF900 and <= 0xFAFF) or (>= 0x20000 and <= 0x3FFFF)
            or 0x3005 or 0x3007; // 々 and 〇 are written as kanji
    }

    /// <summary>The first character (U+FFFD for a broken surrogate).</summary>
    public static Rune FirstRune(string text)
    {
        _ = Rune.DecodeFromUtf16(text, out var rune, out _);
        return rune;
    }

    /// <summary>The last character (U+FFFD for a broken surrogate).</summary>
    public static Rune LastRune(string text)
    {
        _ = Rune.DecodeLastFromUtf16(text, out var rune, out _);
        return rune;
    }
}
