// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;

namespace IceScreenTouch.Api.Ocr;

/// <summary>
/// Checks what an engine returns before the host uses it (docs/API-DESIGN.md, 16.10.1), for the built-in engine as for any other: boxes are cut
/// to the image and broken ones dropped, confidences outside 0..1 become null, the counts and the text length are limited, and the engine's
/// description and image hints are kept within the host's limits.
/// </summary>
public static class OcrOutputGuard
{
    /// <summary>Lines kept from one recognition.</summary>
    public const int MaxLines = 500;

    /// <summary>Words kept from one recognition (over all lines).</summary>
    public const int MaxWords = 5000;

    /// <summary>Characters kept of one text (a line's or a word's).</summary>
    public const int MaxTextLength = 1000;

    /// <summary>Characters kept over all texts of one recognition (the rest is dropped).</summary>
    public const int MaxTotalTextLength = 200_000;

    /// <summary>Lines and words an engine may return that are looked at; anything beyond is ignored unread.</summary>
    public const int MaxExamined = 50_000;

    /// <summary>Characters of an engine's name or version.</summary>
    public const int MaxLabelLength = 64;

    /// <summary>The largest <see cref="OcrImageHints.TargetShortSide"/> and <see cref="OcrImageHints.MaxShortSide"/> the host accepts.</summary>
    public const int MaxHintSide = 4096;

    /// <summary>The largest <see cref="OcrImageHints.MaxUpscale"/>.</summary>
    public const double MaxHintUpscale = 4;

    /// <summary>The largest <see cref="OcrImageHints.MinDimension"/>.</summary>
    public const int MaxHintMinDimension = 256;

    /// <summary>
    /// The engine's description within the host's limits, or null when its id is not <c>[a-z0-9-]{1,32}</c> (such an engine is not used).
    /// Name and version keep at most 64 printable characters; the image hints are cut to the host's limits.
    /// </summary>
    public static OcrEngineInfo? Sanitize(OcrEngineInfo? info)
    {
        if (info is null || !OcrNames.IsWellFormedEngineId(info.Id) || info.Capabilities?.Image is not { } hints)
        {
            return null;
        }

        var maxDimension = Math.Clamp(hints.MaxDimension, 1, PictureLimits.MaxSide);
        var minDimension = Math.Clamp(hints.MinDimension, 1, Math.Min(MaxHintMinDimension, maxDimension));
        var safeHints = new OcrImageHints(
            hints.TargetShortSide is { } target ? Math.Clamp(target, 1, MaxHintSide) : null,
            double.IsFinite(hints.MaxUpscale) ? Math.Clamp(hints.MaxUpscale, 1, MaxHintUpscale) : 1,
            hints.MaxShortSide is { } maxShort ? Math.Clamp(maxShort, 1, MaxHintSide) : null,
            maxDimension,
            minDimension);
        return info with
        {
            Name = Label(info.Name),
            Version = Label(info.Version),
            Capabilities = info.Capabilities with { Image = safeHints },
        };
    }

    /// <summary>
    /// The recognition with every box inside a <paramref name="width"/> x <paramref name="height"/> image: a box that sticks out is cut, one with
    /// a size of 0 or less or a value that is not a number is dropped with its word (a line without a usable box or text is dropped). Confidences
    /// outside 0..1 become null. At most <see cref="MaxLines"/> lines, <see cref="MaxWords"/> words and <see cref="MaxTextLength"/> characters a text.
    /// </summary>
    public static OcrRecognition Sanitize(OcrRecognition? recognition, int width, int height)
    {
        if (recognition?.Lines is not { } lines)
        {
            return new OcrRecognition(OcrNames.Undetermined, []);
        }

        var language = OcrNames.IsWellFormedLanguageTag(recognition.Language) ? recognition.Language : OcrNames.Undetermined;
        List<OcrLine> kept = [];
        var words = 0;
        var characters = 0;
        var examined = 0;
        foreach (var line in lines)
        {
            if (kept.Count >= MaxLines || ++examined > MaxExamined || characters >= MaxTotalTextLength)
            {
                break;
            }

            if (line is null)
            {
                continue;
            }

            List<OcrWord> keptWords = [];
            foreach (var word in line.Words ?? [])
            {
                if (words >= MaxWords || ++examined > MaxExamined)
                {
                    break;
                }

                if (word is null || Clip(word.Box, width, height) is not { } box || Text(word.Text) is not { } text || characters + text.Length > MaxTotalTextLength)
                {
                    continue;
                }

                keptWords.Add(new OcrWord(text, box, Confidence(word.Confidence)));
                words++;
                characters += text.Length;
            }

            var lineBox = Clip(line.Box, width, height);
            var lineText = Text(line.Text);
            if (keptWords.Count > 0)
            {
                // The line's box holds its words (an engine may give none, or one that misses some).
                var union = keptWords.Skip(1).Aggregate(keptWords[0].Box, (a, w) => a.Union(w.Box));
                lineBox = lineBox is { } b ? b.Union(union) : union;
            }
            else if ((line.Words?.Count ?? 0) > 0 || lineBox is null || lineText is null)
            {
                // Every word was broken, or a line-only engine gave no usable box or text.
                continue;
            }

            if (keptWords.Count == 0)
            {
                characters += lineText!.Length; // a line-only engine: the line is the text
            }

            kept.Add(new OcrLine(lineText ?? string.Empty, lineBox!.Value, keptWords, Confidence(line.Confidence)));
        }

        return new OcrRecognition(language, kept);
    }

    private static OcrRect? Clip(OcrRect box, int width, int height)
    {
        if (!double.IsFinite(box.X) || !double.IsFinite(box.Y) || !double.IsFinite(box.Width) || !double.IsFinite(box.Height)
            || box.Width <= 0 || box.Height <= 0)
        {
            return null;
        }

        var left = Math.Max(box.X, 0);
        var top = Math.Max(box.Y, 0);
        var right = Math.Min(box.Right, width);
        var bottom = Math.Min(box.Bottom, height);
        return right > left && bottom > top ? new OcrRect(left, top, right - left, bottom - top) : null;
    }

    private static double? Confidence(double? value) =>
        value is { } v && double.IsFinite(v) && v is >= 0 and <= 1 ? v : null;

    // The text without leading and trailing white space, at most MaxTextLength characters (never half a surrogate pair); null when empty.
    private static string? Text(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.Length > MaxTextLength)
        {
            var cut = MaxTextLength;
            if (char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;
            }

            text = text[..cut];
        }

        return text;
    }

    // Printable characters only (no control or format characters), at most MaxLabelLength.
    private static string Label(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, MaxLabelLength));
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
                or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (builder.Length + rune.Utf16SequenceLength > MaxLabelLength)
            {
                break;
            }

            _ = builder.Append(rune.ToString());
        }

        return builder.ToString().Trim();
    }
}
