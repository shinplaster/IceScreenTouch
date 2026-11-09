// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Core;

/// <summary>Pressing keys and typing text through an <see cref="IKeyboardSink"/> (the probe and the console tools).</summary>
public static class KeyboardTyping
{
    private static readonly TimeSpan ModifierGap = TimeSpan.FromMilliseconds(15);

    /// <summary>
    /// Presses and releases one key. The modifiers go down one report before the key and come up after it, like a
    /// person typing. Returns when the key-down report was sent (<paramref name="pressed"/> gets that moment).
    /// </summary>
    public static async ValueTask PressAsync(this IKeyboardSink sink, HidKey key, TimeSpan? hold = null, Action? pressed = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        try
        {
            if (key.Modifiers != 0)
            {
                await sink.SendKeyboardAsync(key.Modifiers, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                await Task.Delay(ModifierGap, cancellationToken).ConfigureAwait(false);
            }

            pressed?.Invoke();
            await sink.SendKeyboardAsync(key.Modifiers, new[] { key.Usage }, cancellationToken).ConfigureAwait(false);
            await Task.Delay(hold ?? TimeSpan.FromMilliseconds(30), cancellationToken).ConfigureAwait(false);
            if (key.Modifiers != 0)
            {
                await sink.SendKeyboardAsync(key.Modifiers, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
                await Task.Delay(ModifierGap, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Release everything even when cancelled: a key left down repeats on the device.
            await ReleaseSend.RunAsync(ct => sink.SendKeyboardAsync(0, ReadOnlyMemory<byte>.Empty, ct), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Types <paramref name="text"/> key by key for a device set to <paramref name="layout"/>. Returns the
    /// characters that have no key on that layout (they are skipped). <paramref name="pressed"/> gets each key as
    /// its key-down report goes out.
    /// </summary>
    public static async ValueTask<string> TypeAsync(this IKeyboardSink sink, string text, HidKeyboardLayout layout, TimeSpan? interval = null, Action<char>? pressed = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(text);
        var skipped = new System.Text.StringBuilder();
        foreach (var c in text.ReplaceLineEndings("\n"))
        {
            if (!HidKeyboard.TryMap(c, layout, out var key))
            {
                skipped.Append(c);
                continue;
            }

            await sink.PressAsync(key, pressed: pressed is null ? null : () => pressed(c), cancellationToken: cancellationToken).ConfigureAwait(false);
            await Task.Delay(interval ?? TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }

        return skipped.ToString();
    }
}
