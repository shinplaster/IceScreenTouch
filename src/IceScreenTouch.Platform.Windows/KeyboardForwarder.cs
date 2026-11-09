// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Decides, for every key the low-level hook sees, whether it goes to the device (and is swallowed) or stays with
/// Windows, and translates it. Free of Win32 calls so it can be tested; <see cref="KeyboardHook"/> feeds it.
/// </summary>
/// <remarks>
/// While the mirror window has the focus: Home = the home button and Ctrl+R = rotation (both handled by the bridge,
/// never sent); keys with Alt or Win stay with Windows (Alt+Tab, Win+D...), and so do F1..F24 and the media keys;
/// Ctrl is sent as Command (Ctrl+C = Command+C) except Ctrl+Space, which stays Ctrl+Space (the input source
/// switch on iOS), and so does the Zenkaku/Hankaku key. Shift and Ctrl themselves are never swallowed (the mouse
/// hook reads Ctrl for the pinch): each key carries the modifiers held when it went down. Called on the hook
/// thread only.
/// </remarks>
public sealed class KeyboardForwarder
{
    private readonly bool[] _down = new bool[256];
    private readonly Dictionary<int, KeyboardInputEvent> _forwarded = [];
    private readonly HashSet<int> _swallowed = [];

    // Presses of Home and Ctrl+R not yet taken by the bridge (written on the hook thread, taken on the bridge's).
    private readonly int[] _shortcutPresses = new int[2];
    private readonly Func<bool> _mirrorFocused;
    private readonly Func<int, int, bool, char?> _toChar;
    private readonly Action<KeyboardInputEvent> _emit;

    /// <summary>Creates the forwarder.</summary>
    /// <param name="mirrorFocused">True while the mirror window has the keyboard focus.</param>
    /// <param name="toChar">
    /// The character the PC's keyboard layout types for (virtual key, scan code, Shift held), or null. Ctrl, Alt and
    /// Caps Lock must not be applied.
    /// </param>
    /// <param name="emit">Receives the translated events (must be quick, e.g. a channel write).</param>
    public KeyboardForwarder(Func<bool> mirrorFocused, Func<int, int, bool, char?> toChar, Action<KeyboardInputEvent> emit)
    {
        ArgumentNullException.ThrowIfNull(mirrorFocused);
        ArgumentNullException.ThrowIfNull(toChar);
        ArgumentNullException.ThrowIfNull(emit);
        _mirrorFocused = mirrorFocused;
        _toChar = toChar;
        _emit = emit;
    }

    /// <summary>Keys go to the device (off: only the shortcuts are taken, everything else stays with Windows).</summary>
    public bool Forwarding { get; set; } = true;

    /// <summary>The hardware keyboard layout the device is set to (how characters become keys).</summary>
    public HidKeyboardLayout DeviceLayout { get; set; } = HidKeyboardLayout.Us;

    /// <summary>
    /// True while <paramref name="vk"/> is held, including keys this forwarder swallowed (Windows' own key state
    /// never sees those). <see cref="VirtualKeys.Shift"/> and <see cref="VirtualKeys.Control"/> mean either side.
    /// </summary>
    public bool IsDown(int vk) => vk switch
    {
        VirtualKeys.Shift => _down[VirtualKeys.LeftShift] || _down[VirtualKeys.RightShift],
        VirtualKeys.Control => _down[VirtualKeys.LeftControl] || _down[VirtualKeys.RightControl],
        VirtualKeys.Menu => _down[VirtualKeys.LeftMenu] || _down[VirtualKeys.RightMenu],
        >= 0 and < 256 => _down[vk],
        _ => false,
    };

    /// <summary>
    /// Whether the shortcut was pressed since the last call (any thread): the presses of Home and Ctrl+R that
    /// this forwarder swallowed, even if they are up again now.
    /// </summary>
    public bool TakeShortcutPress(ShortcutKey key) => TakeShortcutPresses(key) > 0;

    /// <summary>
    /// How many times the shortcut was pressed since the last call (any thread). Each press counts once, also when
    /// it is made while the bridge is still busy with the one before.
    /// </summary>
    public int TakeShortcutPresses(ShortcutKey key) =>
        Interlocked.Exchange(ref _shortcutPresses[key == ShortcutKey.Home ? 0 : 1], 0);

    /// <summary>Handles one key transition. Returns true to swallow it (Windows and the focused window never see it).</summary>
    public bool OnKey(int vk, int scanCode, bool down)
    {
        if (vk is <= 0 or >= 256)
        {
            return false;
        }

        _down[vk] = down;
        if (!down)
        {
            if (_forwarded.Remove(vk, out var pressed))
            {
                _emit(pressed with { Down = false });
                return true;
            }

            return _swallowed.Remove(vk);
        }

        if (_forwarded.ContainsKey(vk) || _swallowed.Contains(vk))
        {
            return true; // auto-repeat: the device repeats a held key by itself
        }

        if (!_mirrorFocused() || IsWindowsKey(vk) || IsDown(VirtualKeys.Menu) || IsDown(VirtualKeys.LeftWin) || IsDown(VirtualKeys.RightWin))
        {
            return false;
        }

        if (vk == VirtualKeys.Home || (vk == VirtualKeys.R && IsDown(VirtualKeys.Control)))
        {
            // Shortcuts: the bridge takes the presses counted here (one each): a quick tap while its loop
            // waits (a long press holds it for 600 ms) would be up again before it looks, and lost to Windows too.
            _ = Interlocked.Increment(ref _shortcutPresses[vk == VirtualKeys.Home ? 0 : 1]);
            _swallowed.Add(vk);
            return true;
        }

        if (!Forwarding || Translate(vk, scanCode) is not { } e)
        {
            return false;
        }

        if (IsInputSourceToggle(vk))
        {
            // Zenkaku/Hankaku may never report its release: send a whole tap now. Not remembered as held: with no
            // release to forget it, every later press (in any window) would be taken for auto-repeat and swallowed.
            // A release that does come goes on to Windows, which ignores it.
            _emit(e);
            _emit(e with { Down = false });
            return true;
        }

        _forwarded[vk] = e;
        _emit(e);
        return true;
    }

