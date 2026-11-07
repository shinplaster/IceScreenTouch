using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

/// <summary>A cancelled gesture or key press still releases on the device (code review, 2026-11-07).</summary>
public class ReleaseSendTests
{
    [Fact]
    public async Task RunsWithTheCallersTokenWhenItIsNotCancelled()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken seen = default;

        await ReleaseSend.RunAsync(ct => { seen = ct; return ValueTask.CompletedTask; }, cts.Token);

        Assert.Equal(cts.Token, seen);
    }

    [Fact]
    public async Task RunsWithATokenOfItsOwnWhenTheCallersIsCancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var sent = 0;

        await ReleaseSend.RunAsync(
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                sent++;
                return ValueTask.CompletedTask;
            },
            cts.Token);

        Assert.Equal(1, sent);
    }

    [Fact]
    public async Task ACancelledTapStillLiftsTheFinger()
    {
        var sink = new CancelAwareTouchSink();
        var player = new TouchPlayer(sink);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => player.TapAsync(new AxisPoint(5000, 5000), TimeSpan.FromSeconds(30), cts.Token).AsTask());

        Assert.True(sink.Frames[0].All(c => c.Tip));
        Assert.True(sink.Frames[^2].All(c => !c.Tip)); // lifted where it was
        Assert.Equal((ushort)5000, sink.Frames[^2][0].X);
        Assert.Empty(sink.Frames[^1]);
        Assert.False(player.IsDown);
    }

    [Fact]
    public async Task ACancelledKeyPressStillReleasesTheKey()
    {
        var sink = new CancelAwareKeyboardSink();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var key = new HidKey(0x2A, HidModifiers.LeftGui); // ⌘ + Backspace, held long

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sink.PressAsync(key, TimeSpan.FromSeconds(30), cancellationToken: cts.Token).AsTask());

        Assert.Contains(sink.Reports, r => r.Keys.Contains((byte)0x2A));
        Assert.Equal(KeyboardReport.Released, sink.Reports[^1]);
    }

    // Like the BLE peripheral: a send with a cancelled token does not go out.
    private sealed class CancelAwareTouchSink : ITouchSink
    {
        public bool IsSubscribed => true;

        public List<TouchContact[]> Frames { get; } = [];

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(contacts.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelAwareKeyboardSink : IKeyboardSink
    {
        public bool HasKeyboard => true;

        public List<KeyboardReport> Reports { get; } = [];

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reports.Add(new KeyboardReport(modifiers, keys.ToArray()));
            return ValueTask.CompletedTask;
        }
    }
}
