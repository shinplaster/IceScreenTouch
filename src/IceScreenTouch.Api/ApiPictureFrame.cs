// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>
/// The pixels of the picture of the device as the host cut them out of the receiver window (see <see cref="IApiHost.CapturePictureAsync"/>):
/// 32-bit BGRA, top-down rows, owned by the frame (a copy; the capture buffer is gone). The alpha byte means nothing (a receiver's
/// window may leave it 0): the screenshot is always opaque.
/// </summary>
public sealed class ApiPictureFrame
{
    /// <summary>Creates the frame; <paramref name="bgra"/> must hold <paramref name="height"/> rows of <paramref name="stride"/> bytes (the last may be short of the padding).</summary>
    /// <param name="bgra">The pixels.</param>
    /// <param name="width">Width in pixels (1 or more).</param>
    /// <param name="height">Height in pixels (1 or more).</param>
    /// <param name="stride">Bytes per row (at least 4 times the width).</param>
    /// <param name="isLandscape">The picture is wider than tall, from the same snapshot of the bridge as the cut (see <see cref="PictureInfo.IsLandscape"/>).</param>
    /// <param name="rotation">The rotation the app assumes for that orientation, from the same snapshot.</param>
    public ApiPictureFrame(byte[] bgra, int width, int height, int stride, bool isLandscape, Rotation rotation)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan((long)stride, (long)width * 4);
        if (!PictureLimits.Allows(width, height))
        {
            throw new ArgumentOutOfRangeException(nameof(width), $"a picture of {width}x{height} pixels is bigger than the limit");
        }

        if (bgra.LongLength < ((long)stride * (height - 1)) + ((long)width * 4))
        {
            throw new ArgumentException("the pixel buffer is smaller than height rows of stride bytes", nameof(bgra));
        }

        Bgra = bgra;
        Width = width;
        Height = height;
        Stride = stride;
        IsLandscape = isLandscape;
        Rotation = rotation;
    }

    /// <summary>The pixels, BGRA, top-down.</summary>
    public byte[] Bgra { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row.</summary>
    public int Stride { get; }

    /// <summary>The picture is landscape.</summary>
    public bool IsLandscape { get; }

    /// <summary>The assumed rotation for that orientation.</summary>
    public Rotation Rotation { get; }
}

/// <summary>The size a screenshot may have: a bigger one is no picture of a phone, and its buffer and encoder would cost too much.</summary>
public static class PictureLimits
{
    /// <summary>The longest side in pixels.</summary>
    public const int MaxSide = 16_384;

    /// <summary>Pixels in all (a little more than 8K: 7680 x 4320).</summary>
    public const long MaxPixels = 40_000_000;

    /// <summary>The size is positive and within <see cref="MaxSide"/> and <see cref="MaxPixels"/> (computed in 64 bits).</summary>
    public static bool Allows(long width, long height) =>
        width is > 0 and <= MaxSide && height is > 0 and <= MaxSide && width * height <= MaxPixels;
}

/// <summary>A screenshot ready to send: the encoded image and what the headers say.</summary>
/// <param name="Data">The PNG or JPEG file.</param>
/// <param name="ContentType"><c>image/png</c> or <c>image/jpeg</c>.</param>
/// <param name="Orientation"><c>portrait</c> or <c>landscape</c>.</param>
/// <param name="Rotation">0, 90, 180 or 270.</param>
/// <param name="SourceWidth">Width of the picture before it was shrunk.</param>
/// <param name="SourceHeight">Height of the picture before it was shrunk.</param>
public sealed record ScreenshotImage(byte[] Data, string ContentType, string Orientation, int Rotation, int SourceWidth, int SourceHeight);
