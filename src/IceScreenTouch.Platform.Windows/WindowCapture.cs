using IceScreenTouch.Core;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>Reads the pixels of a captured image (32-bit BGRA, top-down rows).</summary>
/// <typeparam name="T">What the reader makes of them.</typeparam>
public delegate T CapturedPixelsReader<out T>(ReadOnlySpan<byte> bgra, int width, int height, int stride);

/// <summary>
/// Captures the client area of a window. While nothing covers it, the pixels are copied from the screen: the
/// window itself is not involved. Otherwise <c>PrintWindow(PW_CLIENTONLY | PW_RENDERFULLCONTENT)</c> reads the
/// window's own picture from the compositor, which works while the window is covered by others, and for windows
/// drawn with Direct3D (AirServer, d3d11videosink); but it makes the window draw again, and LonelyScreen (a Qt
/// window with an OpenGL picture) visibly flickered at every capture (2026-11-06). Without PW_RENDERFULLCONTENT
/// its picture is missing. No yellow capture border, no Direct3D device. The bitmap is kept while the size stays
/// the same. Not thread-safe: use one instance per thread.
/// </summary>
public sealed class WindowCapture : IDisposable
{
    // Points per side of the grid checked for windows on top (corners and inner points).
    private const int CoverGrid = 5;

    private nint _dc;
    private nint _bitmap;
    private nint _oldBitmap;
    private nint _bits;
    private int _width;
    private int _height;

    /// <summary>Copy from the screen while the window is not covered (default); false = always PrintWindow.</summary>
    public bool PreferScreen { get; init; } = true;

    /// <summary>The last capture came from the screen (else from PrintWindow).</summary>
    public bool LastFromScreen { get; private set; }

    /// <summary>
    /// Captures the client area of <paramref name="window"/> and hands the pixels to <paramref name="read"/>.
    /// Returns default when the window is gone, minimized or empty, or the capture fails.
    /// </summary>
    public unsafe T? Capture<T>(nint window, CapturedPixelsReader<T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        ObjectDisposedException.ThrowIf(_width < 0, this);
        if (!Win32Windows.IsShown(window) || !GetClientRect(window, out var rc) || rc.Right <= 0 || rc.Bottom <= 0)
        {
            return default;
        }

        if (!Ensure(rc.Right, rc.Bottom))
        {
            return default;
        }

        LastFromScreen = PreferScreen && Win32Windows.ClientRectOnScreen(window) is { } onScreen
            && onScreen.Width == rc.Right && onScreen.Height == rc.Bottom
            && IsUncovered(window, onScreen) && CopyFromScreen(onScreen);
        if (!LastFromScreen && !PrintWindow(window, _dc, PwClientOnly | PwRenderFullContent))
        {
            return default;
        }

        GdiFlush();
        var stride = _width * 4;
        return read(new ReadOnlySpan<byte>((void*)_bits, stride * _height), _width, _height, stride);
    }

    // The window is on top at a grid of points over its client area (a point off the screen finds no window).
    private static bool IsUncovered(nint window, RectI client)
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
            return screen != 0 && BitBlt(_dc, 0, 0, _width, _height, screen, client.Left, client.Top, SrcCopy);
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Release();
        _width = -1;
    }

    private bool Ensure(int width, int height)
    {
        if (_dc != 0 && width == _width && height == _height)
        {
            return true;
        }

        Release();
        var screen = GetDC(0);
        try
        {
            _dc = CreateCompatibleDC(screen);
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }

        if (_dc == 0)
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
        _bitmap = CreateDIBSection(_dc, ref header, 0, out _bits, 0, 0);
        if (_bitmap == 0 || _bits == 0)
        {
            Release();
            return false;
        }

        _oldBitmap = SelectObject(_dc, _bitmap);
        (_width, _height) = (width, height);
        return true;
    }

    private void Release()
    {
        if (_dc != 0 && _oldBitmap != 0)
        {
            SelectObject(_dc, _oldBitmap);
        }

        if (_bitmap != 0)
        {
            DeleteObject(_bitmap);
        }

        if (_dc != 0)
        {
            DeleteDC(_dc);
        }

        (_dc, _bitmap, _oldBitmap, _bits, _width, _height) = (0, 0, 0, 0, 0, 0);
    }
}
