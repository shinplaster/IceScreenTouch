using System.Globalization;
using IceScreenTouch.Core;

namespace IceScreenTouch.Probe;

/// <summary>The outcome of a keyboard probe run.</summary>
/// <param name="Checks">The pass/fail items (names in English: see <see cref="KeyboardProbe"/>).</param>
/// <param name="LatencyMs">Median time from a key-down report to the page's keydown arriving at the PC, if measured.</param>
/// <param name="LikelyLayout">
/// When the symbols came out wrong but match what the other layout would type: that layout (the device's hardware
/// keyboard setting differs from the app's).
/// </param>
/// <param name="JapaneseInput">The text came out as Japanese (kana or full-width): the device's input source is Japanese.</param>
/// <param name="NavigationKeysTaken">
/// Letters arrived but Space or the arrow keys did not: the device used them itself (Full Keyboard Access on an iPad
/// took Space and the arrows, 2026-11-06).
/// </param>
public sealed record KeyboardProbeResult(IReadOnlyList<Check> Checks, double? LatencyMs, HidKeyboardLayout? LikelyLayout, bool JapaneseInput, bool NavigationKeysTaken = false)
{
    /// <summary>No key reached the page.</summary>
    public bool NothingArrived => Checks.Count > 0 && Checks[0].Name == KeyboardProbe.ArriveCheck && !Checks[0].Ok;

    /// <summary>Every check passed.</summary>
    public bool Passed => Checks.Count > 0 && Checks.All(c => c.Ok);
}

/// <summary>
/// Types into the text box of the keyboard page (/keys) over the HID keyboard and compares what arrived: letters
/// and digits (with the latency), every ASCII symbol (and which hardware layout the device seems set to when they
/// come out wrong), Enter / arrows / Backspace, and Command+A / C / V. The box is emptied at the end. Japanese
/// conversion is not tested: it depends on the keyboards set up on the device.
/// </summary>
/// <summary>A probe stopped because its page left the front (the probe ends as cancelled).</summary>
public sealed class ProbePageLeftException : OperationCanceledException
{
    /// <summary>Creates the exception.</summary>
    public ProbePageLeftException()
        : base("the probe page is no longer in front")
    {
    }

