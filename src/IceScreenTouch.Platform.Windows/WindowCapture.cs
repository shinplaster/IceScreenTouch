// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>Reads the pixels of a captured image (32-bit BGRA, top-down rows).</summary>
/// <typeparam name="T">What the reader makes of them.</typeparam>
public delegate T CapturedPixelsReader<out T>(ReadOnlySpan<byte> bgra, int width, int height, int stride);

/// <summary>Where the pixels of the last <see cref="WindowCapture"/> came from.</summary>
public enum CaptureOrigin
{
    /// <summary>Nothing was taken (<see cref="WindowCapture.CoveredCapture"/> had no picture).</summary>
    None = 0,

    /// <summary>A copy from the screen: nothing covered the window.</summary>
    Screen,

    /// <summary>Windows.Graphics.Capture (<see cref="WindowCapture.CoveredCapture"/>).</summary>
    GraphicsCapture,

    /// <summary>PrintWindow.</summary>
    PrintWindow,
}

/// <summary>Where <see cref="WindowCapture"/> may take the pixels from.</summary>
public enum WindowCaptureMode
{
    /// <summary>
    /// From the screen while a grid of points over the window finds the window on top (hit-testing), else with PrintWindow. The grid
    /// does not see click-through overlays or small windows between its points, so this is only for pixels that never leave the
    /// application (the picture detection). Pixels that leave it are read with <see cref="WindowPictureCapture"/>.
    /// </summary>
    ScreenWhenNotHit = 0,

    /// <summary>Always with PrintWindow: only the window itself is drawn, nothing from other windows can appear.</summary>
    PrintWindowOnly,
}

/// <summary>
/// Captures the client area of a window, either with <c>PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT)</c> (the window's own picture
/// from the compositor, which works while the window is covered by others, and for windows drawn with Direct3D: AirServer,
/// d3d11videosink) or, when <see cref="Mode"/> allows and a grid of points finds the window on top, by copying from the screen.
/// PrintWindow makes the window draw again, and LonelyScreen (a Qt window with an OpenGL picture) visibly flickered at every capture
/// (2026-11-06; white bands at the top and bottom, 2026-11-14). Without PW_RENDERFULLCONTENT its picture is missing. No yellow capture
/// border, no Direct3D device (unless <see cref="CoveredCapture"/> is set, for the covered case). The bitmap is kept while the size stays the same. Not thread-safe: use one instance per thread.
/// </summary>
/// <remarks>
/// A window that does not follow its monitor's DPI (AirServer's installer build, UxPlay's d3d11videosink window, 5KPlayer, LonelyScreen
/// on a 125 % monitor) is stretched by DWM: its client area is bigger than what it draws, and PrintWindow gives the unstretched picture.
/// That is printed into a bitmap of the size the window draws at and stretched up to the client size, so the result is the same
/// picture as on the screen (verified 2026-11-14; before, the picture sat shrunk in the top left corner).
/// </remarks>
public sealed class WindowCapture : IDisposable
{
    // Points per side of the grid checked for windows on top (corners and inner points).
    private const int CoverGrid = 5;

    private readonly Dib _target = new();
    private readonly Dib _unscaled = new();
    private bool _disposed;

    /// <summary>Where the pixels may come from (default: <see cref="WindowCaptureMode.ScreenWhenNotHit"/>).</summary>
    public WindowCaptureMode Mode { get; init; } = WindowCaptureMode.ScreenWhenNotHit;

    /// <summary>
    /// What takes the picture while the window is not found uncovered (default: null, PrintWindow). The caller owns it and disposes it
    /// after this capture. Created with <c>borderless: true</c> it only runs when the capture border can be switched off, otherwise it
    /// leaves the picture to PrintWindow. Used from the thread that calls <see cref="Capture{T}"/>.
    /// </summary>
    public WindowPictureCapture? CoveredCapture { get; init; }

    /// <summary>The last capture came from the screen (else from PrintWindow or <see cref="CoveredCapture"/>).</summary>
    public bool LastFromScreen { get; private set; }

    /// <summary>Where the last capture's pixels came from (<see cref="CaptureOrigin.None"/>: <see cref="CoveredCapture"/> found no picture).</summary>
    public CaptureOrigin LastOrigin { get; private set; }

    /// <summary>
    /// Captures the client area of <paramref name="window"/> and hands the pixels to <paramref name="read"/>. Returns default when
    /// the window is gone, minimized or empty, or the capture fails.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="read">Reads the pixels (valid only inside the call).</param>
    public unsafe T? Capture<T>(nint window, CapturedPixelsReader<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Win32Windows.IsShown(window) || !GetClientRect(window, out var rc) || rc.Right <= 0 || rc.Bottom <= 0)
        {
            (LastFromScreen, LastOrigin) = (false, CaptureOrigin.None);
            return default;
        }

        // A client area whose pixels do not fit a buffer of 2 GB is not a window that is read (and the span below is an int).
        if ((long)rc.Right * rc.Bottom * 4 > int.MaxValue || !_target.Ensure(rc.Right, rc.Bottom))
        {
            (LastFromScreen, LastOrigin) = (false, CaptureOrigin.None);
            return default;
        }

