using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Windows.Tests;

/// <summary>The forwarder's decisions, with a fake US PC keyboard layout (no hook, no Win32).</summary>
public class KeyboardForwarderTests
{
    private readonly List<KeyboardInputEvent> _sent = [];
    private bool _focused = true;

    private KeyboardForwarder Create(HidKeyboardLayout device = HidKeyboardLayout.Us) =>
        new(() => _focused, UsPc, _sent.Add) { DeviceLayout = device };

    // A US PC layout for the keys the tests use.
    private static char? UsPc(int vk, int scan, bool shift) => vk switch
    {
        >= 0x41 and <= 0x5A => (char)(shift ? vk : vk + 32),
        0x32 => shift ? '@' : '2',
        0xBA => shift ? ':' : ';',
        0xDE => shift ? '"' : '\'',
        _ => null,
    };

    private static bool Tap(KeyboardForwarder f, int vk)
    {
        var swallowed = f.OnKey(vk, 0, true);
        f.OnKey(vk, 0, false);
        return swallowed;
    }

    [Fact]
    public void ALetterIsSentAndSwallowed()
    {
        var f = Create();

        Assert.True(Tap(f, VirtualKeys.A));
        Assert.Equal([new(VirtualKeys.A, true, new HidKey(0x04), KeyRole.Character), new(VirtualKeys.A, false, new HidKey(0x04), KeyRole.Character)], _sent);
    }

    [Fact]
    public void NothingIsTakenWithoutTheFocus()
    {
        var f = Create();
        _focused = false;

        Assert.False(Tap(f, VirtualKeys.A));
        Assert.False(Tap(f, VirtualKeys.Home));
        Assert.Empty(_sent);
    }

    [Fact]
    public void AReleaseAfterTheFocusLeftIsStillSent()
    {
        var f = Create();
        Assert.True(f.OnKey(VirtualKeys.A, 0, true));
        _focused = false;

        Assert.True(f.OnKey(VirtualKeys.A, 0, false));
        Assert.False(_sent[^1].Down);
    }

    [Fact]
    public void AutoRepeatIsSwallowedButNotSentAgain()
    {
        var f = Create();
        f.OnKey(VirtualKeys.A, 0, true);

        Assert.True(f.OnKey(VirtualKeys.A, 0, true));
        Assert.Single(_sent);
    }

    [Fact]
    public void ShiftAndCtrlStayWithWindowsButApplyToTheKeys()
    {
        var f = Create();

        Assert.False(f.OnKey(VirtualKeys.LeftShift, 0, true));
        Assert.True(Tap(f, VirtualKeys.A));
        Assert.True(Tap(f, VirtualKeys.Left));
        Assert.False(f.OnKey(VirtualKeys.LeftShift, 0, false));
        Assert.False(f.OnKey(VirtualKeys.RightControl, 0, true));
        Assert.True(Tap(f, VirtualKeys.A));
        f.OnKey(VirtualKeys.RightControl, 0, false);

        Assert.Equal(
            [new HidKey(0x04, HidModifiers.LeftShift), new HidKey(HidKeyboard.LeftArrow, HidModifiers.LeftShift), new HidKey(0x04, HidModifiers.LeftGui)],
            _sent.Where(e => e.Down).Select(e => e.Key));
    }

    [Fact]
    public void ACtrlPressedBeforeTheFocusCameStillCounts()
    {
        var f = Create();
        _focused = false;
        f.OnKey(VirtualKeys.LeftControl, 0, true);
        _focused = true;

        Tap(f, VirtualKeys.A);
        Assert.Equal(new HidKey(0x04, HidModifiers.LeftGui), _sent[0].Key);
    }

    [Fact]
    public void CharactersFollowTheDeviceLayout()
    {
        var f = Create(HidKeyboardLayout.Jis);

        // US PC Shift+2 is '@': a JIS device types '@' with the unshifted 0x2F key
        f.OnKey(VirtualKeys.LeftShift, 0, true);
        Tap(f, 0x32);
        f.OnKey(VirtualKeys.LeftShift, 0, false);

        // US PC ';' is ';' on JIS too (0x33); US PC Shift+' is '"' = JIS Shift+2
        Tap(f, 0xBA);
        f.OnKey(VirtualKeys.LeftShift, 0, true);
        Tap(f, 0xDE);

        Assert.Equal([new HidKey(0x2F), new HidKey(0x33), new HidKey(0x1F, HidModifiers.LeftShift)], _sent.Where(e => e.Down).Select(e => e.Key));
    }

    [Fact]
    public void CtrlSpaceAndZenkakuHankakuAreTheInputSourceSwitch()
    {
        var f = Create();
        var expected = new HidKey(HidKeyboard.Space, HidModifiers.LeftControl);

        f.OnKey(VirtualKeys.LeftControl, 0, true);
        Assert.True(Tap(f, VirtualKeys.Space));
        f.OnKey(VirtualKeys.LeftControl, 0, false);
        Assert.Equal(expected, _sent[0].Key);
        Assert.Equal(KeyRole.Exact, _sent[0].Role);

        // Zenkaku/Hankaku is sent as a whole tap at once; its release (if any) goes to Windows and sends nothing
        _sent.Clear();
        Assert.True(f.OnKey(VirtualKeys.OemAuto, 0, true));
        Assert.Equal([true, false], _sent.Select(e => e.Down));
        Assert.Equal(expected, _sent[0].Key);
        Assert.False(f.OnKey(VirtualKeys.OemAuto, 0, false));
        Assert.Equal(2, _sent.Count);
    }