    /// <summary>
    /// What a key sends, with the modifiers held now already applied, or null when it has nothing to send (it stays
    /// with Windows; Shift and Ctrl themselves are among those).
    /// </summary>
    internal KeyboardInputEvent? Translate(int vk, int scanCode)
    {
        if (vk is VirtualKeys.Shift or VirtualKeys.LeftShift or VirtualKeys.RightShift
            or VirtualKeys.Control or VirtualKeys.LeftControl or VirtualKeys.RightControl)
        {
            return null;
        }

        var shift = IsDown(VirtualKeys.Shift);
        var control = IsDown(VirtualKeys.Control);
        if (IsInputSourceToggle(vk) || (vk == VirtualKeys.Space && control))
        {
            return new(vk, true, new HidKey(HidKeyboard.Space, HidModifiers.LeftControl), KeyRole.Exact);
        }

        // Ctrl is Command on the device.
        var command = control ? HidModifiers.LeftGui : (byte)0;
        if (NamedKey(vk) is { } usage)
        {
            return new(vk, true, new HidKey(usage, (byte)(command | (shift ? HidModifiers.LeftShift : 0))), KeyRole.Other);
        }

        // The character already has the PC's Shift in it; the device's key brings its own Shift.
        var c = _toChar(vk, scanCode, shift);
        return c is { } ch && ch is not ('\n' or '\t' or ' ') && HidKeyboard.TryMap(ch, DeviceLayout, out var key)
            ? new KeyboardInputEvent(vk, true, key with { Modifiers = (byte)(key.Modifiers | command) }, KeyRole.Character)
            : null;
    }

    private static bool IsInputSourceToggle(int vk) => vk is VirtualKeys.OemAuto or VirtualKeys.OemEnlw or VirtualKeys.Kanji;

    // Alt, Win, function keys, and everything from the browser / media / volume block stays with Windows.
    private static bool IsWindowsKey(int vk) =>
        vk is VirtualKeys.Menu or VirtualKeys.LeftMenu or VirtualKeys.RightMenu or VirtualKeys.LeftWin or VirtualKeys.RightWin or VirtualKeys.Apps
        or (>= VirtualKeys.F1 and <= VirtualKeys.F24)
        or (>= VirtualKeys.BrowserBack and <= VirtualKeys.LaunchApp2)
        or VirtualKeys.Snapshot or VirtualKeys.Pause or VirtualKeys.NumLock or VirtualKeys.Scroll;

    private static byte? NamedKey(int vk) => vk switch
    {
        VirtualKeys.Return => HidKeyboard.Enter,
        VirtualKeys.Escape => HidKeyboard.Escape,
        VirtualKeys.Back => HidKeyboard.Backspace,
        VirtualKeys.Tab => HidKeyboard.Tab,
        VirtualKeys.Space => HidKeyboard.Space,
        VirtualKeys.Capital => HidKeyboard.CapsLock,
        VirtualKeys.Delete => HidKeyboard.Delete,
        VirtualKeys.End => 0x4D,
        VirtualKeys.Prior => 0x4B,
        VirtualKeys.Next => 0x4E,
        VirtualKeys.Left => HidKeyboard.LeftArrow,
        VirtualKeys.Up => HidKeyboard.UpArrow,
        VirtualKeys.Right => HidKeyboard.RightArrow,
        VirtualKeys.Down => HidKeyboard.DownArrow,
        _ => null,
    };
}

/// <summary>The Windows virtual-key codes the keyboard forwarding uses.</summary>
public static class VirtualKeys
{
    public const int Back = 0x08;
    public const int Tab = 0x09;
    public const int Return = 0x0D;
    public const int Shift = 0x10;
    public const int Control = 0x11;
    public const int Menu = 0x12;
    public const int Pause = 0x13;
    public const int Capital = 0x14;
    public const int Kanji = 0x19;
    public const int Escape = 0x1B;
    public const int Space = 0x20;
    public const int Prior = 0x21;
    public const int Next = 0x22;
    public const int End = 0x23;
    public const int Home = 0x24;
    public const int Left = 0x25;
    public const int Up = 0x26;
    public const int Right = 0x27;
    public const int Down = 0x28;
    public const int Snapshot = 0x2C;
    public const int Delete = 0x2E;
    public const int A = 0x41;
    public const int R = 0x52;
    public const int LeftWin = 0x5B;
    public const int RightWin = 0x5C;
    public const int Apps = 0x5D;
    public const int F1 = 0x70;
    public const int F24 = 0x87;
    public const int NumLock = 0x90;
    public const int Scroll = 0x91;
    public const int LeftShift = 0xA0;
    public const int RightShift = 0xA1;
    public const int LeftControl = 0xA2;
    public const int RightControl = 0xA3;
    public const int LeftMenu = 0xA4;
    public const int RightMenu = 0xA5;
    public const int BrowserBack = 0xA6;
    public const int LaunchApp2 = 0xB7;
    public const int Oem1 = 0xBA;
    public const int OemAuto = 0xF3;
    public const int OemEnlw = 0xF4;
}
