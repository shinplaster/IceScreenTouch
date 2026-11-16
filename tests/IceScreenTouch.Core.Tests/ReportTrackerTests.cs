// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

/// <summary>What a script holds when the session ends is let go of on the device (the neutral reports of a stopping session).</summary>
public class ReportTrackerTests
{
    [Fact]
    public async Task AFingerLeftDownIsLiftedAtItsLastPositionThenTheEmptyReportFollows()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        await tracker.SendAsync(new[] { new TouchContact(0, 100, 200) }, CancellationToken.None);
        await tracker.SendAsync(new[] { new TouchContact(0, 1234, 4321), new TouchContact(1, 5, 6) }, CancellationToken.None);
        device.Touches.Clear();

        await tracker.ReleaseAllAsync();

        Assert.Equal(2, device.Touches.Count);
        Assert.Equal([new TouchContact(0, 1234, 4321, false), new TouchContact(1, 5, 6, false)], device.Touches[0]);
        Assert.Empty(device.Touches[1]);
        Assert.DoesNotContain(device.Touches, t => t.Any(c => c is { X: 0, Y: 0 }));
    }

    [Fact]
    public async Task AKeyLeftHeldIsReleasedWithAnEmptyKeyboardReport()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        await tracker.SendKeyboardAsync(0x02, new byte[] { 0x04 }, CancellationToken.None);
        device.Keyboard.Clear();

        await tracker.ReleaseAllAsync();

        var report = Assert.Single(device.Keyboard);
        Assert.Equal((byte)0, report.Modifiers);
        Assert.Empty(report.Keys);
    }

    [Fact]
    public async Task HeldTellsWhatIsStillDownUntilTheRelease()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        Assert.Equal((false, false), tracker.Held);

        await tracker.SendAsync(new[] { new TouchContact(0, 100, 200) }, CancellationToken.None);
        await tracker.SendKeyboardAsync(0, new byte[] { 0x04 }, CancellationToken.None);
        Assert.Equal((true, true), tracker.Held);

        await tracker.ReleaseAllAsync();
        Assert.Equal((false, false), tracker.Held);
    }

    [Fact]
    public async Task AModifierAloneCountsAsHeld()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        await tracker.SendKeyboardAsync(0x02, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        device.Keyboard.Clear();

        await tracker.ReleaseAllAsync();

        Assert.Single(device.Keyboard);
    }

    [Fact]
    public async Task NothingIsSentWhenEverythingIsAlreadyNeutral()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        await tracker.SendAsync(new[] { new TouchContact(0, 7, 8) }, CancellationToken.None);
        await tracker.SendAsync(new[] { new TouchContact(0, 7, 8, false) }, CancellationToken.None);
        await tracker.SendAsync(ReadOnlyMemory<TouchContact>.Empty, CancellationToken.None);
        await tracker.SendKeyboardAsync(0, new byte[] { 0x04 }, CancellationToken.None);
        await tracker.SendKeyboardAsync(0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        device.Touches.Clear();
        device.Keyboard.Clear();

        await tracker.ReleaseAllAsync();

        Assert.Empty(device.Touches);
        Assert.Empty(device.Keyboard);
    }

    [Fact]
    public async Task AFailedSendStillCountsAsHeldBecauseItMayHaveReachedTheDevice()
    {
        var device = new Device { FailNextTouch = true };
        var tracker = new ReportTracker(device);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tracker.SendAsync(new[] { new TouchContact(0, 9, 9) }, CancellationToken.None));
        device.Touches.Clear();

        await tracker.ReleaseAllAsync();

        Assert.Equal(2, device.Touches.Count);
        Assert.All(device.Touches[0], c => Assert.False(c.Tip));
    }

    [Fact]
    public async Task TheReleaseGoesOutEvenWhenTheTokenIsCancelled()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        await tracker.SendAsync(new[] { new TouchContact(0, 3, 4) }, CancellationToken.None);
        await tracker.SendKeyboardAsync(0, new byte[] { 0x04 }, CancellationToken.None);
        device.Touches.Clear();
        device.Keyboard.Clear();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await tracker.ReleaseAllAsync(cts.Token);

        Assert.Equal(2, device.Touches.Count);
        Assert.Single(device.Keyboard);
    }

    [Fact]
    public async Task ReleasingTwiceSendsTheNeutralStateOnce()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        await tracker.SendAsync(new[] { new TouchContact(0, 3, 4) }, CancellationToken.None);
        await tracker.ReleaseAllAsync();
        var sent = device.Touches.Count;

        await tracker.ReleaseAllAsync();

        Assert.Equal(sent, device.Touches.Count);
    }

    [Fact]
    public async Task ABrokenTouchLinkDoesNotKeepTheKeyboardFromBeingReleased()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        await tracker.SendAsync(new[] { new TouchContact(0, 3, 4) }, CancellationToken.None);
        await tracker.SendKeyboardAsync(0, new byte[] { 0x04 }, CancellationToken.None);
        device.Keyboard.Clear();
        device.FailNextTouch = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await tracker.ReleaseAllAsync());

        Assert.Single(device.Keyboard);
    }

    [Fact]
    public async Task SendsThatArriveAfterTheReleaseAreDroppedUnlessTheyAreNeutral()
    {
        var device = new Device();
        var tracker = new ReportTracker(device);
        await tracker.SendAsync(new[] { new TouchContact(0, 100, 200) }, CancellationToken.None);
        await tracker.SendKeyboardAsync(0x02, new byte[] { 0x04 }, CancellationToken.None);
        await tracker.ReleaseAllAsync();
        device.Touches.Clear();
        device.Keyboard.Clear();
        device.HomePresses = 0;

        // A late thread presses again: none of it reaches the device, and nothing is remembered as held.
        await tracker.SendAsync(new[] { new TouchContact(0, 7, 8) }, CancellationToken.None);
        await tracker.SendKeyboardAsync(0, new byte[] { 0x05 }, CancellationToken.None);
        await tracker.SendKeyboardAsync(0x01, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        await tracker.PressHomeAsync(CancellationToken.None);
        Assert.Empty(device.Touches);
        Assert.Empty(device.Keyboard);
        Assert.Equal(0, device.HomePresses);

        // Neutral sends are harmless and still go through; a second release has nothing to do.
        await tracker.SendAsync(new[] { new TouchContact(0, 7, 8, false) }, CancellationToken.None);
        await tracker.SendAsync(ReadOnlyMemory<TouchContact>.Empty, CancellationToken.None);
        await tracker.SendKeyboardAsync(0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal(2, device.Touches.Count);
        Assert.Single(device.Keyboard);
        device.Touches.Clear();
        device.Keyboard.Clear();
        await tracker.ReleaseAllAsync();
        Assert.Empty(device.Touches);
        Assert.Empty(device.Keyboard);
    }

    [Fact]
    public async Task ASinkWithoutAKeyboardReportsNoneAndSendsNothingForIt()
    {
        var sink = new TouchOnly();
        var tracker = new ReportTracker(sink);

        Assert.False(tracker.HasKeyboard);
        Assert.False(tracker.HasHomeButton);
        await tracker.SendKeyboardAsync(0, new byte[] { 0x04 }, CancellationToken.None);
        await tracker.ReleaseAllAsync();
    }

    [Fact]
    public void ItPassesTheStateOfTheInnerSinkThrough()
    {
        var device = new Device { Subscribed = true, KeyboardPresent = true };
        var tracker = new ReportTracker(device);

        Assert.True(tracker.IsSubscribed);
        Assert.True(tracker.HasKeyboard);
        Assert.True(tracker.HasHomeButton);
    }

    private sealed class TouchOnly : ITouchSink
    {
        public bool IsSubscribed => true;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class Device : ITouchSink, IKeyboardSink, IHomeButtonSink
    {
        public bool Subscribed { get; set; } = true;

        public bool KeyboardPresent { get; set; } = true;

        public bool FailNextTouch { get; set; }

        public List<TouchContact[]> Touches { get; } = [];

        public List<(byte Modifiers, byte[] Keys)> Keyboard { get; } = [];

        public bool IsSubscribed => Subscribed;

        public bool HasKeyboard => KeyboardPresent;

        public bool HasHomeButton => KeyboardPresent;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
        {
            Touches.Add(contacts.ToArray());
            if (FailNextTouch)
            {
                FailNextTouch = false;
                throw new InvalidOperationException("link down");
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
        {
            Keyboard.Add((modifiers, keys.ToArray()));
            return ValueTask.CompletedTask;
        }

        public int HomePresses { get; set; }

        public ValueTask PressHomeAsync(CancellationToken cancellationToken)
        {
            HomePresses++;
            return ValueTask.CompletedTask;
        }
    }
}