    [Fact]
    public void ZenkakuHankakuWithoutReleasesKeepsWorkingInAndOutOfTheMirror()
    {
        // The key may report presses only (F3 and F4 alternate): no press may be taken for auto-repeat.
        _focused = true;
        var f = Create();
        Assert.True(f.OnKey(VirtualKeys.OemAuto, 0, true));
        Assert.True(f.OnKey(VirtualKeys.OemEnlw, 0, true));
        Assert.True(f.OnKey(VirtualKeys.OemAuto, 0, true));
        Assert.Equal(6, _sent.Count); // three whole taps

        _focused = false;
        Assert.False(f.OnKey(VirtualKeys.OemEnlw, 0, true)); // Windows' own IME gets it
        Assert.False(f.OnKey(VirtualKeys.OemAuto, 0, true));
    }

    [Fact]
    public void AQuickShortcutTapIsKeptUntilTheBridgeLooks()
    {
        // Pressed and released while the bridge loop waited: the key state is up again, the press still counts once.
        var f = Create();
        Assert.True(Tap(f, VirtualKeys.Home));
        f.OnKey(VirtualKeys.LeftControl, 0, true);
        Assert.True(Tap(f, VirtualKeys.R));
        f.OnKey(VirtualKeys.LeftControl, 0, false);

        Assert.False(f.IsDown(VirtualKeys.Home));
        Assert.True(f.TakeShortcutPress(ShortcutKey.Home));
        Assert.False(f.TakeShortcutPress(ShortcutKey.Home)); // used once
        Assert.True(f.TakeShortcutPress(ShortcutKey.Rotate));
        Assert.False(f.TakeShortcutPress(ShortcutKey.Rotate));

        // A plain R (no Ctrl) is a letter, not the rotate shortcut.
        Assert.True(Tap(f, VirtualKeys.R));
        Assert.False(f.TakeShortcutPress(ShortcutKey.Rotate));
    }

    [Fact]
    public void EveryShortcutPressIsCountedAndTheKeyStateIsOnlyTheHeldState()
    {
        // Two taps between two looks of the bridge: two presses, and no held key left over (review, 2026-11-07).
        var f = Create();
        var desktop = new Win32Desktop(f.IsDown, k => f.TakeShortcutPresses(k));
        Assert.True(Tap(f, VirtualKeys.Home));
        Assert.True(Tap(f, VirtualKeys.Home));

        Assert.False(desktop.IsKeyDown(ShortcutKey.Home)); // a counted press is not "held"
        Assert.Equal(2, desktop.TakeShortcutPresses(ShortcutKey.Home));
        Assert.Equal(0, desktop.TakeShortcutPresses(ShortcutKey.Home));

        // Without a counter (no hook) the desktop says so.
        Assert.Null(new Win32Desktop(f.IsDown).TakeShortcutPresses(ShortcutKey.Home));
    }

    [Fact]
    public void ShortcutsAreSwallowedButNotSentAndShowInTheKeyState()
    {
        var f = Create();
        var desktop = new Win32Desktop(f.IsDown);

        Assert.True(f.OnKey(VirtualKeys.Home, 0, true));
        Assert.True(desktop.IsKeyDown(ShortcutKey.Home));
        Assert.True(f.OnKey(VirtualKeys.Home, 0, false));
        Assert.False(desktop.IsKeyDown(ShortcutKey.Home));

        f.OnKey(VirtualKeys.LeftControl, 0, true);
        Assert.True(f.OnKey(VirtualKeys.R, 0, true));
        Assert.True(desktop.IsKeyDown(ShortcutKey.Rotate));
        Assert.True(f.OnKey(VirtualKeys.R, 0, false));
        f.OnKey(VirtualKeys.LeftControl, 0, false);

        // R alone is a letter
        Assert.True(Tap(f, VirtualKeys.R));
        Assert.False(desktop.IsKeyDown(ShortcutKey.Rotate));
        Assert.Equal([new HidKey(0x15)], _sent.Where(e => e.Down).Select(e => e.Key));
    }

    [Fact]
    public void WindowsKeysStayWithWindows()
    {
        var f = Create();

        Assert.False(Tap(f, VirtualKeys.F1 + 4)); // F5
        Assert.False(Tap(f, VirtualKeys.LeftWin));
        Assert.False(Tap(f, 0xAF)); // volume up
        Assert.False(Tap(f, VirtualKeys.Snapshot));

        // anything with Alt or Win held (Alt+Tab, Win+D)
        f.OnKey(VirtualKeys.LeftMenu, 0, true);
        Assert.False(Tap(f, VirtualKeys.Tab));
        f.OnKey(VirtualKeys.LeftMenu, 0, false);
        f.OnKey(VirtualKeys.LeftWin, 0, true);
        Assert.False(Tap(f, 0x44));
        f.OnKey(VirtualKeys.LeftWin, 0, false);

        Assert.Empty(_sent);
    }

    [Fact]
    public void WithForwardingOffOnlyTheShortcutsAreTaken()
    {
        var f = Create();
        f.Forwarding = false;

        Assert.False(Tap(f, VirtualKeys.A));
        Assert.True(Tap(f, VirtualKeys.Home));
        Assert.Empty(_sent);
    }

    [Fact]
    public void KeysWithNothingToSendStayWithWindows()
    {
        var f = Create();

        Assert.False(Tap(f, 0xE5)); // VK_PROCESSKEY: no character in the fake layout
        Assert.Empty(_sent);
    }
}
