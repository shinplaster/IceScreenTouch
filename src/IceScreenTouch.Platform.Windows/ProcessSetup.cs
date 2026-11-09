// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>Process-wide settings the bridge needs.</summary>
public static class ProcessSetup
{
    /// <summary>
    /// Makes cursor and window coordinates physical pixels on every monitor (per-monitor v2). Must run
    /// before any window is created. Returns false if the awareness was already set (by a manifest or an
    /// earlier call); it is then left as it is.
    /// </summary>
    public static bool EnablePerMonitorDpiAwareness() => SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);

    /// <summary>
    /// Raises the system timer resolution to 1 ms until disposed. Without it, timers and delays have a
    /// granularity of about 15.6 ms on Windows, too coarse for the 4 ms bridge loop.
    /// </summary>
    public static IDisposable HighResolutionTimer() => new TimerResolution(1);

    private sealed class TimerResolution : IDisposable
    {
        private readonly uint _period;
        private bool _disposed;

        public TimerResolution(uint period)
        {
            _period = period;
            _ = timeBeginPeriod(period);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                _ = timeEndPeriod(_period);
            }
        }
    }
}
