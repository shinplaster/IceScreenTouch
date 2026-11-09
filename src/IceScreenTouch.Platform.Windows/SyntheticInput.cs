// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.ComponentModel;
using System.Runtime.InteropServices;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Synthetic input for end-to-end tests: button and key events go through SendInput (and therefore
/// through the low-level hook), cursor moves through SetCursorPos (which the hook does not see, like
/// the real bridge expects).
/// </summary>
public static class SyntheticInput
{
    /// <summary>Virtual key of Ctrl.</summary>
    public const int Control = VkControl;

    /// <summary>Virtual key of Shift.</summary>
    public const int Shift = VkShift;

    /// <summary>Virtual key of Home (the home shortcut).</summary>
    public const int Home = VkHome;

    /// <summary>Virtual key of R (Ctrl+R = the rotate shortcut).</summary>
    public const int R = VkR;

    /// <summary>Virtual key of A.</summary>
    public const int A = 0x41;

    /// <summary>Current cursor position.</summary>
    public static (int X, int Y) CursorPosition => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    /// <summary>Moves the cursor (not seen by low-level hooks).</summary>
    public static void MoveTo(int x, int y) => SetCursorPos(x, y);

    /// <summary>Presses or releases the left button at the cursor.</summary>
    public static void LeftButton(bool down) => Send(new Input
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Flags = down ? MouseEventLeftDown : MouseEventLeftUp } },
    });

    /// <summary>Presses or releases the right button at the cursor.</summary>
    public static void RightButton(bool down) => Send(new Input
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Flags = down ? MouseEventRightDown : MouseEventRightUp } },
    });

    /// <summary>Turns the wheel at the cursor; 120 is one notch, positive = up (or right when horizontal).</summary>
    public static void Wheel(int delta, bool horizontal = false) => Send(new Input
    {
        Type = InputMouse,
        Data = new InputUnion { Mouse = new MouseInput { Flags = horizontal ? MouseEventHWheel : MouseEventWheel, MouseData = unchecked((uint)delta) } },
    });

    /// <summary>Presses or releases a key.</summary>
    public static void Key(int virtualKey, bool down) => Send(new Input
    {
        Type = InputKeyboard,
        Data = new InputUnion { Keyboard = new KeybdInput { Vk = (ushort)virtualKey, Flags = down ? 0 : KeyEventKeyUp } },
    });

    private static void Send(Input input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SendInput failed");
        }
    }
}
