// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Windows.Forms;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;

// Windows, the cursor and the hook are process-wide: run the test classes one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IceScreenTouch.Windows.Tests;

internal static class TestSetup
{
    /// <summary>Physical pixels for the whole test process, before any window exists.</summary>
    [ModuleInitializer]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2255", Justification = "process-wide DPI mode must be set before any window")]
    internal static void Init()
    {
        ProcessSetup.EnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
    }
}

/// <summary>Runs only with ICESCREENTOUCH_INPUT_TESTS=1: the test moves the real cursor and clicks.</summary>
internal sealed class InputFactAttribute : FactAttribute
{
    public const string Variable = "ICESCREENTOUCH_INPUT_TESTS";

    public InputFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"moves the real cursor; set {Variable}=1 to run";
        }
    }
}

/// <summary>
/// Runs only on a desktop where Windows.Graphics.Capture works (an interactive session on Windows 10 1803 or later); otherwise it is
/// reported as skipped with the reason, never passed without checking anything.
/// </summary>
internal sealed class WgcFactAttribute : FactAttribute
{
    public WgcFactAttribute()
    {
        if (!Environment.UserInteractive)
        {
            Skip = "no interactive desktop (the process is not in a user session)";
        }
        else if (!WgcSession.IsSupported())
        {
            Skip = "Windows.Graphics.Capture is not supported on this system";
        }
    }
}

/// <summary>Runs only where a monitor is scaled above 100 % (a window that follows no DPI is stretched there); skipped with the reason otherwise.</summary>
internal class ScaledMonitorFactAttribute : FactAttribute
{
    public ScaledMonitorFactAttribute()
        : this(needsWgc: false)
    {
    }

    protected ScaledMonitorFactAttribute(bool needsWgc)
    {
        if (!Environment.UserInteractive)
        {
            Skip = "no interactive desktop (the process is not in a user session)";
        }
        else if (needsWgc && !WgcSession.IsSupported())
        {
            Skip = "Windows.Graphics.Capture is not supported on this system";
        }
        else if (!HasScaledMonitor())
        {
            Skip = "every monitor of this machine is at 100 % scale: a stretched window cannot be set up";
        }
    }

    /// <summary>A monitor whose DPI is above 96.</summary>
    public static bool HasScaledMonitor() => Screen.AllScreens.Any(s => MonitorDpi(s) > 96);

