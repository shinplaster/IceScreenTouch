using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Video;

/// <summary>
/// Finds the largest visible window of a process, and caches it: the bridge asks every 4 ms, while a full
/// scan of the top-level windows is only repeated when the cached window is gone, hidden or no longer the
/// process's, and at most every <see cref="RescanInterval"/> otherwise (a bigger window may have appeared).
/// </summary>
internal sealed class WindowTracker
{
    private static readonly TimeSpan RescanInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MissingRescanInterval = TimeSpan.FromMilliseconds(50);

    private readonly TimeProvider _time;
    private nint _window;
    private int _processId;
    private long _scannedAt;

    public WindowTracker(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    public nint Find(int processId)
    {
        if (processId <= 0)
        {
            _window = 0;
            return 0;
        }

        var sameProcess = _processId == processId;
        var valid = _window != 0 && sameProcess && Win32Windows.IsShown(_window) && Win32Windows.ProcessIdOf(_window) == processId;
        if (!valid)
        {
            _window = 0;
        }

        // Without a window, scan a little more often, but not on every 4 ms tick.
        var interval = valid ? RescanInterval : MissingRescanInterval;
        if (!sameProcess || _time.GetElapsedTime(_scannedAt) >= interval)
        {
            _window = Win32Windows.LargestVisibleWindowOf(processId);
            _processId = processId;
            _scannedAt = _time.GetTimestamp();
        }

        return _window;
    }
}
