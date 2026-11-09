using System.Diagnostics;
using IceScreenTouch.Core;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Turns what the bridge loop sees (<see cref="BridgeStatus"/>, every tick) into the <see cref="SwallowZone"/> the
/// mouse hook reads: the mirror window's top-level window and the picture on the screen, or null while the bridge
/// is suspended (another sender uses the phone) or there is no picture. Does the lookup once per change, not once
/// per tick, and hands over a new zone only when it differs, or as a heartbeat every <see cref="Heartbeat"/> so that
/// the zone never grows older than <see cref="SwallowZone.MaxAge"/> while the bridge runs. <see cref="Stop"/> takes
/// the zone away for good. Called from the bridge loop; <see cref="Stop"/> from any thread.
/// </summary>
public sealed class SwallowZonePublisher
{
    /// <summary>How often an unchanged zone is published again.</summary>
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMilliseconds(250);

    private readonly Action<SwallowZone?> _publish;
    private readonly Func<nint, nint> _rootOf;
    private readonly Func<long> _clock;
    private readonly Lock _gate = new();
    private bool _published;
    private bool _stopped;
    private nint _window;
    private RectD? _picture;
    private bool _suspended;
    private SwallowZone? _zone;
    private long _publishedAt;

    /// <summary>Creates a publisher.</summary>
    /// <param name="publish">Receives each new zone (the hook's volatile field).</param>
    /// <param name="rootOf">The top-level window of a window (default: Win32).</param>
    /// <param name="clock">Stopwatch timestamps (default: <see cref="Stopwatch.GetTimestamp"/>).</param>
    public SwallowZonePublisher(Action<SwallowZone?> publish, Func<nint, nint>? rootOf = null, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(publish);
        _publish = publish;
        _rootOf = rootOf ?? Win32Windows.RootOf;
        _clock = clock ?? Stopwatch.GetTimestamp;
    }

    /// <summary>Takes one tick's status.</summary>
    public void Publish(BridgeStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            var now = _clock();
            var unchanged = _published && status.Window == _window && status.Picture == _picture && status.Suspended == _suspended;
            if (unchanged && (_zone is null || Stopwatch.GetElapsedTime(_publishedAt, now) < Heartbeat))
            {
                return;
            }

            if (!unchanged)
            {
                (_published, _window, _picture, _suspended) = (true, status.Window, status.Picture, status.Suspended);
                var root = status.Window == 0 ? 0 : _rootOf(status.Window);
                _zone = !status.Suspended && status.Picture is { HasArea: true } picture && root != 0 ? new SwallowZone(root, picture) : null;
            }

            _publishedAt = now;
            _publish(_zone is null ? null : _zone with { Stamp = now });
        }
    }

    /// <summary>Takes the zone away and ignores later statuses (the session is stopping).</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            _zone = null;
            _publish(null);
        }
    }
}
