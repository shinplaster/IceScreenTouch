// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

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
        using var cts = new CancellationTokenSource();

        // Cancel only once the touch-down frame is out (a fixed delay can fire before it on a loaded machine).
        var tap = player.TapAsync(new AxisPoint(5000, 5000), TimeSpan.FromSeconds(30), cts.Token).AsTask();
        // An early exception from the operation surfaces at once instead of after the 30 s bound.
        await Task.WhenAny(sink.FirstFrame, tap).WaitAsync(TimeSpan.FromSeconds(30));
        if (tap.IsFaulted)
        {
            await tap;
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tap);

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
        using var cts = new CancellationTokenSource();
        var key = new HidKey(0x2A, HidModifiers.LeftGui); // ⌘ + Backspace, held long

        // Cancel only once the key-down report is out (a fixed delay can fire before it on a loaded machine).
        var press = sink.PressAsync(key, TimeSpan.FromSeconds(30), cancellationToken: cts.Token).AsTask();
        // An early exception from the operation surfaces at once instead of after the 30 s bound.
        await Task.WhenAny(sink.KeyDown, press).WaitAsync(TimeSpan.FromSeconds(30));
        if (press.IsFaulted)
        {
            await press;
        }

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => press);

        Assert.Contains(sink.Reports, r => r.Keys.Contains((byte)0x2A));
        Assert.Equal(KeyboardReport.Released, sink.Reports[^1]);
    }

    // Like the BLE peripheral: a send with a cancelled token does not go out.
    private sealed class CancelAwareTouchSink : ITouchSink
    {
        public bool IsSubscribed => true;

        private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<TouchContact[]> Frames { get; } = [];

        /// <summary>Completes when the first frame has been recorded.</summary>
        public Task FirstFrame => _firstFrame.Task;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frames.Add(contacts.ToArray());
            _firstFrame.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CancelAwareKeyboardSink : IKeyboardSink
    {
        public bool HasKeyboard => true;

        private readonly TaskCompletionSource _keyDown = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<KeyboardReport> Reports { get; } = [];

        /// <summary>Completes when a report with a key in it (not only a modifier) has been recorded.</summary>
        public Task KeyDown => _keyDown.Task;

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reports.Add(new KeyboardReport(modifiers, keys.ToArray()));
            if (keys.Length > 0)
            {
                _keyDown.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }
}
