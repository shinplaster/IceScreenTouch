// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using System.Globalization;

namespace IceScreenTouch.Core;

/// <summary>One key press on the HID keyboard: a Keyboard/Keypad page usage plus the modifiers held with it.</summary>
/// <param name="Usage">Keyboard/Keypad page (0x07) usage, for example 0x04 = A.</param>
/// <param name="Modifiers">Modifier bits (<see cref="HidModifiers"/>).</param>
public readonly record struct HidKey(byte Usage, byte Modifiers = 0);

/// <summary>Modifier bits of the keyboard report (byte 0).</summary>
public static class HidModifiers
{
    public const byte LeftControl = 0x01;
    public const byte LeftShift = 0x02;
    public const byte LeftAlt = 0x04;
    public const byte LeftGui = 0x08;
    public const byte RightControl = 0x10;
    public const byte RightShift = 0x20;
    public const byte RightAlt = 0x40;
    public const byte RightGui = 0x80;
}

/// <summary>
/// The hardware keyboard layout the device is set to (iOS: Settings &gt; General &gt; Keyboard &gt; Hardware
/// Keyboard). HID sends key positions, so the same usage types different symbols on US and JIS.
/// </summary>
public enum HidKeyboardLayout
{
    /// <summary>US (ANSI) layout.</summary>
    Us,

    /// <summary>Japanese (JIS) layout.</summary>
    Jis,
}

/// <summary>Keyboard/Keypad page usages and the mapping from text to key presses.</summary>
public static class HidKeyboard
{
    public const byte Enter = 0x28;
    public const byte Escape = 0x29;
    public const byte Backspace = 0x2A;
    public const byte Tab = 0x2B;
    public const byte Space = 0x2C;
    public const byte CapsLock = 0x39;
    public const byte Delete = 0x4C;
    public const byte RightArrow = 0x4F;
    public const byte LeftArrow = 0x50;
    public const byte DownArrow = 0x51;
    public const byte UpArrow = 0x52;

    /// <summary>JIS "ro" key (International1): backslash, shifted underscore.</summary>
    public const byte International1 = 0x87;

    /// <summary>JIS yen key (International3): yen, shifted vertical bar.</summary>
    public const byte International3 = 0x89;

    /// <summary>LANG1: kana on a Japanese Mac keyboard (Hangul/English toggle on Korean).</summary>
    public const byte Lang1 = 0x90;

    /// <summary>LANG2: eisu (alphanumeric) on a Japanese Mac keyboard.</summary>
    public const byte Lang2 = 0x91;

    private const byte S = HidModifiers.LeftShift;

