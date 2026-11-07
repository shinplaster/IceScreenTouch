using System.ComponentModel;
using System.Threading.Channels;
using IceScreenTouch.Core;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// The keyboard path of a session: the low-level hook, the <see cref="KeyboardForwarder"/> that picks the keys
/// for the device, and a <see cref="KeyboardRelay"/> loop that sends them. Without a keyboard on the sink (dry
/// run, no Bluetooth) or with the setting off, the hook still takes the shortcuts (Home, Ctrl+R) but lets every
/// other key through.
/// </summary>
public sealed class KeyboardInput : IAsyncDisposable
{
    private readonly Channel<KeyboardInputEvent> _events = Channel.CreateUnbounded<KeyboardInputEvent>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private KeyboardHook? _hook;
    private Task _relay = Task.CompletedTask;

    private KeyboardInput(Func<bool> mirrorFocused)
    {
        Forwarder = new KeyboardForwarder(mirrorFocused, KeyboardHook.ToChar, e => _events.Writer.TryWrite(e));
    }

    /// <summary>The forwarder (its key state includes the swallowed keys).</summary>
    public KeyboardForwarder Forwarder { get; }

    /// <summary>The hook is installed (otherwise the shortcuts fall back to Windows' key state).</summary>
    public bool HookInstalled => _hook is not null;

    /// <summary>Keys reach the device.</summary>
    public bool Forwarding => HookInstalled && Forwarder.Forwarding;

    /// <summary>
    /// The key state for <see cref="Win32Desktop"/>: the forwarder's while the hook is installed, Windows' own
    /// otherwise.
    /// </summary>
    public bool KeyDown(int vk) => HookInstalled ? Forwarder.IsDown(vk) : Win32Desktop.IsVirtualKeyDown(vk);

    /// <summary>The shortcut presses the hook took since the last call; null without the hook (nothing is counted).</summary>
    public int? ShortcutPresses(ShortcutKey key) => HookInstalled ? Forwarder.TakeShortcutPresses(key) : null;

    /// <summary>Installs the hook and starts the relay.</summary>
    /// <param name="sink">Where the keys go; null or one without a keyboard = shortcuts only.</param>
    /// <param name="mirrorFocused">True while the mirror window has the keyboard focus (and the bridge is not suspended).</param>
    /// <param name="forward">The user wants the keys sent (the setting).</param>
    /// <param name="layout">The device's hardware keyboard layout.</param>
    /// <param name="log">Problems and, with <paramref name="verbose"/>, every report (without the keys).</param>
    /// <param name="verbose">Log every report sent (how many keys are held, never which ones).</param>
    public static KeyboardInput Start(IKeyboardSink? sink, Func<bool> mirrorFocused, bool forward, HidKeyboardLayout layout, Action<string> log, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(log);
        var input = new KeyboardInput(mirrorFocused);
        input.Forwarder.Forwarding = forward && sink is { HasKeyboard: true };
        input.Forwarder.DeviceLayout = layout;

        var hook = new KeyboardHook(input.Forwarder);
        try
        {
            hook.Start();
            input._hook = hook;
        }
        catch (Win32Exception ex)
        {
            hook.Dispose();
            log($"[hook] cannot install the keyboard hook ({ex.Message}); keys will not reach the device, and Home / Ctrl+R use Windows' key state");
            return input;
        }

        if (input.Forwarder.Forwarding)
        {
            var relay = new KeyboardRelay();
            var token = input._stop.Token;
            input._relay = Task.Run(() => relay.RunAsync(input._events.Reader, sink!, verbose ? log : null, log, token), CancellationToken.None);
        }

        return input;
    }

    /// <summary>Removes the hook, releases every key on the device and stops the relay.</summary>
    public async ValueTask DisposeAsync()
    {
        _hook?.Dispose();
        _events.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _relay.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }
}
