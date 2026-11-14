// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Pure decisions of the window capture: the size a window that DWM stretches draws at (PrintWindow), and where the client area lies
/// in a Windows.Graphics.Capture frame. The Windows calls that feed them are in <see cref="WindowCapture"/> and
/// <see cref="WindowPictureCapture"/>.
/// </summary>
public static class CaptureGeometry
{
    /// <summary>
    /// The size at which a window that does not follow its monitor's DPI draws its client area (what <c>PrintWindow</c> gives): the
    /// physical size scaled by <paramref name="windowDpi"/> / <paramref name="monitorDpi"/>, each side rounded to the nearest pixel (at
    /// least 1). The same size when either DPI is unknown (0) or they are equal.
    /// </summary>
    public static (int Width, int Height) LogicalClientSize(int physicalWidth, int physicalHeight, uint windowDpi, uint monitorDpi)
    {
        if (windowDpi == 0 || monitorDpi == 0 || windowDpi == monitorDpi || physicalWidth <= 0 || physicalHeight <= 0)
        {
            return (physicalWidth, physicalHeight);
        }

        return (
            Math.Max(1, (int)Math.Round(physicalWidth * (double)windowDpi / monitorDpi, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(physicalHeight * (double)windowDpi / monitorDpi, MidpointRounding.AwayFromZero)));
    }

    /// <summary>
    /// Where the client area lies in a Windows.Graphics.Capture frame of a window. The frame is the window's extended frame bounds
    /// (<c>DWMWA_EXTENDED_FRAME_BOUNDS</c>: the visible frame, without the invisible resize border), in physical pixels, also for a
    /// window that DWM stretches, so the client area's offset inside it is its top left corner on the screen minus the top left corner
    /// of the frame bounds. Null (no picture) when the frame is not exactly the size the bounds have (the window changed since the
    /// bounds were read, or the frame is not the window as assumed: a misaligned picture is worse than none) or when the client area
    /// does not lie inside the frame.
    /// </summary>
    /// <param name="frameBounds">The window's extended frame bounds, screen pixels.</param>
    /// <param name="clientOnScreen">The client area of the window, screen pixels.</param>
    /// <param name="frameWidth">The width of the frame's content.</param>
    /// <param name="frameHeight">The height of the frame's content.</param>
    public static RectI? ClientInFrame(RectI frameBounds, RectI clientOnScreen, int frameWidth, int frameHeight)
    {
        if (frameWidth <= 0 || frameHeight <= 0 || frameBounds.Width != frameWidth || frameBounds.Height != frameHeight
            || clientOnScreen.Width <= 0 || clientOnScreen.Height <= 0)
        {
            return null;
        }

        var x = (long)clientOnScreen.Left - frameBounds.Left;
        var y = (long)clientOnScreen.Top - frameBounds.Top;
        if (x < 0 || y < 0 || x + clientOnScreen.Width > frameWidth || y + clientOnScreen.Height > frameHeight)
        {
            return null;
        }

        return new RectI((int)x, (int)y, clientOnScreen.Width, clientOnScreen.Height);
    }
}
