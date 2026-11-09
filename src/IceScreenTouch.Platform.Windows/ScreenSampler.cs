// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Reads the mean brightness of a small square of the screen (what the compositor shows there, whichever
/// window is on top), fast enough to time a picture change to a few milliseconds. Used by the latency probe
/// on the mirror window. Screen coordinates are physical pixels (the process is per-monitor DPI aware).
/// Not thread-safe (one caller at a time), but the caller may change threads between calls.
/// </summary>
public sealed class ScreenSampler : IDisposable
{
    private readonly int _size;
    private nint _dc;
    private nint _bitmap;
    private nint _oldBitmap;
    private nint _bits;

    /// <summary>Samples squares of <paramref name="size"/> × <paramref name="size"/> pixels.</summary>
    public ScreenSampler(int size = 24)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        _size = size;
        var screen = GetDC(0);
        try
        {
            _dc = CreateCompatibleDC(screen);
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }

        var header = new BitmapInfoHeader
        {
            Size = System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };
        _bitmap = _dc == 0 ? 0 : CreateDIBSection(_dc, ref header, 0, out _bits, 0, 0);
        if (_bitmap == 0 || _bits == 0)
        {
            Dispose();
            throw new InvalidOperationException("cannot create the bitmap for screen sampling");
        }

        _oldBitmap = SelectObject(_dc, _bitmap);
    }

    /// <summary>The side of the sampled square in pixels.</summary>
    public int Size => _size;

    /// <summary>
    /// Mean brightness (0 = black .. 255 = white; Rec. 601 weights) of the square centred on
    /// (<paramref name="centerX"/>, <paramref name="centerY"/>), or null when the screen cannot be read
    /// (secure desktop, locked session).
    /// </summary>
    public unsafe double? MeanLuma(int centerX, int centerY)
    {
        ObjectDisposedException.ThrowIf(_dc == 0, this);
        var half = _size / 2;
        // The screen DC is taken per call: the caller may move between threads (async code).
        var screen = GetDC(0);
        bool copied;
        try
        {
            copied = screen != 0 && BitBlt(_dc, 0, 0, _size, _size, screen, centerX - half, centerY - half, SrcCopy);
        }
        finally
        {
            _ = ReleaseDC(0, screen);
        }

        if (!copied)
        {
            return null;
        }

        GdiFlush();
        var pixels = new ReadOnlySpan<byte>((void*)_bits, _size * _size * 4);
        long sum = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            // BGRA: 0.114 B + 0.587 G + 0.299 R, in integer thousandths.
            sum += (114 * pixels[i]) + (587 * pixels[i + 1]) + (299 * pixels[i + 2]);
        }

        return sum / 1000.0 / (_size * _size);
    }

    /// <inheritdoc/>
    public void Dispose()
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

        (_dc, _bitmap, _oldBitmap, _bits) = (0, 0, 0, 0);
    }
}
