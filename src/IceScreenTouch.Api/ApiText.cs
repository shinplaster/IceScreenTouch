// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace IceScreenTouch.Api;

// The request and response types of the text (OCR) endpoints (docs/API-DESIGN.md, 16.5). Optional values that are null are left out of the
// responses (confidence, words), so an engine without confidences adds nothing.

/// <summary>A rectangle in the request's space (<c>screen</c> 0..1 or <c>pixel</c> of <c>ref</c>).</summary>
public sealed record TextRegion
{
    /// <summary>Left.</summary>
    public required double X { get; init; }

    /// <summary>Top.</summary>
    public required double Y { get; init; }

    /// <summary>Width (more than 0).</summary>
    public required double Width { get; init; }

    /// <summary>Height (more than 0).</summary>
    public required double Height { get; init; }
}

/// <summary><c>POST /v1/text/read</c>.</summary>
public sealed record TextReadRequest
{
    /// <summary>The BCP 47 language tag (null: the engine's default).</summary>
    public string? Lang { get; init; }

    /// <summary>The engine's id (null: the default engine).</summary>
    public string? Engine { get; init; }

    /// <summary><c>screen</c> (default) or <c>pixel</c>.</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size; required for <c>pixel</c>.</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>Only this part of the screen (lines whose centre is inside it).</summary>
    public TextRegion? Region { get; init; }

    /// <summary>Add the words of every line (default false).</summary>
    public bool? Words { get; init; }
}

/// <summary><c>POST /v1/text/find</c>.</summary>
public sealed record TextFindRequest
{
    /// <summary>The text to find (1 to 200 characters).</summary>
    public required string Text { get; init; }

    /// <summary><c>exact</c> (default) or <c>contains</c>.</summary>
    public string? Match { get; init; }

    /// <summary>Treat look-alike characters, voiced marks and the kinds of kana as the same (default false).</summary>
    public bool? Loose { get; init; }

    /// <summary>Tell upper from lower case (default false).</summary>
    public bool? CaseSensitive { get; init; }

    /// <summary>How long to wait for the text, 0 to 30000 ms (default 0: read once).</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>The pause between readings while waiting, 300 to 5000 ms (default 500).</summary>
    public int? PollMs { get; init; }

    /// <summary>The BCP 47 language tag (null: the engine's default).</summary>
    public string? Lang { get; init; }

    /// <summary>The engine's id (null: the default engine).</summary>
    public string? Engine { get; init; }

    /// <summary><c>screen</c> (default) or <c>pixel</c>.</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size; required for <c>pixel</c>.</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>Only this part of the screen.</summary>
    public TextRegion? Region { get; init; }
}

/// <summary><c>POST /v1/touch/tap-text</c>.</summary>
public sealed record TapTextRequest
{
    /// <summary>The text to find (1 to 200 characters).</summary>
    public required string Text { get; init; }

    /// <summary><c>exact</c> (default) or <c>contains</c>.</summary>
    public string? Match { get; init; }

    /// <summary>Treat look-alike characters, voiced marks and the kinds of kana as the same (default false).</summary>
    public bool? Loose { get; init; }

    /// <summary>Tell upper from lower case (default false).</summary>
    public bool? CaseSensitive { get; init; }

    /// <summary>How long to wait for the text, 0 to 30000 ms (default 0).</summary>
    public int? TimeoutMs { get; init; }

    /// <summary>The pause between readings while waiting, 300 to 5000 ms (default 500).</summary>
    public int? PollMs { get; init; }

    /// <summary><c>unique</c> (default), <c>first</c> or <c>index</c>.</summary>
    public string? Select { get; init; }

    /// <summary>The match to tap for <c>select: index</c> (0 = the first in reading order).</summary>
    public int? Index { get; init; }

    /// <summary>Tap this far from the centre of the match (<c>screen</c>: -1..1, <c>pixel</c>: within the <c>ref</c> size).</summary>
    public ApiPoint? Offset { get; init; }

    /// <summary>The pause between finding the text and the reading that confirms it, 0 to 2000 ms (default 250; 0 = no confirmation).</summary>
    public int? SettleMs { get; init; }

    /// <summary>How long the finger stays down (default 80, at least 70).</summary>
    public int? HoldMs { get; init; }

    /// <summary>Number of taps, 1 to 10 (default 1).</summary>
    public int? Count { get; init; }

    /// <summary>Pause between taps (default 120, at least 25).</summary>
    public int? IntervalMs { get; init; }

    /// <summary>The BCP 47 language tag (null: the engine's default).</summary>
    public string? Lang { get; init; }

    /// <summary>The engine's id (null: the default engine).</summary>
    public string? Engine { get; init; }

    /// <summary><c>screen</c> (default) or <c>pixel</c>.</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size; required for <c>pixel</c>.</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>Only this part of the screen.</summary>
    public TextRegion? Region { get; init; }
}

/// <summary>A rectangle of a response, in the request's space.</summary>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public sealed record TextBox(double X, double Y, double Width, double Height);

/// <summary>A point of a response, in the request's space.</summary>
/// <param name="X">Horizontal.</param>
/// <param name="Y">Vertical.</param>
public sealed record TextPoint(double X, double Y);

/// <summary>The picture the text was read from (as the headers of <c>GET /v1/screenshot</c> describe it).</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Orientation"><c>portrait</c> or <c>landscape</c>.</param>
/// <param name="Rotation">0, 90, 180 or 270.</param>
public sealed record TextPicture(int Width, int Height, string Orientation, int Rotation);

/// <summary>The engine that read the text.</summary>
/// <param name="Id">Its id (<c>windows</c>).</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">Its version.</param>
public sealed record TextEngine(string Id, string Name, string Version);

