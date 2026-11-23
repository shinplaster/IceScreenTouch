// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Api.Ocr;

/// <summary>
/// A text recognizer (docs/API-DESIGN.md, 16.10.1): it takes an image and a language and returns lines, words and boxes. Everything
/// else (preparing the image, checking the output, the reading order, matching, coordinates, limits) stays outside, in
/// <see cref="OcrService"/>, so an engine can be replaced without changing the REST API.
/// </summary>
/// <remarks>
/// The contract the host relies on (and checks anyway, see <see cref="OcrOutputGuard"/>):
/// <list type="bullet">
/// <item>The image is packed (stride = width * 4), opaque BGRA. The engine reads it only during the call, never writes it and never keeps it.</item>
/// <item>Boxes are axis-aligned rectangles in pixels of the image passed in. Lines may come in any order; a line may have no words.</item>
/// <item><see cref="RecognizeAsync"/> is never called twice at the same time (the host allows one recognition in all), so an engine need not be thread safe.</item>
/// <item>The engine honours the cancellation token where it can; the host does not depend on it (it stops waiting on its own deadline).</item>
/// <item>A well-formed language tag that the engine cannot use is "not available" (null from <see cref="ResolveLanguageAsync"/>), never an exception.</item>
/// </list>
/// </remarks>
public interface IOcrEngine
{
    /// <summary>What the engine is. A snapshot that never blocks.</summary>
    OcrEngineInfo Info { get; }

    /// <summary>The languages the engine can read now, and the one it uses when the request names none.</summary>
    ValueTask<OcrLanguageSet> GetLanguagesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The tag the engine will use for a request (<paramref name="tag"/> null = the default), or null when it has no such language. The host has
    /// already checked that the tag is well formed (<see cref="OcrNames.IsWellFormedLanguageTag"/>).
    /// </summary>
    ValueTask<string?> ResolveLanguageAsync(string? tag, CancellationToken cancellationToken);

    /// <summary>Recognizes the text of <paramref name="request"/>. Throws <see cref="OcrEngineException"/> for the failures it can name.</summary>
    Task<OcrRecognition> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken);
}

/// <summary>One image to recognize.</summary>
/// <param name="Bgra">Packed (stride = width * 4), opaque BGRA pixels; valid only during the call.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Language">A tag returned by <see cref="IOcrEngine.ResolveLanguageAsync"/>.</param>
/// <param name="Region">Pixels of this image to read, for an engine that crops itself (<see cref="OcrCapabilities.Region"/>); null = the whole image.</param>
public sealed record OcrRequest(ReadOnlyMemory<byte> Bgra, int Width, int Height, string Language, OcrRect? Region);

/// <summary>What an engine recognized.</summary>
/// <param name="Language">The language the recognizer used (<c>und</c> when it detected none).</param>
/// <param name="Lines">The lines, in any order; horizontal text only.</param>
public sealed record OcrRecognition(string Language, IReadOnlyList<OcrLine> Lines);

/// <summary>A line as the engine returns it.</summary>
/// <param name="Text">The line's text (a hint: the host rebuilds it from the words when there are any).</param>
/// <param name="Box">The line's box in pixels of the image.</param>
/// <param name="Words">The words with their own boxes; empty for an engine that only returns lines.</param>
/// <param name="Confidence">0..1, or null when the engine gives none.</param>
public sealed record OcrLine(string Text, OcrRect Box, IReadOnlyList<OcrWord> Words, double? Confidence);

/// <summary>A word as the engine returns it.</summary>
/// <param name="Text">The word.</param>
/// <param name="Box">The word's box in pixels of the image.</param>
/// <param name="Confidence">0..1, or null when the engine gives none.</param>
public sealed record OcrWord(string Text, OcrRect Box, double? Confidence);

/// <summary>An axis-aligned rectangle in pixels (top left and size).</summary>
/// <param name="X">Left.</param>
/// <param name="Y">Top.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct OcrRect(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge.</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>The centre's x.</summary>
    public double CenterX => X + (Width / 2);

    /// <summary>The centre's y.</summary>
    public double CenterY => Y + (Height / 2);

    /// <summary>The smallest rectangle that holds both.</summary>
    public OcrRect Union(OcrRect other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new OcrRect(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }
}

/// <summary>The languages of an engine.</summary>
/// <param name="Available">The tags the engine can read now (may be empty).</param>
/// <param name="Default">The tag used when a request names none, or null when there is none.</param>
public sealed record OcrLanguageSet(IReadOnlyList<string> Available, string? Default);