    // Symbols that differ between the layouts. Letters, digits and whitespace are shared.
    private static readonly FrozenDictionary<char, HidKey> UsSymbols = new Dictionary<char, HidKey>
    {
        ['!'] = new(0x1E, S), ['@'] = new(0x1F, S), ['#'] = new(0x20, S), ['$'] = new(0x21, S), ['%'] = new(0x22, S),
        ['^'] = new(0x23, S), ['&'] = new(0x24, S), ['*'] = new(0x25, S), ['('] = new(0x26, S), [')'] = new(0x27, S),
        ['-'] = new(0x2D), ['_'] = new(0x2D, S), ['='] = new(0x2E), ['+'] = new(0x2E, S),
        ['['] = new(0x2F), ['{'] = new(0x2F, S), [']'] = new(0x30), ['}'] = new(0x30, S),
        ['\\'] = new(0x31), ['|'] = new(0x31, S), [';'] = new(0x33), [':'] = new(0x33, S),
        ['\''] = new(0x34), ['"'] = new(0x34, S), ['`'] = new(0x35), ['~'] = new(0x35, S),
        [','] = new(0x36), ['<'] = new(0x36, S), ['.'] = new(0x37), ['>'] = new(0x37, S),
        ['/'] = new(0x38), ['?'] = new(0x38, S),
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<char, HidKey> JisSymbols = new Dictionary<char, HidKey>
    {
        ['!'] = new(0x1E, S), ['"'] = new(0x1F, S), ['#'] = new(0x20, S), ['$'] = new(0x21, S), ['%'] = new(0x22, S),
        ['&'] = new(0x23, S), ['\''] = new(0x24, S), ['('] = new(0x25, S), [')'] = new(0x26, S),
        ['-'] = new(0x2D), ['='] = new(0x2D, S), ['^'] = new(0x2E), ['~'] = new(0x2E, S),
        ['@'] = new(0x2F), ['`'] = new(0x2F, S), ['['] = new(0x30), ['{'] = new(0x30, S),
        [']'] = new(0x32), ['}'] = new(0x32, S), [';'] = new(0x33), ['+'] = new(0x33, S),
        [':'] = new(0x34), ['*'] = new(0x34, S),
        [','] = new(0x36), ['<'] = new(0x36, S), ['.'] = new(0x37), ['>'] = new(0x37, S),
        ['/'] = new(0x38), ['?'] = new(0x38, S),
        ['\\'] = new(International1), ['_'] = new(International1, S),
        ['¥'] = new(International3), ['|'] = new(International3, S),
    }.ToFrozenDictionary();

    private static readonly FrozenDictionary<string, byte> Names = BuildNames();

    private static readonly FrozenDictionary<HidKey, char> UsChars = BuildReverse(HidKeyboardLayout.Us);

    private static readonly FrozenDictionary<HidKey, char> JisChars = BuildReverse(HidKeyboardLayout.Jis);

    /// <summary>
    /// The character a device set to <paramref name="layout"/> types for <paramref name="key"/> (the reverse of
    /// <see cref="TryMap"/>; only Shift counts among the modifiers). For example, the key that types '@' on US
    /// types '"' on JIS.
    /// </summary>
    public static bool TryChar(HidKey key, HidKeyboardLayout layout, out char c) =>
        (layout == HidKeyboardLayout.Jis ? JisChars : UsChars).TryGetValue(
            key with { Modifiers = (byte)(key.Modifiers & (HidModifiers.LeftShift | HidModifiers.RightShift)) != 0 ? S : (byte)0 },
            out c);

    /// <summary>
    /// The key press that types <paramref name="c"/> on a device set to <paramref name="layout"/>. Only
    /// printable ASCII (and the yen sign on JIS), space, tab and newline have one.
    /// </summary>
    public static bool TryMap(char c, HidKeyboardLayout layout, out HidKey key)
    {
        switch (c)
        {
            case >= 'a' and <= 'z':
                key = new((byte)(0x04 + (c - 'a')));
                return true;
            case >= 'A' and <= 'Z':
                key = new((byte)(0x04 + (c - 'A')), S);
                return true;
            case >= '1' and <= '9':
                key = new((byte)(0x1E + (c - '1')));
                return true;
            case '0':
                key = new(0x27);
                return true;
            case ' ':
                key = new(Space);
                return true;
            case '\t':
                key = new(Tab);
                return true;
            case '\n':
                key = new(Enter);
                return true;
        }

        return (layout == HidKeyboardLayout.Jis ? JisSymbols : UsSymbols).TryGetValue(c, out key);
    }

    /// <summary>
    /// Parses a key name such as <c>enter</c>, <c>left</c>, <c>f5</c>, <c>kana</c>, a single character on
    /// <paramref name="layout"/>, or a hex usage (<c>0x90</c>). Modifiers come as prefixes joined by
    /// <c>+</c>: <c>ctrl+space</c>, <c>cmd+a</c>, <c>shift+tab</c>.
    /// </summary>
    public static bool TryParse(string text, HidKeyboardLayout layout, out HidKey key)
    {
        key = default;

        // "cmd++" is Command with the '+' key itself.
        var plusKey = text.Length >= 2 && text.EndsWith("++", StringComparison.Ordinal);
        string[] parts = text == "+" ? ["+"] : plusKey ? [.. text[..^2].Split('+'), "+"] : text.Split('+');
        byte modifiers = 0;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            var m = ModifierBit(parts[i]);
            if (m == 0)
            {
                return false;
            }

            modifiers |= m;
        }

        var last = parts[^1];
        if (last.Length == 0)
        {
            return false;
        }

        if (Names.TryGetValue(last.ToLowerInvariant(), out var usage))
        {
            key = new(usage, modifiers);
            return true;
        }

        if (last.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && byte.TryParse(last.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out usage))
        {
            key = new(usage, modifiers);
            return true;
        }

        if (last.Length == 1 && TryMap(last[0], layout, out var single))
        {
            key = single with { Modifiers = (byte)(single.Modifiers | modifiers) };
            return true;
        }

        return false;
    }

    private static byte ModifierBit(string name) => name.ToLowerInvariant() switch
    {
        "ctrl" or "control" => HidModifiers.LeftControl,
        "shift" => HidModifiers.LeftShift,
        "alt" or "opt" or "option" => HidModifiers.LeftAlt,
        "cmd" or "command" or "gui" or "win" => HidModifiers.LeftGui,
        _ => 0,
    };

    private static FrozenDictionary<HidKey, char> BuildReverse(HidKeyboardLayout layout)
    {
        var map = new Dictionary<HidKey, char>();
        foreach (var c in Enumerable.Range(' ', '~' - ' ' + 1).Select(i => (char)i).Append('¥'))
        {
            if (TryMap(c, layout, out var key))
            {
                map.TryAdd(key, c);
            }
        }

        return map.ToFrozenDictionary();
    }

    private static FrozenDictionary<string, byte> BuildNames()
    {
        var names = new Dictionary<string, byte>
        {
            ["enter"] = Enter, ["return"] = Enter, ["esc"] = Escape, ["escape"] = Escape,
            ["bs"] = Backspace, ["backspace"] = Backspace, ["tab"] = Tab, ["space"] = Space,
            ["capslock"] = CapsLock, ["del"] = Delete, ["delete"] = Delete,
            ["home"] = 0x4A, ["pgup"] = 0x4B, ["end"] = 0x4D, ["pgdn"] = 0x4E,
            ["right"] = RightArrow, ["left"] = LeftArrow, ["down"] = DownArrow, ["up"] = UpArrow,
            ["kana"] = Lang1, ["lang1"] = Lang1, ["eisu"] = Lang2, ["lang2"] = Lang2,
            ["hankaku"] = 0x35, ["ro"] = International1, ["yen"] = International3,
            ["henkan"] = 0x8A, ["muhenkan"] = 0x8B, ["katakanahiragana"] = 0x88,
        };
        for (var i = 1; i <= 12; i++)
        {
            names[$"f{i}"] = (byte)(0x3A + i - 1);
        }

        return names.ToFrozenDictionary();
    }
}
