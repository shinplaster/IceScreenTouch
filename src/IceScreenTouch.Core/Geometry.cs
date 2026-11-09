// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Core;

/// <summary>A point in screen pixels, or in picture units (0..1) depending on context.</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>A size in pixels.</summary>
public readonly record struct SizeD(double Width, double Height)
{
    /// <summary>Wider than tall.</summary>
    public bool IsLandscape => Width > Height;
}

/// <summary>A rectangle with fractional coordinates (left, top, width, height).</summary>
public readonly record struct RectD(double Left, double Top, double Width, double Height)
{
    /// <summary>The empty rectangle at the origin.</summary>
    public static RectD Empty => default;

    /// <summary>True when the rectangle has an area.</summary>
    public bool HasArea => Width > 0 && Height > 0;

    /// <summary>Wider than tall.</summary>
    public bool IsLandscape => Width > Height;

    /// <summary>Half-open containment: the left/top edges are inside, the right/bottom edges are not.</summary>
    public bool Contains(PointD p) =>
        HasArea && p.X >= Left && p.X < Left + Width && p.Y >= Top && p.Y < Top + Height;

    /// <summary>The same rectangle moved by (dx, dy).</summary>
    public RectD Offset(double dx, double dy) => this with { Left = Left + dx, Top = Top + dy };
}

/// <summary>A rectangle in whole pixels (left, top, width, height), e.g. a monitor work area.</summary>
public readonly record struct RectI(int Left, int Top, int Width, int Height);

/// <summary>A point on the touch digitizer, 0..<see cref="HidConstants.AxisMax"/> on both axes.</summary>
public readonly record struct AxisPoint(ushort X, ushort Y);

/// <summary>
/// Rotation from a picture point to a digitizer point. The digitizer axes are tied to the phone's
/// portrait frame (verified on a real iPhone), so a landscape picture must be rotated before sending.
/// Which of the two landscapes is shown cannot be detected from the stream; it is a user setting.
/// </summary>
public enum Rotation
{
    /// <summary>Portrait, no rotation: (u, v).</summary>
    R0 = 0,

    /// <summary>(1 - v, u). Matches the landscape a game starts in (verified).</summary>
    R90 = 90,

    /// <summary>(1 - u, 1 - v).</summary>
    R180 = 180,

    /// <summary>(v, 1 - u). Matches the opposite landscape (verified).</summary>
    R270 = 270,
}

/// <summary>Pure coordinate math, ported from poc/poc3-integrated/geometry.py.</summary>
public static class Geometry
{
    /// <summary>iPhone portrait aspect, used until the receiver reports the real picture size.</summary>
    public static readonly SizeD FallbackVideoSize = new(393, 852);

    private static readonly Rotation[] RotationCycle = [Rotation.R90, Rotation.R270, Rotation.R0, Rotation.R180];

    /// <summary>
    /// Where a picture of size <paramref name="video"/> is drawn inside a client area: the aspect ratio
    /// is kept and the picture is centered (letterboxed), as d3d11videosink does. The result is relative
    /// to the client area's top-left corner; it is empty if any size is not positive.
    /// </summary>
    public static RectD VideoRect(double clientWidth, double clientHeight, SizeD video)
    {
        if (clientWidth <= 0 || clientHeight <= 0 || video.Width <= 0 || video.Height <= 0)
        {
            return RectD.Empty;
        }

        var scale = Math.Min(clientWidth / video.Width, clientHeight / video.Height);
        var w = video.Width * scale;
        var h = video.Height * scale;
        return new RectD((clientWidth - w) / 2, (clientHeight - h) / 2, w, h);
    }

    /// <summary>Screen point -> (u, v) in 0..1 inside the picture (u to the right, v downward), clamped.</summary>
    public static PointD ToUnit(PointD screen, RectD picture) => new(
        Math.Clamp((screen.X - picture.Left) / picture.Width, 0.0, 1.0),
        Math.Clamp((screen.Y - picture.Top) / picture.Height, 0.0, 1.0));

