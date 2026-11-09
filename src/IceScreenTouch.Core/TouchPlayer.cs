// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Core;

/// <summary>
/// Sends fingers down and lifts them correctly, and plays scripted gestures (tap, swipe, pinch).
/// Ported from the gesture helpers of poc/poc2-ble-hid/ble_hid_poc.py. Not thread-safe: use it from
/// one loop at a time.
/// </summary>
public sealed class TouchPlayer
{
    private readonly ITouchSink _sink;
    private readonly TimeProvider _time;
    private TouchContact[] _down = [];

    /// <summary>Creates a player that sends to <paramref name="sink"/> and waits on <paramref name="time"/>.</summary>
    public TouchPlayer(ITouchSink sink, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The sink the reports go to.</summary>
    public ITouchSink Sink => _sink;

    /// <summary>True while at least one finger is down.</summary>
    public bool IsDown => _down.Length > 0;

    /// <summary>The fingers currently down, as last sent.</summary>
    public IReadOnlyList<TouchContact> Contacts => _down;

    /// <summary>Puts one finger (contact 0) down at <paramref name="p"/>, or moves it there.</summary>
    public ValueTask DownAsync(AxisPoint p, CancellationToken cancellationToken = default) =>
        SendDownAsync([new TouchContact(0, p.X, p.Y)], cancellationToken);

    /// <summary>Puts two fingers (contacts 0 and 1) down, or moves them.</summary>
    public ValueTask DownAsync(AxisPoint first, AxisPoint second, CancellationToken cancellationToken = default) =>
        SendDownAsync([new TouchContact(0, first.X, first.Y), new TouchContact(1, second.X, second.Y)], cancellationToken);

    /// <summary>
    /// Lifts every finger at its LAST position (Tip = 0), then sends an empty report. Never lifts at
    /// (0, 0): iOS would see the finger jump to the top-left corner first. The lift goes out even if
    /// <paramref name="cancellationToken"/> is cancelled (<see cref="ReleaseSend"/>).
    /// </summary>
    public async ValueTask LiftAsync(CancellationToken cancellationToken = default)
    {
        var down = _down;
        _down = [];
        if (down.Length > 0)
        {
            var lifted = Array.ConvertAll(down, c => c.Lifted());
            await ReleaseSend.RunAsync(ct => _sink.SendAsync(lifted, ct), cancellationToken).ConfigureAwait(false);
        }

        await ReleaseSend.RunAsync(ct => _sink.SendAsync(ReadOnlyMemory<TouchContact>.Empty, ct), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tap: down, hold, lift (the lift also when cancelled).</summary>
    public async ValueTask TapAsync(AxisPoint p, TimeSpan hold, CancellationToken cancellationToken = default)
    {
        try
        {
            await DownAsync(p, cancellationToken).ConfigureAwait(false);
            await DelayAsync(hold, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await LiftAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One-finger swipe in <paramref name="steps"/> + 1 frames spread over <paramref name="duration"/>.</summary>
    public async ValueTask SwipeAsync(AxisPoint from, AxisPoint to, TimeSpan duration, int steps = 20, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        var pause = duration / steps;
        try
        {
            for (var i = 0; i <= steps; i++)
            {
                var t = (double)i / steps;
                await DownAsync(Lerp(from, to, t), cancellationToken).ConfigureAwait(false);
                await DelayAsync(pause, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await LiftAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Two-finger horizontal pinch around <paramref name="center"/>: each finger moves from
    /// <paramref name="startRadius"/> to <paramref name="endRadius"/> away from the center.
    /// </summary>
    public async ValueTask PinchAsync(AxisPoint center, int startRadius, int endRadius, TimeSpan duration, int steps = 20, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(steps, 1);
        var pause = duration / steps;
        try
        {
            for (var i = 0; i <= steps; i++)
            {
                var r = (int)(startRadius + ((endRadius - startRadius) * (double)i / steps));
                await DownAsync(Axis(center.X - r, center.Y), Axis(center.X + r, center.Y), cancellationToken).ConfigureAwait(false);
                await DelayAsync(pause, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await LiftAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Clamps (x, y) to the digitizer range.</summary>
    public static AxisPoint Axis(int x, int y) =>
        new((ushort)Math.Clamp(x, 0, HidConstants.AxisMax), (ushort)Math.Clamp(y, 0, HidConstants.AxisMax));

    private async ValueTask SendDownAsync(TouchContact[] contacts, CancellationToken cancellationToken)
    {
        _down = contacts;
        await _sink.SendAsync(contacts, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    // Truncates toward zero like the PoC's int(...).
    private static AxisPoint Lerp(AxisPoint a, AxisPoint b, double t) =>
        Axis((int)(a.X + ((b.X - a.X) * t)), (int)(a.Y + ((b.Y - a.Y) * t)));
}
