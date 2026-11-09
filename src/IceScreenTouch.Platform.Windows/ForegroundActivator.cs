using System.Diagnostics;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>How a window was brought to the foreground (or that it could not be).</summary>
public enum ActivationMethod
{
    /// <summary>It was in front already.</summary>
    AlreadyForeground,

    /// <summary>SetForegroundWindow was enough.</summary>
    SetForegroundWindow,

    /// <summary>After attaching this thread's input to the foreground window's thread.</summary>
    AttachThreadInput,

    /// <summary>Both failed.</summary>
    Failed,
}

/// <summary>
/// Brings a window to the foreground without any synthetic input (no key presses that could reach the phone). A
/// press the mouse hook swallows never activates the window it was over, but keys go to the device only while the
/// mirror window is in front. Windows refuses SetForegroundWindow to a process that did not get the last input, so
/// the fallback attaches this thread's input queue to the foreground window's thread for the call.
/// </summary>
public sealed class ForegroundActivator
{
    private readonly ForegroundOperations _ops;
    private readonly Action<string> _log;
    private static readonly TimeSpan SupersedeAfter = TimeSpan.FromSeconds(3);

    private readonly Action<Action> _queue;
    private readonly Func<long> _clock;
    private readonly Lock _gate = new();
    private int _loggedMethods; // one bit per ActivationMethod: each outcome is logged once
    private bool _running;
    private long _startedAt;
    private int _generation;

    /// <summary>The activator for real windows.</summary>
    public ForegroundActivator(Action<string>? log = null)
        : this(ForegroundOperations.Win32, log)
    {
    }

    internal ForegroundActivator(ForegroundOperations ops, Action<string>? log = null, Action<Action>? queue = null, Func<long>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(ops);
        _queue = queue ?? (work => ThreadPool.UnsafeQueueUserWorkItem(_ => work(), null));
        _clock = clock ?? Stopwatch.GetTimestamp;
        _ops = ops;
        _log = log ?? (_ => { });
    }

    /// <summary>
    /// Asks for <paramref name="window"/> to be brought to the front, on a pool thread: the caller is a hook
    /// callback that must return at once. A request while one is being served is dropped (the next press asks again),
    /// unless the one being served has taken more than a few seconds: a hung foreground thread can block the calls
    /// for good, and later presses must still be served.
    /// </summary>
    public void RequestAsync(nint window)
    {
        int generation;
        lock (_gate)
        {
            if (_running && Stopwatch.GetElapsedTime(_startedAt, _clock()) < SupersedeAfter)
            {
                return;
            }

            (_running, _startedAt, generation) = (true, _clock(), ++_generation);
        }

        _queue(() => Run(window, generation));
    }

    /// <summary>Brings <paramref name="window"/> to the front now; says how.</summary>
    internal ActivationMethod Activate(nint window)
    {
        var method = Try(window);
        var bit = 1 << (int)method;
        if (method != ActivationMethod.AlreadyForeground && (Interlocked.Or(ref _loggedMethods, bit) & bit) == 0)
        {
            _log(method switch
            {
                ActivationMethod.SetForegroundWindow => "[hook] the mirror window was brought to the front with SetForegroundWindow",
                ActivationMethod.AttachThreadInput => "[hook] the mirror window was brought to the front with AttachThreadInput (SetForegroundWindow alone was refused)",
                _ => "[hook] the mirror window could not be brought to the front (SetForegroundWindow and AttachThreadInput both failed): keys stay with Windows: Alt + click the picture, or click the window outside the picture, to give it the keyboard focus",
            });
        }

        return method;
    }

    private void Run(nint window, int generation)
    {
        try
        {
            _ = Activate(window);
        }
#pragma warning disable CA1031 // A pool thread must not take the app down; the press was swallowed either way.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log($"[hook] bringing the mirror window to the front failed: {ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                if (_generation == generation)
                {
                    _running = false; // not a newer request's flag
                }
            }
        }
    }

    private ActivationMethod Try(nint window)
    {
        if (_ops.ForegroundRoot() == window)
        {
            return ActivationMethod.AlreadyForeground;
        }

        _ = _ops.SetForeground(window);
        if (_ops.ForegroundRoot() == window)
        {
            return ActivationMethod.SetForegroundWindow;
        }

        var foregroundThread = _ops.ForegroundThread();
        var self = _ops.CurrentThread();
        if (foregroundThread == 0 || foregroundThread == self || !_ops.Attach(self, foregroundThread, true))
        {
            return ActivationMethod.Failed;
        }

        try
        {
            _ = _ops.BringToTop(window);
            _ = _ops.SetForeground(window);
        }
        finally
        {
            _ = _ops.Attach(self, foregroundThread, false);
        }

        return _ops.ForegroundRoot() == window ? ActivationMethod.AttachThreadInput : ActivationMethod.Failed;
    }

    /// <summary>The Win32 calls, replaceable in tests.</summary>
    internal sealed record ForegroundOperations(
        Func<nint> ForegroundRoot,
        Func<nint, bool> SetForeground,
        Func<nint, bool> BringToTop,
        Func<uint> ForegroundThread,
        Func<uint> CurrentThread,
        Func<uint, uint, bool, bool> Attach)
    {
        public static ForegroundOperations Win32 { get; } = new(
            Win32Windows.ForegroundRoot,
            SetForegroundWindow,
            BringWindowToTop,
            () =>
            {
                var foreground = GetForegroundWindow();
                return foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
            },
            GetCurrentThreadId,
            AttachThreadInput);
    }
}
