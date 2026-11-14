// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Keeps a running capture session for each window (keyed by window handle) so that a burst of screenshots does not start one each
/// (about 26 ms, against 1 ms for a read on a kept session). A session that was not used for <see cref="IdleTime"/> is closed, and so
/// is every session of another window when a different window is asked for (the receiver's window changed), and a session whose use
/// threw. Thread-safe: uses, sweeps and the final dispose exclude each other, so a session is never closed while it is being read.
/// </summary>
/// <typeparam name="TSession">The session.</typeparam>
internal sealed class CaptureSessionCache<TSession> : IDisposable
    where TSession : class, IDisposable
{
    /// <summary>How long a session lives without being used.</summary>
    public static readonly TimeSpan IdleTime = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly TimeSpan _idle;
    private readonly Func<nint, TSession> _create;
    private readonly object _gate = new();
    private readonly Dictionary<nint, Entry> _entries = [];
    private readonly Action? _onEmpty;
    private readonly ITimer _timer;
    private bool _disposed;

    /// <summary>Creates the cache; sessions come from <paramref name="create"/> (called inside the lock).</summary>
    /// <param name="time">The clock and timers.</param>
    /// <param name="create">Starts a session for a window.</param>
    /// <param name="idle">How long an unused session lives (default <see cref="IdleTime"/>).</param>
    /// <param name="onEmpty">Called (inside the lock) when the last session has been closed by a sweep, a drop, a clear or a failed use, to let go of what only sessions need.</param>
    public CaptureSessionCache(TimeProvider time, Func<nint, TSession> create, TimeSpan? idle = null, Action? onEmpty = null)
    {
        _time = time;
        _create = create;
        _idle = idle ?? IdleTime;
        _onEmpty = onEmpty;
        _timer = time.CreateTimer(_ => Sweep(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>The number of sessions kept.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="use"/> on the session of <paramref name="key"/> (started if there is none), with the sessions of all other
    /// keys closed. If <paramref name="create"/> or <paramref name="use"/> throws, that window's session is closed and the exception goes on.
    /// </summary>
    public TResult Use<TResult>(nint key, Func<TSession, TResult> use)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var other in _entries.Keys.Where(k => k != key).ToList())
            {
                Remove(other);
            }

            try
            {
                if (!_entries.TryGetValue(key, out var entry))
                {
                    entry = new Entry(_create(key), _time.GetTimestamp());
                    _entries[key] = entry;
                }

                var result = use(entry.Session);
                entry.LastUse = _time.GetTimestamp();
                _ = _timer.Change(_idle, Timeout.InfiniteTimeSpan);
                return result;
            }
            catch
            {
                Remove(key);
                NotifyIfEmpty();
                throw;
            }
        }
    }

    /// <summary>
    /// Closes every session (the cache stays usable). <paramref name="whileLocked"/> runs after that inside the same lock, so no session
    /// can be started in between (for example on a device that is about to be disposed).
    /// </summary>
    /// <param name="whileLocked">Run after the sessions are closed, before another use can start one.</param>
    public void Clear(Action? whileLocked = null)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.ToList())
            {
                Remove(key);
            }

            NotifyIfEmpty();
            whileLocked?.Invoke();
        }
    }

    /// <summary>Closes the session of <paramref name="key"/> (if any).</summary>
    public void Drop(nint key)
    {
        lock (_gate)
        {
            Remove(key);
            NotifyIfEmpty();
        }
    }

    /// <summary>
    /// Closes the sessions that have been unused for <see cref="IdleTime"/>, and sets the timer for the next one. Called by the timer;
    /// skipped when a use is in progress (that session is not idle).
    /// </summary>
    public void Sweep()
    {
        if (!Monitor.TryEnter(_gate))
        {
            return;
        }

        try
        {
            if (_disposed)
            {
                return;
            }

            var next = TimeSpan.MaxValue;
            foreach (var key in _entries.Keys.ToList())
            {
                var unused = _time.GetElapsedTime(_entries[key].LastUse);
                if (unused >= _idle)
                {
                    Remove(key);
                }
                else
                {
                    next = TimeSpan.FromTicks(Math.Min(next.Ticks, (_idle - unused).Ticks));
                }
            }

            _ = _timer.Change(next == TimeSpan.MaxValue ? Timeout.InfiniteTimeSpan : next, Timeout.InfiniteTimeSpan);
            NotifyIfEmpty();
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Closes every session; later uses fail with <see cref="ObjectDisposedException"/>.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var key in _entries.Keys.ToList())
            {
                Remove(key);
            }
        }

        _timer.Dispose();
    }

    // Closes one session (inside the lock). A failing dispose must not keep the others open.
    private void Remove(nint key)
    {
        if (!_entries.Remove(key, out var entry))
        {
            return;
        }

        try
        {
            entry.Session.Dispose();
        }
#pragma warning disable CA1031 // whatever a session throws on closing, it is out of the cache already and the others must still be closed
        catch (Exception)
#pragma warning restore CA1031
        {
            // Already gone with its window, or half closed: nothing more can be done with it.
        }
    }

    // Inside the lock: the last session is gone (a sweep, a drop, a clear or a failed use).
    private void NotifyIfEmpty()
    {
        if (_entries.Count != 0 || _onEmpty is null)
        {
            return;
        }

        try
        {
            _onEmpty();
        }
#pragma warning disable CA1031 // letting go of spare memory is best effort
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private sealed class Entry(TSession session, long lastUse)
    {
        public TSession Session { get; } = session;

        public long LastUse { get; set; } = lastUse;
    }
}
