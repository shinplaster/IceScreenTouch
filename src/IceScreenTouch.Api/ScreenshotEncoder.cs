// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using SkiaSharp;

namespace IceScreenTouch.Api;

/// <summary>Shrinks a picture frame (never enlarges) and encodes it as an opaque PNG or JPEG. Runs on any thread; no Windows API.</summary>
internal static class ScreenshotEncoder
{
    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>The size after <c>maxSize</c>: the longest side becomes <paramref name="maxSize"/> when it is larger, the aspect stays (each side at least 1).</summary>
    public static (int Width, int Height) ShrunkSize(int width, int height, int? maxSize)
    {
        var longest = Math.Max(width, height);
        if (maxSize is not { } limit || longest <= limit)
        {
            return (width, height);
        }

        var scale = (double)limit / longest;
        return (width >= height ? limit : Math.Max(1, (int)Math.Round(width * scale)), height > width ? limit : Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>The encoded screenshot of <paramref name="frame"/>.</summary>
    /// <exception cref="InvalidOperationException">Skia could not shrink or encode the picture.</exception>
    public static ScreenshotImage Encode(ApiPictureFrame frame, ScreenshotOptions options)
    {
        // Opaque: Skia ignores the alpha byte (a receiver's window may leave it 0), and the encoders write no alpha channel.
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var handle = GCHandle.Alloc(frame.Bgra, GCHandleType.Pinned);
        try
        {
            using var source = new SKBitmap();
            if (!source.InstallPixels(info, handle.AddrOfPinnedObject(), frame.Stride))
            {
                throw new InvalidOperationException("the picture could not be read");
            }

            var (width, height) = ShrunkSize(frame.Width, frame.Height, options.MaxSize);
            SKBitmap? shrunk = null;
            try
            {
                if ((width, height) != (frame.Width, frame.Height))
                {
                    shrunk = source.Resize(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque), Sampling)
                        ?? throw new InvalidOperationException("the picture could not be shrunk");
                }

                using var pixmap = (shrunk ?? source).PeekPixels();
                using var data = options.Format == ScreenshotFormat.Jpeg
                    ? pixmap.Encode(new SKJpegEncoderOptions(options.Quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore))
                    : pixmap.Encode(SKPngEncoderOptions.Default);
                if (data is null)
                {
                    throw new InvalidOperationException("the picture could not be encoded");
                }

                return new ScreenshotImage(
                    data.ToArray(),
                    options.Format == ScreenshotFormat.Jpeg ? "image/jpeg" : "image/png",
                    frame.IsLandscape ? "landscape" : "portrait",
                    (int)frame.Rotation,
                    frame.Width,
                    frame.Height);
            }
            finally
            {
                shrunk?.Dispose();
            }
        }
        finally
        {
            handle.Free();
        }
    }
}

/// <summary>
/// At most <see cref="Capacity"/> admissions (<see cref="Limit"/> for screenshots) in any rolling <see cref="Window"/>, counted on the given
/// clock. A text reading counts in two windows at once (the screenshots and its own, see <see cref="TryAcquire(ScreenshotRateLimiter)"/>).
/// </summary>
internal sealed class ScreenshotRateLimiter(TimeProvider time, int capacity = ScreenshotRateLimiter.Limit)
{
    /// <summary>Screenshots per window.</summary>
    public const int Limit = 10;

    /// <summary>Admissions per window.</summary>
    public int Capacity { get; } = capacity;

    /// <summary>The rolling window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly Queue<long> _admitted = new(Limit);

    /// <summary>Counts one request and returns true, or returns false (and counts nothing) when the window is full.</summary>
    public bool TryAcquire()
    {
        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (!HasRoom(now))
            {
                return false;
            }

            _admitted.Enqueue(now);
            return true;
        }
    }

    /// <summary>
    /// Counts one request in this window and in <paramref name="other"/> when both have room, else counts nothing in either: a request refused by
    /// one never uses up the other. Both windows are checked and counted under both locks (taken in a fixed order: this one, then
    /// <paramref name="other"/>; call it always with the same two in the same order).
    /// </summary>
    public bool TryAcquire(ScreenshotRateLimiter other)
    {
        ArgumentNullException.ThrowIfNull(other);
        lock (_gate)
        {
            lock (other._gate)
            {
                // Both windows are on the controller's clock.
                var now = time.GetTimestamp();
                if (!HasRoom(now) || !other.HasRoom(now))
                {
                    return false;
                }

                _admitted.Enqueue(now);
                other._admitted.Enqueue(now);
                return true;
            }
        }
    }

    // Drops the admissions older than the window; true when another fits. Called under the lock.
    private bool HasRoom(long now)
    {
        while (_admitted.Count > 0 && time.GetElapsedTime(_admitted.Peek(), now) >= Window)
        {
            _admitted.Dequeue();
        }

        return _admitted.Count < Capacity;
    }
}
