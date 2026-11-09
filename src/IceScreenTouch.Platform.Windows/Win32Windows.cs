using System.Runtime.InteropServices;
using IceScreenTouch.Core;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>Window queries and placement (physical pixels; call <see cref="ProcessSetup.EnablePerMonitorDpiAwareness"/> first).</summary>
public static partial class Win32Windows
{
    /// <summary>The top-level window that contains <paramref name="window"/> (itself if it is one); 0 if invalid.</summary>
    public static nint RootOf(nint window) => window == 0 ? 0 : GetAncestor(window, GaRoot);

    /// <summary>The top-level window under a screen point, or 0.</summary>
    public static nint RootWindowAt(int x, int y)
    {
        var child = WindowFromPoint(new Point { X = x, Y = y });
        return child == 0 ? 0 : GetAncestor(child, GaRoot);
    }

    /// <summary>The top-level window with the keyboard focus, or 0.</summary>
    public static nint ForegroundRoot() => RootOf(GetForegroundWindow());

    /// <summary>Client area of <paramref name="window"/> in screen pixels, or null.</summary>
    public static RectI? ClientRectOnScreen(nint window)
    {
        if (window == 0 || !GetClientRect(window, out var rc))
        {
            return null;
        }

        var origin = default(Point);
        if (!ClientToScreen(window, ref origin))
        {
            return null;
        }

        return new RectI(origin.X, origin.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    /// <summary>Window rectangle (with the frame) in screen pixels, or null.</summary>
    public static RectI? WindowRectOnScreen(nint window)
    {
        if (window == 0 || !GetWindowRect(window, out var r))
        {
            return null;
        }

        return new RectI(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>Work area (without the taskbar) of the monitor nearest to <paramref name="window"/>.</summary>
    public static RectI WorkAreaOf(nint window)
    {
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        _ = GetMonitorInfo(MonitorFromWindow(window, MonitorDefaultToNearest), ref info);
        var r = info.Work;
        return new RectI(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>
    /// The display scale of the monitor nearest to <paramref name="window"/> (1.0 = 96 DPI, 1.5 = 150 %). The
    /// monitor's, not the window's: Windows stretches a window that does not handle DPI by this much as well.
    /// </summary>
    public static double MonitorScaleOf(nint window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        return monitor != 0 && GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1.0;
    }

    /// <summary>
    /// The window is maximized, or covers its whole monitor (borderless full screen, as AirServer's full screen
    /// mode). Such windows are not resized for the picture.
    /// </summary>
    public static bool IsMaximizedOrFullScreen(nint window)
    {
        var root = RootOf(window);
        if (root == 0)
        {
            return false;
        }

        if (IsZoomed(root))
        {
            return true;
        }

        if (!GetWindowRect(root, out var r))
        {
            return false;
        }

        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(root, MonitorDefaultToNearest), ref info))
        {
            return false;
        }

        var m = info.Monitor;
        return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    /// <summary>
    /// Frame size and work area of the top-level window of <paramref name="window"/>, or null. The frame is what
    /// the top-level window has around the client area of <paramref name="window"/> itself: for a window hosted by
    /// a UWP frame (<see cref="HostedWindows"/>) that includes the frame's title bar.
    /// </summary>
    public static WindowMetrics? GetMetrics(nint window)
    {
        var root = RootOf(window);
        if (WindowRectOnScreen(root) is not { } wr || ClientRectOnScreen(window) is not { } client)
        {
            return null;
        }

        return new WindowMetrics(wr.Width - client.Width, wr.Height - client.Height, WorkAreaOf(root));
    }

    /// <summary>
    /// Resizes the top-level window of <paramref name="window"/> so that its client area becomes
    /// width x height, keeping the top-left corner unless the window would leave the work area.
    /// Returns false if the window could not be measured.
    /// </summary>
    public static bool SetClientSize(nint window, int width, int height)
    {
        var root = RootOf(window);
        if (GetMetrics(window) is not { } m || WindowRectOnScreen(root) is not { } wr)
        {
            return false;
        }

        var totalW = width + m.FrameWidth;
        var totalH = height + m.FrameHeight;
        var (x, y) = Geometry.ClampPosition(wr.Left, wr.Top, totalW, totalH, m.WorkArea);
        return SetWindowPos(root, 0, x, y, totalW, totalH, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>Puts the top-level window of <paramref name="window"/> above the non-topmost windows (or back among them).</summary>
    public static bool SetTopMost(nint window, bool topMost) =>
        SetWindowPos(RootOf(window), topMost ? HwndTopMost : HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>
    /// True if <paramref name="window"/> exists, is visible and is not minimized. A child window is minimized with
    /// its top-level window (IsIconic is false for the child itself).
    /// </summary>
    public static bool IsShown(nint window) => window != 0 && IsWindow(window) && IsWindowVisible(window) && !IsIconic(RootOf(window));

    /// <summary>The process that owns <paramref name="window"/>, or 0.</summary>
    public static int ProcessIdOf(nint window)
    {
        _ = GetWindowThreadProcessId(window, out var pid);
        return (int)pid;
    }

    /// <summary>All top-level windows, in z-order.</summary>
    public static unsafe IReadOnlyList<nint> TopLevelWindows()
    {
        var list = new List<nint>();
        var handle = GCHandle.Alloc(list);
        try
        {
            _ = EnumWindows(&Collect, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return list;
    }

    /// <summary>
    /// The biggest visible, non-minimized top-level window owned by process <paramref name="processId"/>, or 0. A
    /// UWP app has no top-level window of its own (ApplicationFrameHost owns the frame): without one, the window
    /// it draws inside a frame is returned instead (see <see cref="HostedWindows"/>).
    /// </summary>
    public static nint LargestVisibleWindowOf(int processId)
    {
        nint best = 0;
        long bestArea = 0;
        foreach (var w in TopLevelWindows())
        {
            if (ProcessIdOf(w) != processId || !IsShown(w) || ClientRectOnScreen(w) is not { Width: > 0, Height: > 0 } rc)
            {
                continue;
            }

            var area = (long)rc.Width * rc.Height;
            if (area > bestArea)
            {
                (best, bestArea) = (w, area);
            }
        }

        return best != 0 ? best : HostedWindows.LargestOf(processId);
    }

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [UnmanagedCallersOnly]
    private static int Collect(nint hwnd, nint lParam)
    {
        ((List<nint>)GCHandle.FromIntPtr(lParam).Target!).Add(hwnd);
        return 1;
    }
}
