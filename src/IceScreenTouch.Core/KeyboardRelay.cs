using System.Threading.Channels;

namespace IceScreenTouch.Core;

/// <summary>How a forwarded key combines with the modifiers that are physically held.</summary>
public enum KeyRole
{
    /// <summary>A modifier (Shift, or Ctrl sent as Command): its bits in <see cref="HidKey.Modifiers"/>, no usage.</summary>
    Modifier,

    /// <summary>
    /// A key that types a character. The PC's Shift is already in the character (a JIS Shift+2 is '"', which is
    /// Shift+' on a US device), so the key's own Shift replaces the held one; the other modifiers stay.
    /// </summary>
    Character,

    /// <summary>Any other key (arrows, Enter, Backspace...): the held modifiers apply as they are.</summary>
    Other,

    /// <summary>Exactly <see cref="HidKey.Modifiers"/>, whatever is held (Ctrl+Space, the input source switch).</summary>
    Exact,
}

/// <summary>A key going down or up, already translated for the device.</summary>
/// <param name="Code">Identifies the physical key (the virtual key on Windows), to match the release with the press.</param>
/// <param name="Down">Pressed (true) or released.</param>
/// <param name="Key">What to send.</param>
/// <param name="Role">How it combines with the held modifiers.</param>
public readonly record struct KeyboardInputEvent(int Code, bool Down, HidKey Key, KeyRole Role);

/// <summary>Where keyboard reports go: the BLE HID peripheral (report ID 4), or a recorder in tests.</summary>
public interface IKeyboardSink
{
    /// <summary>The published HID map has the keyboard.</summary>
    bool HasKeyboard { get; }

    /// <summary>Sends one keyboard report: the modifiers and the keys held down (none = everything released).</summary>
    ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken);
}

/// <summary>One keyboard report.</summary>
/// <param name="Modifiers">Modifier bits.</param>
/// <param name="Keys">Usages held down, oldest first (at most <see cref="HidReports.KeyboardKeySlots"/>).</param>
public sealed record KeyboardReport(byte Modifiers, IReadOnlyList<byte> Keys)
{
    /// <summary>Everything released.</summary>
    public static KeyboardReport Released { get; } = new(0, []);

    /// <inheritdoc/>
    public bool Equals(KeyboardReport? other) =>
        other is not null && Modifiers == other.Modifiers && Keys.SequenceEqual(other.Keys);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Modifiers, Keys.Count);

    /// <inheritdoc/>
    public override string ToString() => $"mods=0x{Modifiers:X2} keys=[{string.Join(' ', Keys.Select(k => $"0x{k:X2}"))}]";

    /// <summary>
    /// The report for the log: how many keys are held, not which ones. The usages would let anyone who reads the
    /// log file read back what was typed on the device, passwords included.
    /// </summary>
    public string Redacted() => Keys.Count == 0 ? "released" : $"{Keys.Count} key(s) held";
}

/// <summary>
/// Turns key presses and releases into keyboard reports, the way a real keyboard would send them: a report lists
/// every key held, so the device repeats a held key by itself. Modifiers are only sent together with a key (a
/// Ctrl held alone is not a Command held alone on the device). Not thread-safe; <see cref="RunAsync"/> owns it.
/// </summary>
public sealed class KeyboardRelay
{
    private const byte ShiftBits = HidModifiers.LeftShift | HidModifiers.RightShift;

    private readonly List<KeyboardInputEvent> _held = [];
    private readonly Dictionary<int, byte> _modifiers = [];
    private KeyboardReport _last = KeyboardReport.Released;

    /// <summary>The last report produced.</summary>
    public KeyboardReport Last => _last;

    /// <summary>Keys (not modifiers) held now.</summary>
    public int HeldKeys => _held.Count;

    /// <summary>Applies one event. Returns the report to send, or null when nothing changed (a key repeat).</summary>
    public KeyboardReport? Apply(KeyboardInputEvent e)
    {
        if (e.Role == KeyRole.Modifier)
        {
            if (e.Down)
            {
                _modifiers[e.Code] = e.Key.Modifiers;
            }
            else
            {
                _modifiers.Remove(e.Code);
            }
        }
        else if (e.Down)
        {
            if (_held.Exists(h => h.Code == e.Code))
            {
                return null; // auto-repeat of a key already down
            }

            _held.Add(e);
        }
        else if (_held.RemoveAll(h => h.Code == e.Code) == 0)
        {
            return null;
        }

        return Produce(Compute());
    }

    /// <summary>Forgets every key and modifier. Returns the release report, or null if nothing was held.</summary>
    public KeyboardReport? ReleaseAll()
    {
        _held.Clear();
        _modifiers.Clear();
        return Produce(KeyboardReport.Released);
    }

    /// <summary>
    /// Reads events until cancelled and sends a report for each change. Everything is released at the end. Sends
    /// nothing while the sink has no keyboard. A report that cannot be sent goes to <paramref name="problems"/>
    /// and the relay goes on (the next report carries the whole state again).
    /// </summary>
    /// <param name="input">The key events.</param>
    /// <param name="sink">Where the reports go.</param>
    /// <param name="log">Every report sent, without the keys (the verbose log).</param>
    /// <param name="problems">Reports that could not be sent.</param>
    /// <param name="cancellationToken">Stops the relay.</param>
    public async Task RunAsync(ChannelReader<KeyboardInputEvent> input, IKeyboardSink sink, Action<string>? log = null, Action<string>? problems = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(sink);
        try
        {
            await foreach (var e in input.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Apply(e) is { } report && sink.HasKeyboard)
                {
                    log?.Invoke($"[keys] {report.Redacted()}");
                    try
                    {
                        await sink.SendKeyboardAsync(report.Modifiers, report.Keys.ToArray(), cancellationToken).ConfigureAwait(false);
                    }
#pragma warning disable CA1031 // The hook keeps taking the keys while the relay runs: one failed send must not end it.
                    catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
                    {
                        problems?.Invoke($"[keys] cannot send a key report: {ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReleaseAll() is { } released && sink.HasKeyboard)
            {
                using var timeout = new CancellationTokenSource(ReleaseSend.Grace);
                try
                {
                    await sink.SendKeyboardAsync(released.Modifiers, ReadOnlyMemory<byte>.Empty, timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
#pragma warning disable CA1031 // Stopping must go on (the caller still has the hook, the receiver and the HID service to release).
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    problems?.Invoke($"[keys] cannot release the keys: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private KeyboardReport Compute()
    {
        if (_held.Count == 0)
        {
            return KeyboardReport.Released;
        }

        byte physical = 0;
        foreach (var m in _modifiers.Values)
        {
            physical |= m;
        }

        var last = _held[^1];
        var modifiers = last.Role switch
        {
            KeyRole.Character => (byte)((physical & ~ShiftBits) | last.Key.Modifiers),
            KeyRole.Exact => last.Key.Modifiers,
            _ => (byte)(physical | last.Key.Modifiers),
        };

        var keys = _held.Select(h => h.Key.Usage).Distinct().TakeLast(HidReports.KeyboardKeySlots).ToArray();
        return new KeyboardReport(modifiers, keys);
    }

    private KeyboardReport? Produce(KeyboardReport report)
    {
        if (report.Equals(_last))
        {
            return null;
        }

        _last = report;
        return report;
    }
}
