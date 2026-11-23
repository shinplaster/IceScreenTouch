// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Api;

/// <summary>The machine-readable error codes of the API (docs/API-DESIGN.md, chapter 10).</summary>
public static class ApiErrorCodes
{
    /// <summary>400: the body, a range or a limit is wrong.</summary>
    public const string InvalidRequest = "invalid_request";

    /// <summary>401: the token is missing or wrong.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>403: the Origin or Host header is not allowed.</summary>
    public const string ForbiddenOrigin = "forbidden_origin";

    /// <summary>403: the license does not include the API (trial, expired).</summary>
    public const string PurchaseRequired = "purchase_required";

    /// <summary>409: the mirroring session is not running.</summary>
    public const string NotRunning = "not_running";

    /// <summary>409: the target device has not subscribed to the HID reports.</summary>
    public const string NotConnected = "not_connected";

    /// <summary>409: there is no picture (receiver closed or minimized) and the request needs one.</summary>
    public const string NoPicture = "no_picture";

    /// <summary>409: another operation, a WebSocket session or a probe measurement is running.</summary>
    public const string Busy = "busy";

    /// <summary>409: the user touched the mirror; everything was lifted.</summary>
    public const string UserInterrupted = "user_interrupted";

    /// <summary>409: the user used the mirror within the last second.</summary>
    public const string UserActive = "user_active";

    /// <summary>409: the published HID map has no keyboard.</summary>
    public const string NoKeyboard = "no_keyboard";

    /// <summary>409: the published HID map has no home button.</summary>
    public const string NoHomeButton = "no_home_button";

    /// <summary>
    /// 409: a <c>pixel</c> request refers to a screenshot whose orientation (width above height = landscape) differs
    /// from the current picture: the screen rotated since the screenshot; take a new one. A square <c>ref</c> is never refused.
    /// </summary>
    public const string PictureChanged = "picture_changed";

    /// <summary>500: an unexpected failure inside the application (for example a failed BLE write).</summary>
    public const string InternalError = "internal_error";

    /// <summary>429: screenshots are requested too often.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>499: <c>POST /v1/cancel</c> (or the client going away) ended the operation.</summary>
    public const string Cancelled = "cancelled";

    /// <summary>409: OCR cannot serve the request: no OCR for the language, no such engine, or the engine stopped (docs/API-DESIGN.md 16.5.6).</summary>
    public const string OcrUnavailable = "ocr_unavailable";

    /// <summary>409: <c>tap-text</c> found no (such) match before its time ran out.</summary>
    public const string TextNotFound = "text_not_found";

    /// <summary>409: <c>tap-text</c> with <c>select: unique</c> found more than one match.</summary>
    public const string AmbiguousText = "ambiguous_text";

    /// <summary>409: the <c>offset</c> of <c>tap-text</c> puts the tap outside the screen.</summary>
    public const string TapOutsideScreen = "tap_outside_screen";

    /// <summary>404: no such route.</summary>
    public const string NotFound = "not_found";

    /// <summary>405: the route exists but not for this HTTP method.</summary>
    public const string MethodNotAllowed = "method_not_allowed";

    /// <summary>The HTTP status that goes with <paramref name="code"/>.</summary>
    public static int StatusOf(string code) => code switch
    {
        InvalidRequest => 400,
        Unauthorized => 401,
        NotFound => 404,
        MethodNotAllowed => 405,
        ForbiddenOrigin or PurchaseRequired => 403,
        RateLimited => 429,
        InternalError => 500,
        Cancelled => 499,
        NotRunning or NotConnected or NoPicture or Busy or UserInterrupted or UserActive or NoKeyboard or NoHomeButton or PictureChanged
            or OcrUnavailable or TextNotFound or AmbiguousText or TapOutsideScreen => 409,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "unknown API error code"),
    };
}

/// <summary>An API failure: a machine code, the HTTP status and a human-readable detail (never user data).</summary>
/// <param name="Code">One of <see cref="ApiErrorCodes"/>.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="Detail">A sentence for the person reading the response.</param>
public sealed record ApiError(string Code, int Status, string Detail)
{
    /// <summary>The extension members of the problem response (OCR errors only); null for the others, whose shape does not change.</summary>
    public ApiErrorExtras? Extras { get; init; }

    /// <summary>An error with the status that belongs to <paramref name="code"/>.</summary>
    public static ApiError Create(string code, string detail) => new(code, ApiErrorCodes.StatusOf(code), detail);

    /// <summary>400.</summary>
    public static ApiError InvalidRequest(string detail) => Create(ApiErrorCodes.InvalidRequest, detail);
}

/// <summary>The outcome of an operation without a payload.</summary>
public sealed class ApiResult
{
    private ApiResult(ApiError? error) => Error = error;

    /// <summary>Success.</summary>
    public static ApiResult Success { get; } = new(null);

    /// <summary>Null on success.</summary>
    public ApiError? Error { get; }

    /// <summary>True when the operation was carried out.</summary>
    public bool IsSuccess => Error is null;

    /// <summary>A failed result.</summary>
    public static ApiResult Failure(ApiError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ApiResult(error);
    }

    /// <summary>A successful result with a payload.</summary>
    public static ApiResult<T> Ok<T>(T value) => new(value, null);

    /// <summary>A failed result for an operation with a payload.</summary>
    public static ApiResult<T> Fail<T>(ApiError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ApiResult<T>(default, error);
    }
}

/// <summary>The outcome of an operation with a payload.</summary>
/// <typeparam name="T">The payload.</typeparam>
public sealed class ApiResult<T>
{
    internal ApiResult(T? value, ApiError? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>Null on success.</summary>
    public ApiError? Error { get; }

    /// <summary>The payload (default on failure).</summary>
    public T? Value { get; }

    /// <summary>True when the operation was carried out.</summary>
    public bool IsSuccess => Error is null;


    internal T OrThrow() => Error is null ? Value! : throw new ApiRequestException(Error);

    /// <summary>The same outcome without the payload.</summary>
    public ApiResult WithoutValue() => Error is null ? ApiResult.Success : ApiResult.Failure(Error);
}

/// <summary>Thrown while a request is checked; becomes the failed result (never leaves the controller).</summary>
internal sealed class ApiRequestException(ApiError error) : Exception(error.Detail)
{
    public ApiError Error { get; } = error;
}

/// <summary>
/// The extension members some errors carry in their problem response (docs/API-DESIGN.md 16.5.6). A member that is null is left out. They hold
/// what a script needs to act (the languages and engines there are, the candidates of a match); the <see cref="ApiError.Detail"/> never holds
/// screen text or the searched text.
/// </summary>
public sealed record ApiErrorExtras
{
    /// <summary><c>ocr_unavailable</c>: <c>language</c> or <c>engine</c>.</summary>
    public string? Reason { get; init; }

    /// <summary><c>ocr_unavailable</c>: the engine's id (left out when there is no engine at all).</summary>
    public string? Engine { get; init; }

    /// <summary><c>ocr_unavailable</c>: the languages of that engine.</summary>
    public IReadOnlyList<string>? Available { get; init; }

    /// <summary><c>ocr_unavailable</c>: the ids of the engines that can be used.</summary>
    public IReadOnlyList<string>? Engines { get; init; }

    /// <summary><c>text_not_found</c>, <c>ambiguous_text</c>, <c>tap_outside_screen</c>: the candidates (at most 20).</summary>
    public IReadOnlyList<TextMatchResult>? Matches { get; init; }

    /// <summary>The number of matches (all of them, also beyond the 20 listed).</summary>
    public int? MatchCount { get; init; }
}
