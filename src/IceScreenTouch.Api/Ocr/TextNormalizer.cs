// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Buffers;
using System.Text;

namespace IceScreenTouch.Api.Ocr;

/// <summary>How searched text is compared with read text (docs/API-DESIGN.md, 16.6).</summary>
/// <param name="Loose">Treat look-alike characters, voiced marks and the kinds of kana as the same.</param>
/// <param name="CaseSensitive">Tell upper from lower case.</param>
public readonly record struct TextCompareOptions(bool Loose, bool CaseSensitive);

/// <summary>Normalized text and, for each of its characters, the index of the word it came from.</summary>
/// <param name="Text">The normalized text.</param>
/// <param name="Owners">For each character of <paramref name="Text"/>, the index of its word.</param>
public sealed record NormalizedText(string Text, IReadOnlyList<int> Owners);

/// <summary>
/// The normalization applied to the searched text and to each read word before they are compared (docs/API-DESIGN.md, 16.6):
/// <list type="number">
/// <item>Unicode NFKC (full-width and half-width letters, digits and symbols; half-width kana become full-width).</item>
/// <item>Every white space character removed (the Japanese recognizer puts a space between characters).</item>
/// <item>Without <see cref="TextCompareOptions.CaseSensitive"/>: upper case (<see cref="char.ToUpperInvariant"/>, culture independent).</item>
/// <item>With <see cref="TextCompareOptions.Loose"/>: voiced and semi-voiced marks removed (<c>ボ</c>, <c>ポ</c> → <c>ホ</c>), small kana made
/// large, hiragana made katakana, and the look-alikes the recognizer confuses folded into one (<see cref="FoldLookAlike"/>).</item>
/// </list>
/// The case is folded before the look-alikes, and the look-alike table knows both cases, so <c>l</c>, <c>I</c>, <c>1</c> and <c>|</c> meet whatever
/// the case option (and without it, <c>i</c> and <c>L</c> meet them too, as their upper case does).
/// </summary>
public static class TextNormalizer
{
    /// <summary>The normalized form of a whole text (the searched text).</summary>
    public static string Normalize(string text, TextCompareOptions options) => NormalizeWord(text ?? string.Empty, options);

    /// <summary>The words normalized one by one and joined, with the word index of every character (to map a match back to words).</summary>
    public static NormalizedText NormalizeWords(IReadOnlyList<string> words, TextCompareOptions options)
    {
        ArgumentNullException.ThrowIfNull(words);
        var builder = new StringBuilder();
        List<int> owners = [];
        for (var i = 0; i < words.Count; i++)
        {
            var normalized = NormalizeWord(words[i] ?? string.Empty, options);
            _ = builder.Append(normalized);
            for (var k = 0; k < normalized.Length; k++)
            {
                owners.Add(i);
            }
        }

        return new NormalizedText(builder.ToString(), owners);
    }

    private static string NormalizeWord(string text, TextCompareOptions options)
    {
        var s = ReplaceBrokenSurrogates(text).Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (!char.IsWhiteSpace(c))
            {
                _ = builder.Append(options.CaseSensitive ? c : char.ToUpperInvariant(c));
            }
        }

        if (!options.Loose)
        {
            return builder.ToString();
        }

        var withoutMarks = RemoveVoicedMarks(builder.ToString());
        builder.Clear();
        foreach (var c in withoutMarks)
        {
            _ = builder.Append(FoldLookAlike(ToKatakana(ToLargeKana(c)), options.CaseSensitive));
        }

