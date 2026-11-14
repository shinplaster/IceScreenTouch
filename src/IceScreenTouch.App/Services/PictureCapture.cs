// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Services;

/// <summary>The pixels of a crop, copied out of a capture buffer.</summary>
/// <param name="Bgra">32-bit BGRA, top-down, alpha 255 everywhere.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stride">Bytes per row (the width times 4: no padding).</param>
public sealed record CroppedPixels(byte[] Bgra, int Width, int Height, int Stride);

/// <summary>Pure decisions and copies of a screenshot (no Windows): what to cut out of a capture, and the cut.</summary>
public static class PictureCaptureMath
{
    /// <summary>
    /// The crop (in the coordinates of the captured client area) for a picture that the bridge measured in a client area
    /// (<paramref name="snapshotClient"/>, screen pixels, from the same <see cref="BridgeStatus"/> as <paramref name="picture"/>), when the client
    /// area now is <paramref name="liveWidth"/> x <paramref name="liveHeight"/> big. Null (a transient <c>no_picture</c>) when the window was
    /// resized since (the measurement does not hold any more), or when the picture is not inside the client area by more than the rounding
    /// (<see cref="PictureCrop.ToClient"/>: a crop smaller than <see cref="PictureCrop.InfoOf"/> by more than 1 pixel would be a silently smaller image).
    /// A window that only moved is fine: the crop is relative to the client area, wherever that is.
    /// </summary>
    public static RectI? CropFor(RectD picture, RectI snapshotClient, int liveWidth, int liveHeight)
    {
        if (snapshotClient.Width != liveWidth || snapshotClient.Height != liveHeight)
        {
            return null;
        }

        if (PictureCrop.InfoOf(picture) is not { } size || PictureCrop.ToClient(picture, snapshotClient) is not { } crop)
        {
            return null;
        }

        return Math.Abs(crop.Width - size.Width) > 1 || Math.Abs(crop.Height - size.Height) > 1 ? null : crop;
    }

    /// <summary>
    /// Copies <paramref name="crop"/> out of a BGRA buffer of <paramref name="height"/> rows of <paramref name="stride"/> bytes (the row may be
    /// longer than the width) into a buffer of its own, and makes it opaque (a receiver's window may leave the alpha byte 0). Null when the
    /// crop is empty or not inside the buffer.
    /// </summary>
    public static CroppedPixels? CopyCrop(ReadOnlySpan<byte> bgra, int width, int height, int stride, RectI crop)
    {
        if (crop.Width <= 0 || crop.Height <= 0 || crop.Left < 0 || crop.Top < 0
            || (long)crop.Left + crop.Width > width || (long)crop.Top + crop.Height > height
            || !PictureLimits.Allows(crop.Width, crop.Height) || width <= 0 || height <= 0
            || stride < (long)width * 4 || bgra.Length < ((long)stride * (height - 1)) + ((long)width * 4))
        {
            return null;
        }

        // Sizes are limited (PictureLimits), so these products fit; the offsets into the source are computed in 64 bits (a stride is any int).
        var row = crop.Width * 4;
        var pixels = new byte[row * crop.Height];
        for (var y = 0; y < crop.Height; y++)
        {
            var from = ((long)(crop.Top + y) * stride) + ((long)crop.Left * 4);
            bgra.Slice((int)from, row).CopyTo(pixels.AsSpan(y * row, row));
        }

        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }

        return new CroppedPixels(pixels, crop.Width, crop.Height, row);
    }
}

/// <summary>What <see cref="CaptureRunner"/> reports (the host logs it).</summary>
public enum CaptureIssue
{
    /// <summary>A capture did not finish in time (the window does not answer).</summary>
    TimedOut,

    /// <summary>A request was refused at once because an earlier capture is still stuck.</summary>
    StillStuck,
}

/// <summary>
/// Runs one capture at a time, off the request's thread, with a time limit that starts when the capture starts (waiting for the one
/// before it does not count). A capture that does not finish in time ends the request (null) but keeps the gate until it does finish;
/// while it is stuck, new requests are refused at once instead of each waiting out the time limit and parking a request thread, and a
/// request that waits for the gate for as long as a capture may take is refused as well (the one in front is stuck). A cancellation by
/// the caller is not a timeout: it ends the wait with <see cref="OperationCanceledException"/> (the capture itself still runs to its
/// end and then frees the gate).
/// </summary>
#pragma warning disable CA1001 // The SemaphoreSlim has no wait handle (AvailableWaitHandle is never used): nothing to dispose.
public sealed class CaptureRunner(TimeSpan timeout, Action<CaptureIssue> report)
#pragma warning restore CA1001
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _state = new();
    private bool _running;
    private bool _stuck;

    /// <summary>A capture has timed out and is still running.</summary>
    public bool IsStuck
    {
        get
        {
            lock (_state)
            {
                return _stuck;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="capture"/> on the thread pool when it is its turn. Returns its result; null when it timed out or an earlier one is
    /// stuck (<see cref="CaptureIssue"/> is reported). An exception of the capture comes out of this call (the gate is freed either way).
    /// </summary>
    public async ValueTask<T?> RunAsync<T>(Func<T?> capture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (IsStuck)
        {
            report(CaptureIssue.StillStuck);
            return default;
        }

        // Waiting is not the capture's time. Waiting as long as a capture may take means the one in front has not finished in its time.
        if (!await _gate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            report(CaptureIssue.StillStuck);
            return default;
        }

        lock (_state)
        {
            _running = true;
        }

        var work = Task.Run(
            () =>
            {
                try
                {
                    return capture();
                }
                finally
                {
                    lock (_state)
                    {
                        _running = false;
                        _stuck = false;
                    }

                    _gate.Release();
                }
            },
            CancellationToken.None);
        try
        {
            return await work.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_state)
            {
                _stuck = _running;
            }

            report(CaptureIssue.TimedOut);
            return default;
        }
    }
}

/// <summary>Lets a message through at most once per interval for each key (a script may ask ten times a second).</summary>
public sealed class LogThrottle(TimeProvider time, TimeSpan interval)
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _last = [];

    /// <summary>True (and counts it) when no message of this key was let through within the interval.</summary>
    public bool Allow(string key)
    {
        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (_last.TryGetValue(key, out var at) && time.GetElapsedTime(at, now) < interval)
            {
                return false;
            }

            _last[key] = now;
            return true;
        }
    }
}
