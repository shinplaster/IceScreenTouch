// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Api.Tests.Ocr;

/// <summary>
/// A conformance check that needs an OCR language (see <see cref="OcrEngineConformanceTests"/>). This project has no real engine, so the check
/// runs as a plain fact wherever a class derives from the conformance tests here; IceScreenTouch.Windows.Tests has its own attribute that skips
/// when Windows lacks the language.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OcrLanguageFactAttribute(string? language = null) : FactAttribute
{
    /// <summary>The language the check reads, or null when it only needs the engine.</summary>
    public string? Language { get; } = language;
}
