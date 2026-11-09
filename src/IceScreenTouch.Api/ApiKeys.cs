// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>The key names of the API (docs/API-DESIGN.md, chapter 12) and their HID keys on a US keyboard.</summary>
public static class ApiKeys
{
    // The special keys by their API names (case-insensitive) -> the names HidKeyboard.TryParse knows.
    private static readonly FrozenDictionary<string, string> Special = BuildSpecial();

    /// <summary>
    /// Parses a key name and its modifiers. Letters, digits and the unshifted US symbols are single characters
    /// (<c>a</c>, <c>;</c>); a character that needs Shift on its own (<c>A</c>, <c>!</c>) is refused: send the base
    /// key with the <c>shift</c> modifier. The modifiers are <c>cmd</c>, <c>shift</c>, <c>option</c>, <c>ctrl</c>.
    /// </summary>
    public static bool TryParse(string? name, IReadOnlyList<string>? modifiers, out HidKey key, out string error)
    {
        key = default;
        error = string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            error = "key is required";
            return false;
        }

        byte bits = 0;
        foreach (var m in modifiers ?? [])
        {
            var bit = ModifierBit(m);
            if (bit == 0)
            {
                error = "modifiers can be cmd, shift, option and ctrl";
                return false;
            }

            bits |= bit;
        }

        HidKey found;
        if (Special.TryGetValue(name.ToLowerInvariant(), out var alias))
        {
            if (!HidKeyboard.TryParse(alias, HidKeyboardLayout.Us, out found))
            {
                error = "unknown key";
                return false;
            }
        }
        else if (name.Length == 1 && HidKeyboard.TryMap(name[0], HidKeyboardLayout.Us, out found) && found.Modifiers == 0)
        {
            // A basic character on its own key.
        }
        else
        {
            error = "unknown key (use a letter, digit, basic symbol or a name such as Enter, ArrowUp, F5)";
            return false;
        }

        key = found with { Modifiers = (byte)(found.Modifiers | bits) };
        return true;
    }

    private static byte ModifierBit(string? name) => name?.ToLowerInvariant() switch
    {
        "cmd" => HidModifiers.LeftGui,
        "shift" => HidModifiers.LeftShift,
        "option" => HidModifiers.LeftAlt,
        "ctrl" => HidModifiers.LeftControl,
        _ => 0,
    };

    private static FrozenDictionary<string, string> BuildSpecial()
    {
        var names = new Dictionary<string, string>
        {
            ["enter"] = "enter", ["escape"] = "escape", ["backspace"] = "backspace", ["tab"] = "tab",
            ["space"] = "space", ["delete"] = "delete", ["home"] = "home", ["end"] = "end",
            ["arrowup"] = "up", ["arrowdown"] = "down", ["arrowleft"] = "left", ["arrowright"] = "right",
            ["pageup"] = "pgup", ["pagedown"] = "pgdn",
        };
        for (var i = 1; i <= 12; i++)
        {
            names[$"f{i}"] = $"f{i}";
        }

        return names.ToFrozenDictionary();
    }
}