        return builder.ToString();
    }

    /// <summary>Removes the voiced and semi-voiced marks: NFD, without U+3099 and U+309A, back to NFC.</summary>
    public static string RemoveVoicedMarks(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var decomposed = ReplaceBrokenSurrogates(text).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (c is not ('゙' or '゚'))
            {
                _ = builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>A small kana as the large one (<c>ッ</c> → <c>ツ</c>, <c>ぁ</c> → <c>あ</c>); anything else as it is.</summary>
    public static char ToLargeKana(char c) => c switch
    {
        'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' => (char)(c + 1), // ぁぃぅぇぉ
        'っ' or 'ゃ' or 'ゅ' or 'ょ' or 'ゎ' => (char)(c + 1), // っゃゅょゎ
        'ゕ' => 'か', // ゕ → か
        'ゖ' => 'け', // ゖ → け
        'ァ' or 'ィ' or 'ゥ' or 'ェ' or 'ォ' => (char)(c + 1), // ァィゥェォ
        'ッ' or 'ャ' or 'ュ' or 'ョ' or 'ヮ' => (char)(c + 1), // ッャュョヮ
        'ヵ' => 'カ', // ヵ → カ
        'ヶ' => 'ケ', // ヶ → ケ
        >= 'ㇰ' and <= 'ㇿ' => SmallKatakanaExtension[c - 'ㇰ'], // ㇰ..ㇿ
        _ => c,
    };

    /// <summary>A hiragana as the katakana (<c>へ</c> → <c>ヘ</c>, <c>ゝ</c> → <c>ヽ</c>); anything else as it is.</summary>
    public static char ToKatakana(char c) => c switch
    {
        >= 'ぁ' and <= 'ゖ' => (char)(c + 0x60),
        'ゝ' or 'ゞ' => (char)(c + 0x60),
        _ => c,
    };

    /// <summary>
    /// The one character a group of look-alikes the recognizer confuses is folded into (defined on the NFKC forms, after the kana are
    /// folded): the long vowel mark, <c>一</c> and the dashes; <c>ロ口</c>, <c>カ力</c>, <c>エ工</c>, <c>ニ二</c>, <c>タ夕</c>, <c>ト卜</c>;
    /// <c>l I 1 |</c> (and <c>L</c> when the case is folded, since that is the upper case of <c>l</c>); <c>O 0</c>.
    /// </summary>
    public static char FoldLookAlike(char c, bool caseSensitive) => c switch
    {
        'ー' or '一' or '-' or '‐' or '‒' or '–' or '—' or '―' or '−' => 'ー', // ー 一 - ‐ ‒ – — ― −
        '口' => 'ロ', // 口 → ロ
        '力' => 'カ', // 力 → カ
        '工' => 'エ', // 工 → エ
        '二' => 'ニ', // 二 → ニ
        '夕' => 'タ', // 夕 → タ
        '卜' => 'ト', // 卜 → ト
        'I' or '1' or '|' => 'I',
        'l' when caseSensitive => 'I',
        'L' when !caseSensitive => 'I',
        '0' => 'O',
        _ => c,
    };

    private static readonly char[] SmallKatakanaExtension =
    [
        'ク', 'シ', 'ス', 'ト', 'ヌ', 'ハ', 'ヒ', 'フ', // ク シ ス ト ヌ ハ ヒ フ
        'ヘ', 'ホ', 'ム', 'ラ', 'リ', 'ル', 'レ', 'ロ', // ヘ ホ ム ラ リ ル レ ロ
    ];

    // Normalize throws on a lone surrogate and on some noncharacters (U+FFFE); an engine's text or a request may hold them. They become U+FFFD.
    private static string ReplaceBrokenSurrogates(string text)
    {
        if (!NeedsReplacing(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        Span<char> buffer = stackalloc char[2];
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            var status = Rune.DecodeFromUtf16(rest, out var rune, out var consumed);
            var kept = status == OperationStatus.Done && !IsNoncharacter(rune) ? rune : Rune.ReplacementChar;
            _ = builder.Append(buffer[..kept.EncodeToUtf16(buffer)]);
            rest = rest[consumed..];
        }

        return builder.ToString();
    }

    private static bool NeedsReplacing(string text)
    {
        var rest = text.AsSpan();
        while (!rest.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out var consumed) != OperationStatus.Done || IsNoncharacter(rune))
            {
                return true;
            }

            rest = rest[consumed..];
        }

        return false;
    }

    // U+FDD0..U+FDEF and the last two code points of every plane.
    private static bool IsNoncharacter(Rune rune) => rune.Value is >= 0xFDD0 and <= 0xFDEF || (rune.Value & 0xFFFE) == 0xFFFE;
}