    /// <summary>Picture point (u, v) -> digitizer point, both in 0..1.</summary>
    public static PointD Rotate(PointD unit, Rotation rotation)
    {
        var (u, v) = (unit.X, unit.Y);
        return rotation switch
        {
            Rotation.R0 => new PointD(u, v),
            Rotation.R90 => new PointD(1 - v, u),
            Rotation.R180 => new PointD(1 - u, 1 - v),
            Rotation.R270 => new PointD(v, 1 - u),
            _ => throw new ArgumentOutOfRangeException(nameof(rotation), rotation, "unsupported rotation"),
        };
    }

    /// <summary>0..1 point -> digitizer axis values, rounded (half to even, as the PoC) and clamped.</summary>
    public static AxisPoint UnitToAxis(PointD unit) => new(ToAxisValue(unit.X), ToAxisValue(unit.Y));

    /// <summary>Screen point -> digitizer point for a picture shown at <paramref name="picture"/>.</summary>
    public static AxisPoint ScreenToAxis(PointD screen, RectD picture, Rotation rotation) =>
        UnitToAxis(Rotate(ToUnit(screen, picture), rotation));

    /// <summary>Second finger of a pinch: the point mirrored through the screen center.</summary>
    public static AxisPoint MirrorPoint(AxisPoint p) =>
        new((ushort)(HidConstants.AxisMax - p.X), (ushort)(HidConstants.AxisMax - p.Y));

    /// <summary>Next rotation in the full cycle 90 -> 270 -> 0 -> 180 (Ctrl+R uses <see cref="ToggleLandscape"/>).</summary>
    public static Rotation NextRotation(Rotation current)
    {
        var i = Array.IndexOf(RotationCycle, current);
        if (i < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(current), current, "unsupported rotation");
        }

        return RotationCycle[(i + 1) % RotationCycle.Length];
    }

    /// <summary>
    /// The other landscape for Ctrl+R: 90 &lt;-&gt; 270. Anything else (0 / 180, set by hand) goes to 90.
    /// Only the two landscapes are ever correct for a landscape picture (verified in PoC 3 and M3).
    /// </summary>
    public static Rotation ToggleLandscape(Rotation current) => current == Rotation.R90 ? Rotation.R270 : Rotation.R90;

    /// <summary>
    /// Client size that shows the picture 1:1, scaled down only if the window would not fit on the
    /// monitor. The window (client + <paramref name="frameWidth"/>/<paramref name="frameHeight"/> of
    /// chrome) may use at most <paramref name="fraction"/> of the work area.
    /// </summary>
    public static (int Width, int Height) FitClientSize(
        SizeD video, double workWidth, double workHeight, double frameWidth = 0, double frameHeight = 0, double fraction = 0.9)
    {
        if (video.Width <= 0 || video.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(video), video, "video size must be positive");
        }

        var maxW = Math.Max(1.0, (workWidth * fraction) - frameWidth);
        var maxH = Math.Max(1.0, (workHeight * fraction) - frameHeight);
        var scale = Math.Min(1.0, Math.Min(maxW / video.Width, maxH / video.Height));
        return (Math.Max(1, (int)Math.Round(video.Width * scale)), Math.Max(1, (int)Math.Round(video.Height * scale)));
    }

    /// <summary>Moves a window (x, y, w, h) the least amount needed to lie inside the work area.</summary>
    public static (int X, int Y) ClampPosition(int x, int y, int width, int height, RectI work)
    {
        x = Math.Min(x, work.Left + work.Width - width);
        y = Math.Min(y, work.Top + work.Height - height);
        return (Math.Max(x, work.Left), Math.Max(y, work.Top));
    }

    private static ushort ToAxisValue(double unit) =>
        (ushort)Math.Clamp(Math.Round(unit * HidConstants.AxisMax), 0, HidConstants.AxisMax);
}
