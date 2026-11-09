// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>The coordinate spaces of the API (docs/API-DESIGN.md, chapter 6).</summary>
public enum CoordinateSpace
{
    /// <summary>0..1 of the screen as it is seen now; the controller un-rotates it.</summary>
    Screen,

    /// <summary>Pixels of a screenshot of the given <see cref="PixelRef"/> size.</summary>
    Pixel,

    /// <summary>0..1 in the digitizer's own (portrait) frame; no rotation, no picture needed.</summary>
    Device,
}

/// <summary>
/// A coordinate space as a request names it, checked but not yet tied to the state of the application. Turns a
/// position of the request into the unit square of that space (range checks included).
/// </summary>
public sealed class CoordinateSpec
{
    private CoordinateSpec(CoordinateSpace space, PixelRef? reference)
    {
        Space = space;
        Reference = reference;
    }

    /// <summary>The space.</summary>
    public CoordinateSpace Space { get; }

    /// <summary>The screenshot size (only for <see cref="CoordinateSpace.Pixel"/>).</summary>
    public PixelRef? Reference { get; }

    /// <summary>The conversion needs the picture (to know its orientation).</summary>
    public bool NeedsPicture => Space != CoordinateSpace.Device;

    /// <summary>Parses the <c>space</c> and <c>ref</c> of a request (a null space is <c>screen</c>).</summary>
    public static ApiResult<CoordinateSpec> Parse(string? space, PixelRef? reference)
    {
        var parsed = space?.ToUpperInvariant() switch
        {
            null or "SCREEN" => CoordinateSpace.Screen,
            "PIXEL" => CoordinateSpace.Pixel,
            "DEVICE" => CoordinateSpace.Device,
            _ => (CoordinateSpace?)null,
        };
        if (parsed is not { } s)
        {
            return ApiResult.Fail<CoordinateSpec>(ApiError.InvalidRequest("space must be screen, pixel or device"));
        }

        if (s != CoordinateSpace.Pixel)
        {
            return ApiResult.Ok<CoordinateSpec>(new CoordinateSpec(s, null));
        }

        if (reference is null)
        {
            return ApiResult.Fail<CoordinateSpec>(ApiError.InvalidRequest("space pixel needs ref {width, height}"));
        }

        if (reference.Width <= 0 || reference.Height <= 0)
        {
            return ApiResult.Fail<CoordinateSpec>(ApiError.InvalidRequest("ref width and height must be positive"));
        }

        return ApiResult.Ok<CoordinateSpec>(new CoordinateSpec(s, reference));
    }

    /// <summary>
    /// The position as 0..1 in this space's unit square. Out of range (or not a number) is
    /// <c>invalid_request</c>: x in [0, width] and y in [0, height] for pixels, [0, 1] otherwise.
    /// </summary>
    public ApiResult<PointD> ToUnit(double x, double y)
    {
        var (w, h) = Reference is { } r ? ((double)r.Width, (double)r.Height) : (1.0, 1.0);
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x > w || y > h)
        {
            return ApiResult.Fail<PointD>(ApiError.InvalidRequest(Space == CoordinateSpace.Pixel
                ? "position is outside the screenshot (x in 0..width, y in 0..height)"
                : "position is outside the screen (x and y must be in 0..1)"));
        }

        return ApiResult.Ok<PointD>(new PointD(x / w, y / h));
    }

    /// <summary>Ties the space to the picture and rotations of <paramref name="state"/>.</summary>
    public ApiResult<CoordinateFrame> Bind(ApiHostSnapshot state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!NeedsPicture)
        {
            return ApiResult.Ok<CoordinateFrame>(new CoordinateFrame(this, Rotation.R0, 1.0));
        }

        if (state.Picture is not { } picture)
        {
            return ApiResult.Fail<CoordinateFrame>(ApiError.Create(ApiErrorCodes.NoPicture, "there is no picture of the device"));
        }

        if (Reference is { } stale && stale.Width != stale.Height && (stale.Width > stale.Height) != picture.IsLandscape)
        {
            return ApiResult.Fail<CoordinateFrame>(ApiError.Create(ApiErrorCodes.PictureChanged, "the screen rotated since the screenshot; take a new screenshot"));
        }

        var rotation = picture.IsLandscape ? state.LandscapeRotation : state.PortraitRotation;

        // The short side of the screen as a fraction of the width (x axis of the space).
        var (w, h) = Reference is { } r ? ((double)r.Width, (double)r.Height) : ((double)picture.Width, (double)picture.Height);
        return ApiResult.Ok<CoordinateFrame>(new CoordinateFrame(this, rotation, Math.Min(w, h) / w));
    }
}

/// <summary>
/// A <see cref="CoordinateSpec"/> tied to the current picture and rotation: turns unit positions into digitizer
/// positions.
/// </summary>
public sealed class CoordinateFrame
{
    private readonly Rotation _rotation;
    private readonly double _shortSideOverWidth;

    internal CoordinateFrame(CoordinateSpec spec, Rotation rotation, double shortSideOverWidth)
    {
        Spec = spec;
        _rotation = rotation;
        _shortSideOverWidth = shortSideOverWidth;
    }

    /// <summary>The space the frame converts from.</summary>
    public CoordinateSpec Spec { get; }

    /// <summary>
    /// The digitizer position of a point of the space's unit square: rotated back for <c>screen</c> and
    /// <c>pixel</c> (landscape or portrait rotation by the picture's orientation), as it is for <c>device</c>.
    /// Rounding at the edges is <see cref="Geometry.UnitToAxis"/>.
    /// </summary>
    public AxisPoint ToAxis(PointD unit) =>
        Geometry.UnitToAxis(Spec.NeedsPicture ? Geometry.Rotate(unit, _rotation) : unit);

    /// <summary>
    /// The horizontal distance, in the space's unit x, that a radius (a fraction of the screen's short side) covers.
    /// For <c>screen</c> and <c>pixel</c>: radius times short side over width of the picture (of the screenshot for
    /// <c>pixel</c>). For <c>device</c>: the radius itself, because the digitizer's x axis always spans the
    /// device's short side.
    /// </summary>
    public double HalfWidth(double radius) => radius * _shortSideOverWidth;
}