/// <summary>A word of a read line.</summary>
/// <param name="Text">The word.</param>
/// <param name="Box">Its box.</param>
/// <param name="Confidence">0..1, left out when the engine gives none.</param>
public sealed record TextWordResult(
    string Text,
    TextBox Box,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Confidence = null);

/// <summary>A read line.</summary>
/// <param name="Text">The line.</param>
/// <param name="Box">Its box.</param>
/// <param name="Center">The centre of the box.</param>
/// <param name="Confidence">0..1, left out when the engine gives none.</param>
/// <param name="Words">The words, only when the request asked for them.</param>
public sealed record TextLineResult(
    string Text,
    TextBox Box,
    TextPoint Center,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Confidence = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<TextWordResult>? Words = null);

/// <summary>A place where the text was found.</summary>
/// <param name="Text">The words that matched.</param>
/// <param name="LineText">The whole line.</param>
/// <param name="Box">The box of the words (of the line when <paramref name="BoxLevel"/> is <c>line</c>).</param>
/// <param name="Center">The centre of the box.</param>
/// <param name="BoxLevel"><c>word</c> or <c>line</c> (an engine without word boxes: the box is the whole line's).</param>
/// <param name="Confidence">0..1, left out when the engine gives none.</param>
public sealed record TextMatchResult(
    string Text,
    string LineText,
    TextBox Box,
    TextPoint Center,
    string BoxLevel,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Confidence = null);

/// <summary>The match that was tapped and where.</summary>
/// <param name="Text">The words that matched.</param>
/// <param name="LineText">The whole line.</param>
/// <param name="Box">The box.</param>
/// <param name="Center">The centre of the box.</param>
/// <param name="BoxLevel"><c>word</c> or <c>line</c>.</param>
/// <param name="At">Where the finger went down (the centre plus the offset).</param>
/// <param name="Confidence">0..1, left out when the engine gives none.</param>
public sealed record TextTapped(
    string Text,
    string LineText,
    TextBox Box,
    TextPoint Center,
    string BoxLevel,
    TextPoint At,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Confidence = null);

/// <summary>The response of <c>POST /v1/text/read</c>.</summary>
/// <param name="Language">The language the recognizer used.</param>
/// <param name="Engine">The engine.</param>
/// <param name="Picture">The picture that was read.</param>
/// <param name="Space">The space of the boxes.</param>
/// <param name="DurationMs">Capture and recognition.</param>
/// <param name="Lines">The lines in reading order.</param>
public sealed record TextReadResponse(string Language, TextEngine Engine, TextPicture Picture, string Space, long DurationMs, IReadOnlyList<TextLineResult> Lines);

/// <summary>The response of <c>POST /v1/text/find</c>.</summary>
/// <param name="Language">The language the recognizer used.</param>
/// <param name="Engine">The engine.</param>
/// <param name="Picture">The last picture that was read.</param>
/// <param name="Space">The space of the boxes.</param>
/// <param name="DurationMs">From the request to the answer.</param>
/// <param name="Attempts">The readings made.</param>
/// <param name="Matches">The matches in reading order (empty when the text was not found).</param>
public sealed record TextFindResponse(string Language, TextEngine Engine, TextPicture Picture, string Space, long DurationMs, int Attempts, IReadOnlyList<TextMatchResult> Matches);

/// <summary>The response of <c>POST /v1/touch/tap-text</c>.</summary>
/// <param name="Language">The language the recognizer used.</param>
/// <param name="Engine">The engine.</param>
/// <param name="Picture">The picture the tap was decided on.</param>
/// <param name="Space">The space of the boxes.</param>
/// <param name="DurationMs">From the request to the lift.</param>
/// <param name="Attempts">The readings made.</param>
/// <param name="Tapped">The match that was tapped.</param>
/// <param name="MatchCount">The matches on that picture.</param>
public sealed record TapTextResponse(string Language, TextEngine Engine, TextPicture Picture, string Space, long DurationMs, int Attempts, TextTapped Tapped, int MatchCount);

/// <summary>The OCR part of <c>GET /v1/status</c>.</summary>
/// <param name="Available">Text can be read now (the default engine works and has a language).</param>
/// <param name="Engine">The default engine, or null when there is none.</param>
/// <param name="Engines">Every engine (id, name, kind, whether it can be used).</param>
/// <param name="Languages">The default engine's languages (read again on every request).</param>
/// <param name="DefaultLanguage">The language used when a request names none.</param>
public sealed record OcrStatusInfo(bool Available, OcrEngineDetail? Engine, IReadOnlyList<OcrEngineSummary> Engines, IReadOnlyList<string> Languages, string? DefaultLanguage);

/// <summary>The default engine of the status.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Version">Its version.</param>
/// <param name="Kind"><c>builtin</c> or <c>external</c>.</param>
/// <param name="Capabilities">What it gives.</param>
public sealed record OcrEngineDetail(string Id, string Name, string Version, string Kind, OcrEngineCapabilities Capabilities);

/// <summary>What an engine gives.</summary>
/// <param name="WordBoxes">Words have their own boxes (false: matches are located by whole lines).</param>
/// <param name="Confidence">Results carry a confidence.</param>
public sealed record OcrEngineCapabilities(bool WordBoxes, bool Confidence);

/// <summary>One engine of the status.</summary>
/// <param name="Id">Its id.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind"><c>builtin</c> or <c>external</c>.</param>
/// <param name="Available">It can be used now.</param>
public sealed record OcrEngineSummary(string Id, string Name, string Kind, bool Available);