    /// <summary>Creates the exception.</summary>
    public ProbePageLeftException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public ProbePageLeftException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class KeyboardProbe(IKeyboardSink sink, ProbeServer server, TextWriter output)
{
    /// <summary>Check names (the app shows its own text for them).</summary>
    public const string ArriveCheck = "keys arrive";

    /// <inheritdoc cref="ArriveCheck"/>
    public const string LettersCheck = "letters";

    /// <inheritdoc cref="ArriveCheck"/>
    public const string SymbolsCheck = "symbols";

    /// <inheritdoc cref="ArriveCheck"/>
    public const string EditingCheck = "editing keys";

    /// <inheritdoc cref="ArriveCheck"/>
    public const string SelectAllCheck = "select all";

    /// <inheritdoc cref="ArriveCheck"/>
    public const string PasteCheck = "copy and paste";

    /// <summary>Typed first: letters, digits and single spaces (two spaces would become ". " on iOS).</summary>
    public const string Letters = "abc XYZ 123";

    /// <summary>Every ASCII symbol once (no "--" or "...", which iOS may turn into dashes and ellipses).</summary>
    public const string Symbols = "!@#$%^&*()-_=+[]{};:'\",.<>/?\\|`~";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly HidKey Command = new(0, HidModifiers.LeftGui);

    /// <summary>How long the page must stay quiet before a step counts as done.</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The longest wait for a step.</summary>
    public TimeSpan StepTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Runs the probe and prints the result. The device must be set to <paramref name="layout"/>.</summary>
    public async Task<KeyboardProbeResult> RunAsync(HidKeyboardLayout layout, CancellationToken ct = default)
    {
        server.Clear();
        var checks = new List<Check>();

        // 1. Letters and digits; the latency of each key.
        var before = CurrentText();
        var sent = new List<double>();
        var mark = server.Snapshot().Count;

        // Like the later steps: the readiness check, the suspend and the countdown all come before this, and the phone
        // may have switched apps meanwhile; the letters would then be typed into that app (review, 2026-11-07).
        EnsureInFront();
        await sink.TypeAsync(Letters, layout, pressed: _ => { sent.Add(ProbeClock.Now()); EnsureInFront(); }, cancellationToken: ct).ConfigureAwait(false);
        await SettleAsync(ct).ConfigureAwait(false);
        var events = server.Snapshot().Skip(mark).ToList();

        // A modifier pressed alone (the Shift report before a capital) is a keydown of its own on the page.
        var keydowns = events.Where(e => e.Type == "keydown" && e.Key is not ("Shift" or "Meta" or "Control" or "Alt")).ToList();
        output.WriteLine($"keyboard page received {events.Count} events ({keydowns.Count} keydown) for {Letters.Length} keys");
        if (keydowns.Count == 0)
        {
            output.WriteLine("  -> nothing arrived. Is the page open in Safari, in front, with the cursor in the box?");
            checks.Add(new Check(ArriveCheck, false, "no keydown reached the page"));
            return new KeyboardProbeResult(checks, null, null, false);
        }

        checks.Add(new Check(ArriveCheck, true, $"{keydowns.Count} of {Letters.Length} keydowns"));
        var latencies = keydowns.Zip(sent, (e, t) => (e.T - t) * 1000).Where(ms => ms >= 0).Order().ToList();
        double? latency = latencies.Count > 0 ? latencies[latencies.Count / 2] : null;
        var letters = Typed(before, CurrentText());
        var japanese = IsJapanese(letters) || events.Any(e => e.Composing == true);
        checks.Add(Compare(LettersCheck, Letters, letters));
        if (japanese)
        {
            output.WriteLine("  -> the text is Japanese: switch the device's input source to English (Ctrl+Space) and run again");
        }

        // 2. Symbols: their keys differ between US and JIS.
        before = CurrentText();
        await sink.TypeAsync(Symbols, layout, pressed: _ => EnsureInFront(), cancellationToken: ct).ConfigureAwait(false);
        await SettleAsync(ct).ConfigureAwait(false);
        var symbols = Typed(before, CurrentText());
        var symbolCheck = Compare(SymbolsCheck, Symbols, symbols);
        HidKeyboardLayout? likely = null;
        if (!symbolCheck.Ok && !japanese)
        {
            var other = layout == HidKeyboardLayout.Us ? HidKeyboardLayout.Jis : HidKeyboardLayout.Us;
            var predicted = Predict(Symbols, layout, other);
            if (Distance(Normalize(symbols), predicted) < Distance(Normalize(symbols), Symbols))
            {
                likely = other;
                output.WriteLine($"  -> the symbols look like a device set to {other} (the app assumes {layout})");
            }
        }

        checks.Add(symbolCheck);

        // 3. Enter, typing, two steps left, Backspace: "xyz" loses its x.
        before = CurrentText();
        await PressAsync(new HidKey(HidKeyboard.Enter), ct).ConfigureAwait(false);
        await sink.TypeAsync("xyz", layout, pressed: _ => EnsureInFront(), cancellationToken: ct).ConfigureAwait(false);
        await PressAsync(new HidKey(HidKeyboard.LeftArrow), ct).ConfigureAwait(false);
        await PressAsync(new HidKey(HidKeyboard.LeftArrow), ct).ConfigureAwait(false);
        await PressAsync(new HidKey(HidKeyboard.Backspace), ct).ConfigureAwait(false);
        await SettleAsync(ct).ConfigureAwait(false);
        var edited = Typed(before, CurrentText());
        checks.Add(Compare(EditingCheck, "\nyz", edited, "Enter, x y z, Left, Left, Backspace"));

        // Spaces missing from the letters, or arrows that did not move the cursor ("xyz" lost its z): the device
        // keeps those keys for itself.
        var taken = !japanese && ((letters != Letters && letters == Letters.Replace(" ", string.Empty, StringComparison.Ordinal)) || Normalize(edited) == "\nxy");
        if (taken)
        {
            output.WriteLine("  -> Space or the arrow keys did not reach the page: turn off Full Keyboard Access on the device (Settings > Accessibility > Keyboards) and run again");
        }

        // 4. Command+A selects everything; Command+C, Right (to the end), Command+V doubles the text.
        before = CurrentText();
        await PressAsync(Command with { Usage = 0x04 }, ct).ConfigureAwait(false);
        await SettleAsync(ct).ConfigureAwait(false);
        var selection = Selection();
        checks.Add(new Check(
            SelectAllCheck,
            selection is { } s && s.Start == 0 && s.End == before.Length && before.Length > 0,
            selection is { } sel ? string.Create(Inv, $"selection {sel.Start}..{sel.End} of {before.Length} characters") : "no selection reported"));
        if (checks[^1].Ok)
        {
            await PressAsync(Command with { Usage = 0x06 }, ct).ConfigureAwait(false);
            await PressAsync(new HidKey(HidKeyboard.RightArrow), ct).ConfigureAwait(false);
            await PressAsync(Command with { Usage = 0x19 }, ct).ConfigureAwait(false);
            await SettleAsync(ct).ConfigureAwait(false);
            var after = CurrentText();

            // iOS's smart insert may put a space between the pasted text and the word before it.
            var pasted = before.Length > 0 && (after == before + before || after == before + " " + before);
            checks.Add(new Check(
                PasteCheck,
                pasted,
                pasted
                    ? string.Create(Inv, $"{before.Length} characters -> {after.Length}{(after.Length > before.Length * 2 ? " (iOS added a space)" : string.Empty)}")
                    // Only lengths: if ⌘C did nothing, ⌘V pasted whatever the device's clipboard held (a password,
                    // say), and that must not go to the log.
                    : string.Create(Inv, $"{before.Length} characters -> {after.Length} (expected {before.Length * 2}); {Typed(before, after).Length} other characters added")));
        }
        else
        {
            // Without the selection ⌘C copies nothing and ⌘V would paste the device's own clipboard into the page.
            checks.Add(new Check(PasteCheck, false, "not tried: Command+A did not select the text"));
        }

        // Leave the box empty.
        await PressAsync(Command with { Usage = 0x04 }, ct).ConfigureAwait(false);
        await PressAsync(new HidKey(HidKeyboard.Backspace), ct).ConfigureAwait(false);
        await SettleAsync(ct).ConfigureAwait(false);

        output.WriteLine();
        output.WriteLine("keyboard checks:");
        foreach (var c in checks)
        {
            output.WriteLine($"  [{(c.Ok ? "PASS" : "FAIL")}] {c.Name,-15} {c.Detail}");
        }

        if (latency is { } l)
        {
            output.WriteLine(string.Create(Inv, $"latency (key-down report -> keydown at the PC): median {l:0} ms over {latencies.Count} keys"));
        }

        var result = new KeyboardProbeResult(checks, latency, likely, japanese, taken);
        output.WriteLine(result.Passed ? "KEYBOARD PROBE PASSED" : "KEYBOARD PROBE FAILED");
        return result;
    }

    /// <summary>
    /// What a device set to <paramref name="actual"/> types for keys chosen for <paramref name="assumed"/>
    /// (characters with no key on either side are left out).
    /// </summary>
    public static string Predict(string text, HidKeyboardLayout assumed, HidKeyboardLayout actual)
    {
        ArgumentNullException.ThrowIfNull(text);
        var chars = new List<char>();
        foreach (var c in text)
        {
            if (HidKeyboard.TryMap(c, assumed, out var key) && HidKeyboard.TryChar(key, actual, out var typed))
            {
                chars.Add(typed);
            }
        }

        return new string([.. chars]);
    }

    /// <summary>What was added to <paramref name="before"/> (the whole text if it was replaced).</summary>
    public static string Typed(string before, string after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return after.StartsWith(before, StringComparison.Ordinal) ? after[before.Length..] : after;
    }

    /// <summary>Undoes iOS's smart punctuation (curly quotes) so plain ASCII can be compared.</summary>
    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace('‘', '\'').Replace('’', '\'').Replace('“', '"').Replace('”', '"').Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>Kana, kanji or full-width forms: the device converted the keys as Japanese.</summary>
    public static bool IsJapanese(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Any(c => c is (>= '　' and <= 'ヿ') or (>= '一' and <= '鿿') or (>= '＀' and <= '￯'));
    }

    /// <summary>Levenshtein distance.</summary>
    public static int Distance(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var diagonal = row[0];
            row[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var up = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diagonal + (a[i - 1] == b[j - 1] ? 0 : 1));
                diagonal = up;
            }
        }