    private static uint MonitorDpi(Screen screen)
    {
        var point = new System.Drawing.Point(screen.Bounds.Left + (screen.Bounds.Width / 2), screen.Bounds.Top + (screen.Bounds.Height / 2));
        var monitor = MonitorFromPoint(point, 2);
        return monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(System.Drawing.Point point, uint flags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}

/// <summary><see cref="ScaledMonitorFactAttribute"/> and Windows.Graphics.Capture must work.</summary>
internal sealed class ScaledMonitorWgcFactAttribute : ScaledMonitorFactAttribute
{
    public ScaledMonitorWgcFactAttribute()
        : base(needsWgc: true)
    {
    }
}

/// <summary>A WinForms window on its own UI thread, standing in for the UxPlay window.</summary>
internal sealed class StandInWindow : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new();
    private Form? _form;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool NativeSetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    private StandInWindow(Rectangle client, bool topMost, Color color, bool dpiUnaware)
    {
        _thread = new Thread(() =>
        {
            if (dpiUnaware)
            {
                _ = SetThreadDpiAwarenessContext(-1); // DPI_AWARENESS_CONTEXT_UNAWARE: Windows stretches the window on a monitor with a scale above 100 %
            }

            var form = new Form
            {
                Text = "IceScreen Touch test stand-in",
                StartPosition = FormStartPosition.Manual,
                Location = client.Location,
                TopMost = topMost,
                BackColor = color,
                ShowInTaskbar = false,
                AutoScaleMode = AutoScaleMode.None,
                FormBorderStyle = FormBorderStyle.Sizable,
            };
            form.ClientSize = client.Size;
            form.Shown += (_, _) =>
            {
                // Form.TopMost alone did not give WS_EX_TOPMOST here (a maximized window stayed above it).
                if (topMost)
                {
                    Win32Windows.SetTopMost(form.Handle, true);
                }

                _shown.Set();
            };
            _form = form;
            Application.Run(form);
        })
        { IsBackground = true, Name = "stand-in-window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_shown.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("the stand-in window did not show");
        }

        Thread.Sleep(100); // let it paint and settle
    }

    public nint Handle => Invoke(() => _form!.Handle);

    /// <summary>Client area in screen pixels, from WinForms (independent of the Win32 code under test).</summary>
    public Rectangle ClientOnScreen => Invoke(() => _form!.RectangleToScreen(_form.ClientRectangle));

    public Rectangle WorkingArea => Invoke(() => Screen.FromHandle(_form!.Handle).WorkingArea);

    /// <summary>Window size minus client size, from WinForms.</summary>
    public Size Frame => Invoke(() => _form!.Size - _form.ClientSize);

    public static StandInWindow Show(Rectangle client, bool topMost = true, Color? color = null, bool dpiUnaware = false) =>
        new(client, topMost, color ?? Color.FromArgb(0x22, 0x22, 0x33), dpiUnaware);

    public void Activate() => Invoke(() =>
    {
        _form!.Activate();
        return 0;
    });

    /// <summary>Puts a colored block at <paramref name="bounds"/> (client pixels), replacing the previous one.</summary>
    public void ShowBlock(Rectangle bounds, Color color) => Invoke(() =>
    {
        _form!.Controls.Clear();
        _form.Controls.Add(new Panel { Bounds = bounds, BackColor = color });
        _form.Refresh();
        return 0;
    });

    /// <summary>Moves the window (top-level position in physical screen pixels, Win32).</summary>
    public void MoveTo(Point location) => _ = NativeSetWindowPos(Handle, 0, location.X, location.Y, 0, 0, 0x0001 | 0x0004 | 0x0010);

    /// <summary>Resizes the client area (WinForms).</summary>
    public void SetClientSize(Size size) => Invoke(() =>
    {
        _form!.ClientSize = size;
        return 0;
    });

    /// <summary>Maximizes or restores the window.</summary>
    public void SetMaximized(bool maximized) => Invoke(() =>
    {
        _form!.WindowState = maximized ? FormWindowState.Maximized : FormWindowState.Normal;
        return 0;
    });

    /// <summary>Minimizes or restores the window.</summary>
    public void SetMinimized(bool minimized) => Invoke(() =>
    {
        _form!.WindowState = minimized ? FormWindowState.Minimized : FormWindowState.Normal;
        return 0;
    });

    /// <summary>Makes the window borderless and as large as its monitor (a full screen receiver).</summary>
    public void CoverMonitor() => Invoke(() =>
    {
        _form!.FormBorderStyle = FormBorderStyle.None;
        _form.Bounds = Screen.FromHandle(_form.Handle).Bounds;
        return 0;
    });

    public T Invoke<T>(Func<T> f) => (T)_form!.Invoke(f)!;

    public void Dispose()
    {
        try
        {
            _form?.Invoke(() => _form.Close());
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        _shown.Dispose();
    }
}

/// <summary>A touch report with the real time it was sent.</summary>
internal sealed record SentFrame(TimeSpan At, TouchContact[] Contacts)
{
    public bool IsEmpty => Contacts.Length == 0;

    public bool IsDown => Contacts.Length > 0 && Contacts.All(c => c.Tip);

    public bool IsLift => Contacts.Length > 0 && Contacts.All(c => !c.Tip);

    public AxisPoint First => new(Contacts[0].X, Contacts[0].Y);

    public string Kind => IsEmpty ? "empty" : IsDown ? "down" : "lift";
}

/// <summary>Records reports in real time; <see cref="SendDelay"/> simulates a slow BLE link.</summary>
internal sealed class RealTimeSink : ITouchSink
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _lock = new();
    private readonly List<SentFrame> _frames = [];

    public bool IsSubscribed => true;

    public TimeSpan SendDelay { get; init; }

    public IReadOnlyList<SentFrame> Frames
    {
        get
        {
            lock (_lock)
            {
                return [.. _frames];
            }
        }
    }

    public IReadOnlyList<SentFrame> Downs => [.. Frames.Where(f => f.IsDown)];

    public void Clear()
    {
        lock (_lock)
        {
            _frames.Clear();
        }
    }

    public async ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _frames.Add(new SentFrame(_clock.Elapsed, contacts.ToArray()));
        }

        if (SendDelay > TimeSpan.Zero)
        {
            await Task.Delay(SendDelay, cancellationToken).ConfigureAwait(false);
        }
    }
}

/// <summary>A video source that is the stand-in window, with a size the test controls.</summary>
internal sealed class StandInVideo(nint window) : IVideoSource
{
    private readonly Lock _lock = new();
    private SizeD? _size = new SizeD(498, 1080);

    public string Name => "stand-in";

    public SizeD? VideoSize
    {
        get
        {
            lock (_lock)
            {
                return _size;
            }
        }

        set
        {
            lock (_lock)
            {
                _size = value;
            }
        }
    }

    public nint FindWindow() => window;
}

/// <summary>
/// A topmost, layered, click-through, tool window over some screen area (a notification, a recording overlay): hit-testing does
/// not find it, but a copy from the screen contains it.
/// </summary>
internal sealed class ClickThroughOverlay : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _shown = new();
    private Form? _form;

    public ClickThroughOverlay(Rectangle bounds, Color color)
    {
        _thread = new Thread(() =>
        {
            var form = new OverlayForm { StartPosition = FormStartPosition.Manual, Bounds = bounds, BackColor = color };
            form.Shown += (_, _) => _shown.Set();
            _form = form;
            Application.Run(form);
        })
        { IsBackground = true, Name = "overlay-window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        if (!_shown.Wait(TimeSpan.FromSeconds(10)))
        {
            throw new TimeoutException("the overlay did not show");
        }

        Thread.Sleep(300); // let the compositor draw it
    }

    public void Dispose()
    {
        try
        {
            _form?.Invoke(() => _form.Close());
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        _shown.Dispose();
    }

    private sealed class OverlayForm : Form
    {
        public OverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            Opacity = 0.99; // layered
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= 0x00000020 | 0x00080000 | 0x00000080 | 0x08000000 | 0x00000008; // TRANSPARENT | LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
                return p;
            }
        }
    }
}
