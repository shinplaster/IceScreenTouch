// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>
/// The rectangle a screenshot cuts out of the receiver window, and its size as the status shows it. Pure arithmetic (no Windows),
/// so the status (<see cref="PictureInfo"/>) and the screenshot round the same numbers the same way.
/// </summary>
public static class PictureCrop
{
    // A coordinate this far out is not a window on a screen; it only guards the conversion to int.
    private const double Limit = 1_000_000;

    /// <summary>
    /// The size of the picture rectangle in whole pixels (each side rounded to the nearest, halves to even, like
    /// <see cref="Math.Round(double)"/>), or null when it has no area or rounds to nothing.
    /// </summary>
    public static PictureInfo? InfoOf(RectD? picture)
    {
        if (picture is not { } p || !p.HasArea || !double.IsFinite(p.Width) || !double.IsFinite(p.Height))
        {
            return null;
        }

        var width = (int)Math.Round(Math.Min(p.Width, Limit));
        var height = (int)Math.Round(Math.Min(p.Height, Limit));
        return width > 0 && height > 0 ? new PictureInfo(width, height) : null;
    }

    /// <summary>
    /// The picture rectangle (<paramref name="picture"/>, screen pixels) in the coordinates of the client area
    /// (<paramref name="client"/>, screen pixels: its top left is the origin, its size the limit). The size is
    /// <see cref="InfoOf"/>'s, so the screenshot is as large as the status says; the position is rounded to the nearest pixel.
    /// A rectangle that sticks out of the client area by a pixel only because of the rounding is moved back in; one that is
    /// really partly outside is cut at the client edge (smaller than the status says). Null when nothing of it is inside.
    /// </summary>
    public static RectI? ToClient(RectD picture, RectI client)
    {
        if (InfoOf(picture) is not { } size || client.Width <= 0 || client.Height <= 0
            || !double.IsFinite(picture.Left) || !double.IsFinite(picture.Top))
        {
            return null;
        }

        var (x, width) = Fit(picture.Left - client.Left, size.Width, client.Width);
        var (y, height) = Fit(picture.Top - client.Top, size.Height, client.Height);
        return width > 0 && height > 0 ? new RectI(x, y, width, height) : null;
    }

    private static (int Start, int Length) Fit(double start, int length, int limit)
    {
        var s = (int)Math.Round(Math.Clamp(start, -Limit, Limit));
        if (length <= limit)
        {
            // One pixel over because the start and the size were rounded on their own: move, do not cut.
            if (s == -1)
            {
                s = 0;
            }
            else if ((long)s + length == (long)limit + 1)
            {
                s = limit - length;
            }
        }

        var from = Math.Max(s, 0);
        var to = (int)Math.Min((long)s + length, limit);
        return (from, Math.Max(to - from, 0));
    }
}
