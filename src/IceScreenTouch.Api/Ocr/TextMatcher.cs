// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text;

namespace IceScreenTouch.Api.Ocr;

/// <summary>How the searched text must match a line (docs/API-DESIGN.md, 16.6).</summary>
public enum TextMatchMode
{
    /// <summary>The whole line equals the text (an isolated single character at either end may be left out).</summary>
    Exact,

    /// <summary>The line contains the text.</summary>
    Contains,
}

/// <summary>A place where the searched text was found.</summary>
/// <param name="Text">The words that matched, joined as a line's text.</param>
/// <param name="LineText">The whole line.</param>
/// <param name="Box">The matched words' box (the line's when it has no word boxes), in pixels of the picture.</param>
/// <param name="Confidence">The lowest confidence of the matched words, else the line's, else null.</param>
/// <param name="WordLevel">The box is the words' (<c>boxLevel: word</c>); false: the whole line's (<c>boxLevel: line</c>).</param>
/// <param name="LineIndex">The index of the line in the reading order.</param>
public sealed record TextMatch(string Text, string LineText, OcrRect Box, double? Confidence, bool WordLevel, int LineIndex);

/// <summary>
/// Finds the searched text in read lines (docs/API-DESIGN.md, 16.6). Both sides are normalized (<see cref="TextNormalizer"/>) word by word, so a
/// match maps back to the words it covers. The time is linear in the length of the lines (no edit distance, no regular expressions).
/// </summary>
public static class TextMatcher
{
    /// <summary>
    /// A single character at either end of a line is "isolated" (an icon read as a letter, such as the magnifier read as <c>Q</c>) when the gap
    /// to its neighbour is at least this many times the line's height. Measured on few pictures (16.6): adjust with more.
    /// </summary>
    public const double IsolatedGapRatio = 0.38;

    /// <summary>
    /// The gap (times the line's height) that splits a line into fragments for <see cref="Find"/> with <c>splitFragments</c>: a candidate rule for
    /// rows such as a settings list (label, value, chevron) that the recognizer may read as one line (16.6; chosen after the device check).
    /// </summary>
    public const double FragmentGapRatio = 0.8;

