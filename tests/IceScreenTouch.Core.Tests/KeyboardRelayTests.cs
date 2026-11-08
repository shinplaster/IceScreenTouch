using System.Threading.Channels;
using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

public class KeyboardRelayTests
{
    private static KeyboardInputEvent Down(int code, byte usage, byte modifiers = 0, KeyRole role = KeyRole.Character) =>
        new(code, true, new HidKey(usage, modifiers), role);

    private static KeyboardInputEvent Up(int code, KeyRole role = KeyRole.Character) =>
        new(code, false, default, role);

    [Fact]
    public void APressAndAReleaseMakeTwoReports()
    {
        var relay = new KeyboardRelay();

        Assert.Equal(new KeyboardReport(0, [0x04]), relay.Apply(Down(65, 0x04)));
        Assert.Equal(KeyboardReport.Released, relay.Apply(Up(65)));
    }

    [Fact]
    public void AutoRepeatAndStrayReleasesSendNothing()
    {
        var relay = new KeyboardRelay();
        relay.Apply(Down(65, 0x04));

        Assert.Null(relay.Apply(Down(65, 0x04)));
        Assert.Null(relay.Apply(Up(66)));
        Assert.Equal(1, relay.HeldKeys);
    }

    [Fact]
    public void HeldKeysAreListedInPressOrderAndTheLastKeyDecidesTheModifiers()
    {
        var relay = new KeyboardRelay();
        relay.Apply(Down(65, 0x04));

        Assert.Equal(new KeyboardReport(HidModifiers.LeftShift, [0x04, 0x05]), relay.Apply(Down(66, 0x05, HidModifiers.LeftShift)));
        Assert.Equal(new KeyboardReport(0, [0x04]), relay.Apply(Up(66)));
    }

    [Fact]
    public void AtMostSixKeysAreReported()
    {
        var relay = new KeyboardRelay();
        KeyboardReport? last = null;
        for (var i = 0; i < 8; i++)
        {
            last = relay.Apply(Down(100 + i, (byte)(0x04 + i)));
        }

        Assert.Equal(new byte[] { 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B }, last!.Keys);
    }

    [Fact]
    public void HeldModifiersApplyByRole()
    {
        var relay = new KeyboardRelay();
        relay.Apply(new KeyboardInputEvent(16, true, new HidKey(0, HidModifiers.LeftShift | HidModifiers.LeftGui), KeyRole.Modifier));

        // a modifier alone is not sent
        Assert.Equal(KeyboardReport.Released, relay.Last);

        // a character brings its own Shift; Command stays
        Assert.Equal(new KeyboardReport(HidModifiers.LeftGui, [0x2E]), relay.Apply(Down(187, 0x2E)));
        relay.Apply(Up(187));

        // another key takes them all
        Assert.Equal(new KeyboardReport(HidModifiers.LeftShift | HidModifiers.LeftGui, [0x50]), relay.Apply(Down(37, 0x50, role: KeyRole.Other)));
        relay.Apply(Up(37, KeyRole.Other));

        // exact keys ignore them
        Assert.Equal(new KeyboardReport(HidModifiers.LeftControl, [0x2C]), relay.Apply(Down(32, 0x2C, HidModifiers.LeftControl, KeyRole.Exact)));
    }

    [Fact]
    public void ReleaseAllForgetsEverything()
    {
        var relay = new KeyboardRelay();
        relay.Apply(Down(65, 0x04));

        Assert.Equal(KeyboardReport.Released, relay.ReleaseAll());
        Assert.Null(relay.ReleaseAll());
        Assert.Equal(0, relay.HeldKeys);
    }

    [Fact]
    public async Task RunSendsEveryChangeAndReleasesAtTheEnd()
    {
        var sink = new RecordingKeyboardSink();
        var input = Channel.CreateUnbounded<KeyboardInputEvent>();
        using var cts = new CancellationTokenSource();
        var run = new KeyboardRelay().RunAsync(input.Reader, sink, cancellationToken: cts.Token);

        input.Writer.TryWrite(Down(65, 0x04));
        input.Writer.TryWrite(Down(65, 0x04)); // repeat
        input.Writer.TryWrite(Down(66, 0x05));
        await WaitAsync(() => sink.Reports.Count >= 2);
        await cts.CancelAsync();
        await run;

        Assert.Equal(
            [new KeyboardReport(0, [0x04]), new KeyboardReport(0, [0x04, 0x05]), KeyboardReport.Released],
            sink.Reports);
    }

    [Fact]
    public async Task RunLogsReportsWithoutTheKeys()
    {
        var sink = new RecordingKeyboardSink();
        var input = Channel.CreateUnbounded<KeyboardInputEvent>();
        var lines = new List<string>();
        input.Writer.TryWrite(Down(65, 0x04));
        input.Writer.TryWrite(Down(66, 0x05));
        input.Writer.TryComplete();

        await new KeyboardRelay().RunAsync(input.Reader, sink, lines.Add);

        Assert.Equal(["[keys] 1 key(s) held", "[keys] 2 key(s) held"], lines);
        Assert.DoesNotContain(lines, l => l.Contains("0x04", StringComparison.Ordinal) || l.Contains("0x05", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunSendsNothingWithoutAKeyboard()
    {
        var sink = new RecordingKeyboardSink { HasKeyboard = false };
        var input = Channel.CreateUnbounded<KeyboardInputEvent>();
        input.Writer.TryWrite(Down(65, 0x04));
        input.Writer.TryComplete();

        await new KeyboardRelay().RunAsync(input.Reader, sink);

        Assert.Empty(sink.Reports);
    }

    // Polls in real time and fails loudly instead of giving up quietly.
    private static async Task WaitAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out after 10 s waiting for the condition.");
            }

            await Task.Delay(5);
        }
    }

    private sealed class RecordingKeyboardSink : IKeyboardSink
    {
        public bool HasKeyboard { get; init; } = true;

        public List<KeyboardReport> Reports { get; } = [];

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
        {
            lock (Reports)
            {
                Reports.Add(new KeyboardReport(modifiers, keys.ToArray()));
            }

            return ValueTask.CompletedTask;
        }
    }
}
