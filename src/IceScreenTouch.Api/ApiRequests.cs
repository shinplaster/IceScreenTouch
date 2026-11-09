// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Api;

// Plain request and response types. The property names (camel-cased) are the JSON names of
// docs/API-DESIGN.md chapter 7; the server binds them from JSON. Omitted optional values take the defaults of
// ApiController (DefaultTapHoldMs and so on).

/// <summary>A position in the request's coordinate space.</summary>
public sealed record ApiPoint
{
    /// <summary>Horizontal position.</summary>
    public required double X { get; init; }

    /// <summary>Vertical position.</summary>
    public required double Y { get; init; }
}

/// <summary>The size of the screenshot a <c>pixel</c> position refers to.</summary>
public sealed record PixelRef
{
    /// <summary>Screenshot width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Screenshot height in pixels.</summary>
    public required int Height { get; init; }
}

/// <summary><c>POST /v1/touch/tap</c>.</summary>
public sealed record TapRequest
{
    /// <summary>Horizontal position.</summary>
    public required double X { get; init; }

    /// <summary>Vertical position.</summary>
    public required double Y { get; init; }

    /// <summary><c>screen</c> (default), <c>pixel</c> or <c>device</c>.</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size; required for <c>pixel</c>.</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>How long the finger stays down (default 80, at least 70).</summary>
    public int? HoldMs { get; init; }

    /// <summary>Number of taps, 1 to <see cref="ApiController.MaxTapCount"/> (default 1).</summary>
    public int? Count { get; init; }

    /// <summary>Pause between a lift and the next tap (default 120, at least 25).</summary>
    public int? IntervalMs { get; init; }
}

/// <summary><c>POST /v1/touch/long-press</c>.</summary>
public sealed record LongPressRequest
{
    /// <summary>Horizontal position.</summary>
    public required double X { get; init; }

    /// <summary>Vertical position.</summary>
    public required double Y { get; init; }

    /// <summary><c>screen</c> (default), <c>pixel</c> or <c>device</c>.</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size; required for <c>pixel</c>.</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>How long the finger stays down (default 800, at least 70).</summary>
    public int? HoldMs { get; init; }
}

/// <summary><c>POST /v1/touch/swipe</c>.</summary>
public sealed record SwipeRequest
{
    /// <summary>Where the finger goes down.</summary>
    public required ApiPoint From { get; init; }

    /// <summary>Where the finger is lifted.</summary>
    public required ApiPoint To { get; init; }

    /// <summary><c>screen</c> (default), <c>pixel</c> or <c>device</c>.</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size; required for <c>pixel</c>.</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>The time from <c>from</c> to <c>to</c> (default 300).</summary>
    public int? DurationMs { get; init; }

    /// <summary>The finger waits at <c>from</c> this long first (a long press before the drag; default 0).</summary>
    public int? HoldBeforeMs { get; init; }

    /// <summary>The finger waits at <c>to</c> this long before the lift (stops the momentum; default 0).</summary>
    public int? HoldAfterMs { get; init; }
}

/// <summary>One point of a path.</summary>
public sealed record PathPoint
{
    /// <summary>Horizontal position.</summary>
    public required double X { get; init; }

    /// <summary>Vertical position.</summary>
    public required double Y { get; init; }

    /// <summary>Milliseconds since the finger went down; not decreasing from point to point.</summary>
    public required int T { get; init; }
}

/// <summary><c>POST /v1/touch/path</c>.</summary>
public sealed record PathRequest
{
    /// <summary><c>screen</c> (default), <c>pixel</c> or <c>device</c>.</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size; required for <c>pixel</c>.</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>1 to <see cref="ApiController.MaxPathPoints"/> points.</summary>
    public required IReadOnlyList<PathPoint> Points { get; init; }
}

/// <summary><c>POST /v1/touch/pinch</c>.</summary>
public sealed record PinchRequest
{
    /// <summary>The point between the two fingers.</summary>
    public required ApiPoint Center { get; init; }

    /// <summary><c>screen</c> (default), <c>pixel</c> or <c>device</c>.</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size; required for <c>pixel</c>.</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>Half the distance between the fingers at the start, as a fraction of the screen's short side.</summary>
    public required double StartRadius { get; init; }

    /// <summary>Half the distance between the fingers at the end, as a fraction of the screen's short side.</summary>
    public required double EndRadius { get; init; }

    /// <summary>The time from start to end (default 400).</summary>
    public int? DurationMs { get; init; }
}

/// <summary><c>POST /v1/keys/press</c>.</summary>
public sealed record KeyPressRequest
{
    /// <summary>The key name (docs/API-DESIGN.md chapter 12).</summary>
    public required string Key { get; init; }

    /// <summary><c>cmd</c>, <c>shift</c>, <c>option</c>, <c>ctrl</c>.</summary>
    public IReadOnlyList<string>? Modifiers { get; init; }

    /// <summary>How long the key stays down (default 30).</summary>
    public int? HoldMs { get; init; }
}

/// <summary><c>POST /v1/keys/type</c>.</summary>
public sealed record TypeTextRequest
{
    /// <summary>The text; at most <see cref="ApiController.MaxTextLength"/> characters.</summary>
    public required string Text { get; init; }

    /// <summary>The pause after each character (default 20).</summary>
    public int? IntervalMs { get; init; }
}

/// <summary>The response of <c>POST /v1/keys/type</c>.</summary>
/// <param name="Skipped">The characters that have no key on a US keyboard (not typed).</param>
public sealed record TypeTextResponse(string Skipped);

/// <summary><c>PUT /v1/rotation</c>. At least one of the two.</summary>
public sealed record RotationRequest
{
    /// <summary>90 or 270.</summary>
    public int? Landscape { get; init; }

    /// <summary>0 or 180.</summary>
    public int? Portrait { get; init; }
}

/// <summary><c>PUT /v1/target</c>.</summary>
public sealed record TargetRequest
{
    /// <summary>The device id, or null for automatic. The member must be present (a missing one is an error, null is not).</summary>
    public required string? DeviceId { get; init; }
}
