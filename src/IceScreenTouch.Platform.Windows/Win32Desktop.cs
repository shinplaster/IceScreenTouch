// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary><see cref="IDesktop"/> on Win32. Shortcuts: Home = home, Ctrl+R = rotate.</summary>
/// <param name="keyDown">
/// Key state to use instead of Windows' own (<see cref="KeyboardForwarder.IsDown"/>: the keys it swallows never
/// reach Windows' key state). Null = GetAsyncKeyState.
/// </param>
/// <param name="pressesSince">
/// How many times a shortcut was pressed since the last look (<see cref="KeyboardForwarder.TakeShortcutPresses"/>):
/// a tap shorter than the bridge's wait still counts, and so does a second press made while the bridge was busy with
/// the first. It may answer null (no hook, nothing is counted). Null = the key state only.
/// </param>
public sealed class Win32Desktop(Func<int, bool>? keyDown = null, Func<ShortcutKey, int?>? pressesSince = null) : IDesktop
{
    private readonly Func<int, bool> _keyDown = keyDown ?? IsVirtualKeyDown;

    private PointD _lastCursor;

    /// <inheritdoc/>
    /// <remarks>
    /// The last known position when Windows refuses (another desktop is in front: UAC, Ctrl+Alt+Del, Win+L); (0, 0)
    /// would drag a finger that is down to the corner of the picture.
    /// </remarks>
    public PointD CursorPosition => GetCursorPos(out var p) ? _lastCursor = new PointD(p.X, p.Y) : _lastCursor;

    /// <inheritdoc/>
    public bool IsMaximizedOrFullScreen(nint window) => Win32Windows.IsMaximizedOrFullScreen(window);

    /// <summary>True while the virtual key <paramref name="vk"/> is held.</summary>
    public static bool IsVirtualKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <inheritdoc/>
    /// <remarks>
    /// The key state only. The counted presses come from <see cref="TakeShortcutPresses"/>: mixing the two here made a
    /// second press, made while the bridge was busy with the first, look like the same long hold (review, 2026-11-07).
    /// </remarks>
    public bool IsKeyDown(ShortcutKey key) => key switch
    {
        ShortcutKey.Home => _keyDown(VkHome),
        ShortcutKey.Rotate => _keyDown(VkR) && _keyDown(VkControl),
        _ => false,
    };

    /// <inheritdoc/>
    public int? TakeShortcutPresses(ShortcutKey key) => pressesSince?.Invoke(key);

    /// <inheritdoc/>
    public bool IsForeground(nint window) => window != 0 && Win32Windows.ForegroundRoot() == Win32Windows.RootOf(window);

    /// <inheritdoc/>
    public bool IsVisibleAt(nint window, PointD screen) =>
        window != 0 && Win32Windows.RootWindowAt((int)Math.Floor(screen.X), (int)Math.Floor(screen.Y)) == Win32Windows.RootOf(window);

    /// <inheritdoc/>
    public RectI? GetClientRect(nint window) => Win32Windows.ClientRectOnScreen(window);

    /// <inheritdoc/>
    public WindowMetrics? GetMetrics(nint window) => Win32Windows.GetMetrics(window);

    /// <inheritdoc/>
    public void SetClientSize(nint window, int width, int height) => Win32Windows.SetClientSize(window, width, height);
}
