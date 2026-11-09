// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Where the mouse hook swallows button presses: over the picture of the mirror window. Immutable, so that the
/// bridge side can publish a new one through a volatile field and the hook thread can read it without a lock.
/// </summary>
/// <param name="Root">The top-level window of the mirror window: the one that must be on top at the cursor.</param>
/// <param name="Picture">The picture in screen pixels.</param>
/// <param name="Stamp">When the zone was published (<see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>).</param>
public sealed record SwallowZone(nint Root, RectD Picture, long Stamp = 0)
{
    /// <summary>
    /// How long a zone counts without being published again. The bridge publishes every few milliseconds while it
    /// runs (<see cref="SwallowZonePublisher"/>), so a zone that old belongs to a bridge that stopped or hangs: the
    /// presses it would swallow must reach the windows again.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMilliseconds(1500);

    /// <summary>The zone is recent enough to swallow presses at <paramref name="now"/> (a Stopwatch timestamp).</summary>
    public bool IsFresh(long now) => System.Diagnostics.Stopwatch.GetElapsedTime(Stamp, now) <= MaxAge;
}

/// <summary>
/// Decides, for the low-level mouse hook, which left and right button presses never reach the window under the
/// cursor. Some receivers move their own window when the picture is dragged (Reflector 4 and 5KPlayer's Airplay.exe,
/// 2026-11-09), which makes a swipe impossible: the press is kept from them (the bridge still sees it). A swallowed
/// press takes its release with it, wherever the cursor is then, and a release whose press went on goes on too, so
/// that no program sees a release without a press or a press without a release. Not thread-safe: the hook thread
/// only. It must answer at once (Windows drops slow hooks), so it asks nothing but the window under the cursor.
/// </summary>
/// <param name="rootAt">The top-level window under a screen point (0 = none).</param>
public sealed class ClickSwallower(Func<int, int, nint> rootAt)
{
    private bool _leftHeld;
    private bool _rightHeld;

    /// <summary>The swallower for real windows.</summary>
    public ClickSwallower()
        : this(Win32Windows.RootWindowAt)
    {
    }

    /// <summary>
    /// True when the hook must not pass <paramref name="e"/> on. <paramref name="zone"/> is null when nothing is
    /// swallowed now (another receiver, the bridge is suspended, there is no picture), and is taken as null when it
    /// is older than <see cref="SwallowZone.MaxAge"/> at <paramref name="now"/> (a Stopwatch timestamp).
    /// </summary>
    public bool ShouldSwallow(MouseInputEvent e, SwallowZone? zone, long now)
    {
        if (zone is not null && !zone.IsFresh(now))
        {
            zone = null;
        }

        switch (e.Kind)
        {
            case MouseInputKind.Press:
                // Alt + press goes to the receiver (to move its window): it is not swallowed, nor is its release.
                var swallow = !e.Alt
                    && zone is not null
                    && zone.Picture.Contains(e.Position)
                    && rootAt((int)Math.Floor(e.Position.X), (int)Math.Floor(e.Position.Y)) == zone.Root;
                SetHeld(e.Button, swallow);
                return swallow;
            case MouseInputKind.Release:
                var held = e.Button == MouseButton.Left ? _leftHeld : _rightHeld;
                SetHeld(e.Button, false);
                return held;
            default:
                return false; // moves and the wheel are never swallowed
        }
    }

    private void SetHeld(MouseButton button, bool value)
    {
        if (button == MouseButton.Left)
        {
            _leftHeld = value;
        }
        else
        {
            _rightHeld = value;
        }
    }
}