    /// <summary>The matches of <paramref name="query"/> (raw text) in <paramref name="lines"/> (reading order), in reading order.</summary>
    /// <param name="lines">The lines.</param>
    /// <param name="query">The searched text, as the request gave it.</param>
    /// <param name="mode">Exact or contains.</param>
    /// <param name="options">Loose and case options.</param>
    /// <param name="splitFragments">Compare <see cref="TextMatchMode.Exact"/> with the fragments of each line (<see cref="Fragments"/>) instead of whole lines.</param>
    public static IReadOnlyList<TextMatch> Find(IReadOnlyList<TextLine> lines, string query, TextMatchMode mode, TextCompareOptions options, bool splitFragments = false)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(query);
        var target = TextNormalizer.Normalize(query, options);
        List<TextMatch> matches = [];
        if (target.Length == 0)
        {
            return matches;
        }

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            if (mode == TextMatchMode.Contains)
            {
                FindContained(line, index, target, options, matches);
            }
            else if (splitFragments)
            {
                foreach (var fragment in Fragments(line))
                {
                    FindExact(fragment, line, index, target, options, matches);
                }
            }
            else
            {
                FindExact(line, line, index, target, options, matches);
            }
        }

        return matches;
    }

    /// <summary>
    /// The line cut where the gap between two words is at least <see cref="FragmentGapRatio"/> times the line's height (a line without word boxes
    /// is one fragment).
    /// </summary>
    public static IReadOnlyList<TextLine> Fragments(TextLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (!line.HasWordBoxes || line.Words.Count < 2)
        {
            return [line];
        }

        List<TextLine> fragments = [];
        var start = 0;
        for (var i = 1; i <= line.Words.Count; i++)
        {
            if (i == line.Words.Count || Gap(line.Words[i - 1], line.Words[i]) >= FragmentGapRatio * line.Box.Height)
            {
                var words = line.Words.Skip(start).Take(i - start).ToList();
                var box = words.Skip(1).Aggregate(words[0].Box, (a, w) => a.Union(w.Box));
                fragments.Add(new TextLine(TextLayout.Join(words.Select(w => w.Text)), box with { Y = line.Box.Y, Height = line.Box.Height }, words, line.Confidence, true));
                start = i;
            }
        }

        return fragments;
    }

    private static void FindExact(TextLine part, TextLine line, int index, string target, TextCompareOptions options, List<TextMatch> matches)
    {
        var normalized = TextNormalizer.NormalizeWords([.. part.Words.Select(w => w.Text)], options);
        var count = part.Words.Count;
        var head = part.HasWordBoxes && count >= 2 && IsIsolated(part.Words[0], part.Words[1], part.Box.Height);
        var tail = part.HasWordBoxes && count >= 2 && IsIsolated(part.Words[^1], part.Words[^2], part.Box.Height);

        // Leave out as little as possible: nothing, then one end, then both (only when a word stays).
        List<(int From, int To)> variants = [(0, count)];
        if (head)
        {
            variants.Add((1, count));
        }

        if (tail)
        {
            variants.Add((0, count - 1));
        }

        if (head && tail && count > 2)
        {
            variants.Add((1, count - 1));
        }

        foreach (var (from, to) in variants)
        {
            if (string.Equals(Slice(normalized, from, to), target, StringComparison.Ordinal))
            {
                matches.Add(Match(part, line, index, from, to));
                return;
            }
        }
    }

    private static void FindContained(TextLine line, int index, string target, TextCompareOptions options, List<TextMatch> matches)
    {
        var normalized = TextNormalizer.NormalizeWords([.. line.Words.Select(w => w.Text)], options);
        var at = 0;
        while (at <= normalized.Text.Length - target.Length)
        {
            var found = normalized.Text.IndexOf(target, at, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            matches.Add(Match(line, line, index, normalized.Owners[found], normalized.Owners[found + target.Length - 1] + 1));
            at = found + target.Length;
        }
    }

    private static TextMatch Match(TextLine part, TextLine line, int index, int from, int to)
    {
        var words = part.Words.Skip(from).Take(to - from).ToList();
        var box = part.HasWordBoxes ? words.Skip(1).Aggregate(words[0].Box, (a, w) => a.Union(w.Box)) : line.Box;
        var confidences = words.Where(w => w.Confidence is not null).Select(w => w.Confidence!.Value).ToList();
        double? confidence = confidences.Count > 0 ? confidences.Min() : line.Confidence;
        var text = part.HasWordBoxes ? TextLayout.Join(words.Select(w => w.Text)) : line.Text;
        return new TextMatch(text, line.Text, box, confidence, part.HasWordBoxes, index);
    }

    // The normalized characters of words [from, to).
    private static string Slice(NormalizedText normalized, int from, int to)
    {
        var builder = new StringBuilder();
        for (var k = 0; k < normalized.Text.Length; k++)
        {
            var owner = normalized.Owners[k];
            if (owner >= from && owner < to)
            {
                _ = builder.Append(normalized.Text[k]);
            }
        }

        return builder.ToString();
    }

    // A single character that is not a kanji, this far from its neighbour (16.6).
    private static bool IsIsolated(TextWord word, TextWord neighbour, double lineHeight)
    {
        var text = word.Text.Trim();
        if (text.Length == 0 || text.EnumerateRunes().Skip(1).Any() || TextLayout.IsKanji(TextLayout.FirstRune(text)))
        {
            return false;
        }

        return Gap(word, neighbour) >= IsolatedGapRatio * lineHeight;
    }

    private static double Gap(TextWord a, TextWord b) =>
        a.Box.X <= b.Box.X ? b.Box.X - a.Box.Right : a.Box.X - b.Box.Right;
}