/// <summary>Whether the engine is part of the application or one the user runs.</summary>
public enum OcrEngineKind
{
    /// <summary>Shipped with the application.</summary>
    BuiltIn,

    /// <summary>Run by the user outside the application (not in 0.4.0).</summary>
    External,
}

/// <summary>What an engine is.</summary>
/// <param name="Id">Stable id used in the API, <c>[a-z0-9-]{1,32}</c> (for example <c>windows</c>).</param>
/// <param name="Name">Display name (for example <c>Windows.Media.Ocr</c>).</param>
/// <param name="Version">Free text; Windows: the OS build.</param>
/// <param name="Kind">Built in or external.</param>
/// <param name="Capabilities">What it can do.</param>
public sealed record OcrEngineInfo(string Id, string Name, string Version, OcrEngineKind Kind, OcrCapabilities Capabilities);

/// <summary>What an engine can do.</summary>
/// <param name="WordBoxes">Words come with their own boxes (false: line boxes only).</param>
/// <param name="Confidence">Lines or words come with a confidence.</param>
/// <param name="Region">The engine takes <see cref="OcrRequest.Region"/> itself (false: the host crops before the call).</param>
/// <param name="Image">The image size the engine prefers.</param>
public sealed record OcrCapabilities(bool WordBoxes, bool Confidence, bool Region, OcrImageHints Image);

/// <summary>The image size an engine prefers (docs/API-DESIGN.md, 16.10.2). The host scales the picture to it.</summary>
/// <param name="TargetShortSide">Enlarge the image until its short side is this long (null: never enlarge).</param>
/// <param name="MaxUpscale">Enlarge at most this many times.</param>
/// <param name="MaxShortSide">Shrink an image whose short side is longer than this (null: no such limit).</param>
/// <param name="MaxDimension">The longest side the engine accepts; the image is shrunk to fit.</param>
/// <param name="MinDimension">The shortest side the engine accepts; a smaller image is padded.</param>
public sealed record OcrImageHints(int? TargetShortSide, double MaxUpscale, int? MaxShortSide, int MaxDimension, int MinDimension);

/// <summary>The failures an engine can name (docs/API-DESIGN.md, 16.10.1).</summary>
public enum OcrFailure
{
    /// <summary>The engine has no such language (409 <c>ocr_unavailable</c>, reason <c>language</c>).</summary>
    LanguageUnavailable,

    /// <summary>The engine cannot be used now (409 <c>ocr_unavailable</c>, reason <c>engine</c>).</summary>
    EngineUnavailable,

    /// <summary>The engine is busy (429 <c>rate_limited</c>).</summary>
    Busy,

    /// <summary>The recognition failed (500).</summary>
    Failed,
}

/// <summary>A failure an engine names. The message is fixed English text: never screen content or the text that was searched for.</summary>
public sealed class OcrEngineException : Exception
{
    /// <summary>Creates the exception.</summary>
    public OcrEngineException(OcrFailure failure)
        : base(MessageOf(failure)) => Failure = failure;

    /// <summary>Creates the exception with the cause (kept for debugging; its message is never shown to clients).</summary>
    public OcrEngineException(OcrFailure failure, Exception innerException)
        : base(MessageOf(failure), innerException) => Failure = failure;

    /// <summary>What failed.</summary>
    public OcrFailure Failure { get; }

    private static string MessageOf(OcrFailure failure) => failure switch
    {
        OcrFailure.LanguageUnavailable => "the OCR language is not available",
        OcrFailure.EngineUnavailable => "the OCR engine is not available",
        OcrFailure.Busy => "the OCR engine is busy",
        _ => "the text recognition failed",
    };
}

/// <summary>The shapes of the names the OCR API takes (engine ids, language tags).</summary>
public static class OcrNames
{
    /// <summary>The longest language tag.</summary>
    public const int MaxLanguageTagLength = 35;

    /// <summary>The longest engine id.</summary>
    public const int MaxEngineIdLength = 32;

    /// <summary>The language tag of an engine that detects the language itself ("undetermined" in BCP 47).</summary>
    public const string Undetermined = "und";

    /// <summary>1 to 35 characters of <c>[A-Za-z0-9-]</c>, not starting or ending with <c>-</c> (docs/API-DESIGN.md, 16.3).</summary>
    public static bool IsWellFormedLanguageTag(string? tag) =>
        tag is { Length: > 0 and <= MaxLanguageTagLength } && tag[0] != '-' && tag[^1] != '-' && tag.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    /// <summary>1 to 32 characters of <c>[a-z0-9-]</c>.</summary>
    public static bool IsWellFormedEngineId(string? id) =>
        id is { Length: > 0 and <= MaxEngineIdLength } && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
}
