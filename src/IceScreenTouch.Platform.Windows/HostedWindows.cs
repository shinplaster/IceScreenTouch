// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.InteropServices;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Windows of UWP apps. A UWP app has no top-level window of its own: ApplicationFrameHost.exe owns the visible
/// top-level <c>ApplicationFrameWindow</c> (the frame with the title bar), and the app's process owns the child
/// <c>Windows.UI.Core.CoreWindow</c> inside it, which is the content area. AirServer from the Microsoft Store is
/// one (2026-11-09: frame 1518x1175, title bar 1500x40, CoreWindow 1500x1125). The CoreWindow is the window to
/// follow: its client area is the picture's surroundings without the title bar, its top-level window (the frame) is
/// what the cursor, the focus and the z-order see, and PrintWindow draws it on its own.
/// </summary>
internal static class HostedWindows
{
    internal const string FrameClass = "ApplicationFrameWindow";
    internal const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    /// <summary>A window drawn by an app inside a UWP frame, as found by the enumeration.</summary>
    /// <param name="Window">The CoreWindow.</param>
    /// <param name="ProcessId">The process that owns the CoreWindow (the app, not ApplicationFrameHost).</param>
    /// <param name="Shown">The frame and the CoreWindow are visible, and the frame is not minimized.</param>
    /// <param name="Width">Client width in pixels.</param>
    /// <param name="Height">Client height in pixels.</param>
    internal readonly record struct Info(nint Window, int ProcessId, bool Shown, int Width, int Height);

    /// <summary>The biggest shown window with content among <paramref name="windows"/> that <paramref name="processId"/> owns, or 0.</summary>
    internal static nint Choose(IEnumerable<Info> windows, int processId)
    {
        ArgumentNullException.ThrowIfNull(windows);
        nint best = 0;
        long bestArea = 0;
        foreach (var w in windows)
        {
            if (w.ProcessId != processId || !w.Shown || w.Width <= 0 || w.Height <= 0)
            {
                continue;
            }

            var area = (long)w.Width * w.Height;
            if (area > bestArea)
            {
                (best, bestArea) = (w.Window, area);
            }
        }

        return best;
    }

    /// <summary>The biggest shown CoreWindow of process <paramref name="processId"/> inside a visible UWP frame, or 0.</summary>
    internal static nint LargestOf(int processId) => Choose(Enumerate(), processId);

    /// <summary>The CoreWindows inside the top-level UWP frames, in z-order of the frames.</summary>
    internal static IEnumerable<Info> Enumerate()
    {
        foreach (var frame in Win32Windows.TopLevelWindows())
        {
            if (!HasClass(frame, FrameClass) || !Win32Windows.IsShown(frame))
            {
                continue;
            }

            for (var child = FindWindowEx(frame, 0, CoreWindowClass, null); child != 0; child = FindWindowEx(frame, child, CoreWindowClass, null))
            {
                var client = Win32Windows.ClientRectOnScreen(child);
                yield return new Info(child, Win32Windows.ProcessIdOf(child), Win32Windows.IsShown(child), client?.Width ?? 0, client?.Height ?? 0);
            }
        }
    }

    private static unsafe bool HasClass(nint window, string name)
    {
        // Two more than the name: GetClassName leaves room for the terminator, so a longer class name is never cut to equal.
        var buffer = stackalloc char[name.Length + 2];
        var length = GetClassName(window, buffer, name.Length + 2);
        return length == name.Length && new ReadOnlySpan<char>(buffer, length).SequenceEqual(name);
    }
}