        return row[b.Length];
    }

    private static Check Compare(string name, string expected, string got, string? what = null)
    {
        var ok = Normalize(got) == expected;
        var shown = what ?? Show(expected);
        return new Check(name, ok, ok ? shown : $"expected {Show(expected)}, got {Show(got)}");
    }

    private static string Show(string text) => "\"" + text.Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private async ValueTask PressAsync(HidKey key, CancellationToken ct)
    {
        // Only while the page is in front with the cursor in its box: if the phone switched to another app (a
        // notification tapped, a call), the rest (Command+A, Command+V, Enter, Backspace) would edit or send in that
        // app instead (security review, 2026-11-07).
        EnsureInFront();
        await sink.PressAsync(key, cancellationToken: ct).ConfigureAwait(false);
        await Task.Delay(80, ct).ConfigureAwait(false);
    }

    // The page's last hello must say it is in front with the cursor in its box, and be recent: a "hidden" hello
    // that never arrived (Wi-Fi gone, Safari suspended) must not leave an old "visible" one standing.
    private void EnsureInFront()
    {
        if (!ProbeReadiness.InFront(ProbePage.Keys, server, ProbeClock.Now()))
        {
            output.WriteLine("  -> the keyboard page is no longer in front with the cursor in its box: stopped");
            throw new ProbePageLeftException();
        }
    }

    /// <summary>The text in the box as last reported (by any event or hello).</summary>
    private string CurrentText()
    {
        var last = server.Snapshot().LastOrDefault(e => e.Value is not null);
        var hello = server.HelloFor(ProbePage.Keys);
        var latest = hello?.Value is not null && (last is null || hello.T > last.T) ? hello : last;
        return latest?.Value ?? string.Empty;
    }

    private (int Start, int End)? Selection()
    {
        var last = server.Snapshot().LastOrDefault(e => e.SelectionStart is not null && e.SelectionEnd is not null);
        return last is null ? null : (last.SelectionStart!.Value, last.SelectionEnd!.Value);
    }

    /// <summary>Waits until the page has been quiet for <see cref="Quiet"/> (or <see cref="StepTimeout"/> passed).</summary>
    private async Task SettleAsync(CancellationToken ct)
    {
        var deadline = ProbeClock.Now() + StepTimeout.TotalSeconds;
        var count = -1;
        var since = ProbeClock.Now();
        while (ProbeClock.Now() < deadline)
        {
            var n = server.Snapshot().Count;
            if (n != count)
            {
                count = n;
                since = ProbeClock.Now();
            }
            else if (ProbeClock.Now() - since >= Quiet.TotalSeconds)
            {
                return;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }
}