        LastFromScreen = Mode != WindowCaptureMode.PrintWindowOnly && Win32Windows.ClientRectOnScreen(window) is { } onScreen
            && onScreen.Width == rc.Right && onScreen.Height == rc.Bottom
            && IsUncoveredAtGrid(window, onScreen) && CopyFromScreen(onScreen);
        if (LastFromScreen)
        {
            LastOrigin = CaptureOrigin.Screen;
        }
        else
        {
            if (CoveredCapture is not null && Mode != WindowCaptureMode.PrintWindowOnly)
            {
                // The window is (or may be) covered: its own pixels from Windows.Graphics.Capture, which does not make it redraw. The
                // client area comes at the size the screen copy has. When that cannot be used, PrintWindow below takes over.
                var shot = CoveredCapture.CaptureGraphics(window, read);
                if (shot.Source != PictureSource.PrintWindow)
                {
                    LastOrigin = shot.Source == PictureSource.GraphicsCapture ? CaptureOrigin.GraphicsCapture : CaptureOrigin.None;
                    return shot.Value;
                }
            }

            LastOrigin = CaptureOrigin.PrintWindow;
            if (!Print(window, rc.Right, rc.Bottom))
            {
                LastOrigin = CaptureOrigin.None;
                return default;
            }
        }

        GdiFlush();
        var stride = _target.Width * 4;
        return read(new ReadOnlySpan<byte>((void*)_target.Bits, stride * _target.Height), _target.Width, _target.Height, stride);
    }

    // The window is on top at a grid of points over its client area (a point off the screen finds no window).
    private static bool IsUncoveredAtGrid(nint window, RectI client)
    {
        var root = Win32Windows.RootOf(window);
        for (var i = 0; i < CoverGrid; i++)
        {
            for (var j = 0; j < CoverGrid; j++)
            {
                var x = client.Left + 1 + ((client.Width - 3) * i / (CoverGrid - 1));
                var y = client.Top + 1 + ((client.Height - 3) * j / (CoverGrid - 1));
                if (Win32Windows.RootWindowAt(x, y) != root)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool CopyFromScreen(RectI client)
    {
        var screen = GetDC(0);
        try
        {
            return screen != 0 && BitBlt(_target.Dc, 0, 0, _target.Width, _target.Height, screen, client.Left, client.Top, SrcCopy);
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }
    }

    // PrintWindow into the target bitmap; for a window that DWM stretches, into a bitmap of the size it draws at, stretched up.
    private bool Print(nint window, int width, int height)
    {
        var (windowDpi, monitorDpi) = Win32Windows.DpiOf(window);
        var (logicalWidth, logicalHeight) = CaptureGeometry.LogicalClientSize(width, height, windowDpi, monitorDpi);
        if ((logicalWidth, logicalHeight) == (width, height))
        {
            return PrintWindow(window, _target.Dc, PwClientOnly | PwRenderFullContent);
        }

        if (!_unscaled.Ensure(logicalWidth, logicalHeight) || !PrintWindow(window, _unscaled.Dc, PwClientOnly | PwRenderFullContent))
        {
            return false;
        }

        GdiFlush();
        _ = SetStretchBltMode(_target.Dc, Halftone);
        _ = SetBrushOrgEx(_target.Dc, 0, 0, 0); // required after HALFTONE
        return StretchBlt(_target.Dc, 0, 0, width, height, _unscaled.Dc, 0, 0, logicalWidth, logicalHeight, SrcCopy);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _target.Release();
        _unscaled.Release();
        _disposed = true;
    }

    // A top-down 32-bit DIB section selected into its own memory DC; recreated when the size changes.
    private sealed class Dib
    {
        private nint _bitmap;
        private nint _oldBitmap;

        public nint Dc { get; private set; }

        public nint Bits { get; private set; }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public bool Ensure(int width, int height)
        {
            if (Dc != 0 && width == Width && height == Height)
            {
                return true;
            }

            Release();
            var screen = GetDC(0);
            try
            {
                Dc = CreateCompatibleDC(screen);
            }
            finally
            {
                _ = ReleaseDC(0, screen);
            }

            if (Dc == 0)
            {
                return false;
            }

            var header = new BitmapInfoHeader
            {
                Size = System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // top-down
                Planes = 1,
                BitCount = 32,
            };
            _bitmap = CreateDIBSection(Dc, ref header, 0, out var bits, 0, 0);
            Bits = bits;
            if (_bitmap == 0 || Bits == 0)
            {
                Release();
                return false;
            }

            _oldBitmap = SelectObject(Dc, _bitmap);
            (Width, Height) = (width, height);
            return true;
        }

        public void Release()
        {
            if (Dc != 0 && _oldBitmap != 0)
            {
                SelectObject(Dc, _oldBitmap);
            }

            if (_bitmap != 0)
            {
                DeleteObject(_bitmap);
            }

            if (Dc != 0)
            {
                DeleteDC(Dc);
            }

            (Dc, _bitmap, _oldBitmap, Bits, Width, Height) = (0, 0, 0, 0, 0, 0);
        }
    }
}
