// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Platform.Windows;
using Windows.Globalization;
using WinOcrEngine = Windows.Media.Ocr.OcrEngine;

// The namespace of the linked conformance tests, which name this attribute.
namespace IceScreenTouch.Api.Tests.Ocr;

/// <summary>
/// A check that needs Windows.Media.Ocr and, when <c>language</c> is given, its OCR for that language: skipped (not failed) when Windows cannot
/// run OCR, has no OCR for the language, or has no font to draw it (a CI runner may have English only, or no OCR at all).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OcrLanguageFactAttribute : FactAttribute
{
    /// <summary>Skips unless Windows can read <paramref name="language"/> (or run OCR at all, when null) and a font can draw it.</summary>
    public OcrLanguageFactAttribute(string? language = null)
    {
        Language = language;
        if (WindowsMediaOcrEngine.TryCreate() is null)
        {
            Skip = "Windows.Media.Ocr cannot be used on this system";
        }
        else if (language is not null && !WinOcrEngine.IsLanguageSupported(new Language(language)))
        {
            Skip = $"Windows has no OCR for {language} on this machine";
        }
        else if (language?.StartsWith("ja", StringComparison.Ordinal) == true && OcrDrawing.JapaneseTypeface() is null)
        {
            Skip = "no Japanese font to draw the text";
        }
    }

    /// <summary>The language the check reads, or null.</summary>
    public string? Language { get; }
}
