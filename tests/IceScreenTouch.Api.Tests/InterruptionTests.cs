// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

public class InterruptionTests : ApiTestBase
{
    private static readonly ApiPoint From = new() { X = 0.3, Y = 0.9 };
    private static readonly ApiPoint To = new() { X = 0.3, Y = 0.1 };

    private static readonly SwipeRequest LongSwipe = new() { From = From, To = To, Space = "device", DurationMs = 1000 };

    [Fact]
    public async Task TheUserInterruptsASwipeAndEverythingIsLiftedAtTheLastPosition()
    {
        var running = Controller.SwipeAsync(LongSwipe).AsTask();
        Time.Wait(170);
        var downsBefore = Touch.Downs.ToList();
        Assert.NotEmpty(downsBefore);
        Assert.False(running.IsCompleted);

        Host.Lease!.SimulateUserInput();
        var result = await running;

        AssertError(result, ApiErrorCodes.UserInterrupted);
        Assert.Equal(409, result.Error!.Status);
        // No finger frame after the interruption except the lift, which is at the last position (never (0, 0)).
        var last = downsBefore[^1];
        Assert.Equal(downsBefore.Count + 2, Touch.Frames.Count);
        Assert.True(Touch.Frames[^2].IsLift);
        Assert.Equal(last.First, Touch.Frames[^2].First);
        Assert.NotEqual(new AxisPoint(0, 0), Touch.Frames[^2].First);
        Assert.True(Touch.Frames[^1].IsEmpty);
        Assert.DoesNotContain(Touch.Frames, f => f.At > Ms(170));
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task TheLeaseIsGivenBackOnlyAfterTheLift()
    {
        var running = Controller.SwipeAsync(LongSwipe).AsTask();
        Time.Wait(100);
        Host.Lease!.SimulateUserInput();
        await running;

        Assert.True(Host.Lease.Disposed);
        Assert.Equal(["lease:disposed"], Host.Journal.TakeLast(1));
        Assert.Equal(["touch:lift", "touch:empty", "lease:disposed"], Host.Journal.TakeLast(3));
        Assert.Equal(1, Host.Journal.Count(j => j == "lease:disposed"));
    }

    [Fact]
    public async Task TheUserInterruptsAKeyPressAndTheKeyIsReleased()
    {
        var running = Controller.PressKeyAsync(new KeyPressRequest { Key = "a", Modifiers = ["cmd"], HoldMs = 500 }).AsTask();
        Time.Wait(100);
        Assert.Contains(Keys.Reports, r => r.Keys.Length > 0);

        Host.Lease!.SimulateUserInput();
        var result = await running;

        AssertError(result, ApiErrorCodes.UserInterrupted);
        Assert.True(Keys.Reports[^1].IsRelease);
        Assert.Equal(["key:release", "lease:disposed"], Host.Journal.TakeLast(2));
        Assert.Equal(1, Keys.Reports.Count(r => r.Keys.Length > 0)); // the key was not pressed again
    }

    [Fact]
    public async Task TheUserInterruptsTypingBetweenKeysAndNothingStaysHeld()
    {
        var running = Controller.TypeTextAsync(new TypeTextRequest { Text = "abcdefgh", IntervalMs = 200 }).AsTask();
        Time.Wait(300); // inside the pause after the second key
        var pressed = Keys.Reports.Count(r => r.Keys.Length > 0);
        Assert.InRange(pressed, 1, 3);

        Host.Lease!.SimulateUserInput();
        var result = await running;

        AssertError(result, ApiErrorCodes.UserInterrupted);
        Assert.True(Keys.Reports[^1].IsRelease);
        Assert.Equal(pressed, Keys.Reports.Count(r => r.Keys.Length > 0));
        Assert.True(Host.Lease.Disposed);
    }

    [Fact]
    public async Task TheUserInterruptsAPinchAndBothFingersLiftAtTheirLastPositions()
    {
        var running = Controller.PinchAsync(new PinchRequest { Center = new ApiPoint { X = 0.5, Y = 0.5 }, Space = "device", StartRadius = 0.05, EndRadius = 0.4, DurationMs = 2000 }).AsTask();
        Time.Wait(200);
        var last = Touch.Downs.Last();

        Host.Lease!.SimulateUserInput();
        var result = await running;

        AssertError(result, ApiErrorCodes.UserInterrupted);
        Assert.Equal(last.Contacts.Select(c => c.Lifted()), Touch.Frames[^2].Contacts);
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task CancelEndsTheOperationWithCancelledAfterTheLift()
    {
        var running = Controller.SwipeAsync(LongSwipe).AsTask();
        Time.Wait(100);

        Assert.True(Controller.CancelCurrent());
        var result = await running;

        AssertError(result, ApiErrorCodes.Cancelled);
        Assert.Equal(499, result.Error!.Status);
        Assert.Equal(["touch:lift", "touch:empty", "lease:disposed"], Host.Journal.TakeLast(3));
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public void CancelWithNothingRunningReturnsFalse()
    {
        Assert.False(Controller.CancelCurrent());
    }

    [Fact]
    public async Task CancelDuringATapSequenceStopsBeforeTheNextTap()
    {
        var running = Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, Count = 5, HoldMs = 80, IntervalMs = 500 }).AsTask();
        Time.Wait(300); // between the first and the second tap

        Controller.CancelCurrent();
        var result = await running;

        AssertError(result, ApiErrorCodes.Cancelled);
        Assert.Single(Touch.Downs);
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task ADisconnectedClientStopsAndLifts()
    {
        using var disconnect = new CancellationTokenSource();
        var running = Controller.SwipeAsync(LongSwipe, disconnect.Token).AsTask();
        Time.Wait(120);
        var last = Touch.Downs.Last();

        await disconnect.CancelAsync();
        var result = await running;

        AssertError(result, ApiErrorCodes.Cancelled);
        Assert.Equal(last.First, Touch.Frames[^2].First);
        Assert.True(Touch.Frames[^2].IsLift);
        Assert.True(Touch.Frames[^1].IsEmpty);
        Assert.True(Host.Lease!.Disposed);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task ACancelledTokenBeforeTheStartSendsNothingFingerRelated()
    {
        using var disconnect = new CancellationTokenSource();
        await disconnect.CancelAsync();

        var result = await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 }, disconnect.Token);

        AssertError(result, ApiErrorCodes.Cancelled);
        Assert.DoesNotContain(Touch.Frames, f => f.IsDown);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task TheUserWinsWhenBothHappen()
    {
        var running = Controller.SwipeAsync(LongSwipe).AsTask();
        Time.Wait(50);

        Controller.CancelCurrent();
        Host.Lease!.SimulateUserInput();
        var result = await running;

        AssertError(result, ApiErrorCodes.UserInterrupted);
    }

    [Fact]
    public async Task AfterAnInterruptionTheGateIsFreeAndANewOperationRuns()
    {
        var running = Controller.SwipeAsync(LongSwipe).AsTask();
        Time.Wait(50);
        Host.Lease!.SimulateUserInput();
        await running;

        var again = await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 }));

        Assert.True(again.IsSuccess);
        Assert.Equal(2, Host.Leases.Count);
    }

    [Fact]
    public async Task AFailingSinkStillLiftsAndGivesTheInputBack()
    {
        var failing = new FailingHost(Time);
        using var controller = new ApiController(failing, () => true, Time);

        var running = controller.SwipeAsync(LongSwipe).AsTask();
        Time.Wait(50);
        failing.TouchSink.FailAfter = 3;
        Time.Wait(100);

        await Assert.ThrowsAsync<InvalidOperationException>(() => running);
        Assert.False(controller.IsOperating);
        Assert.True(failing.Lease!.Disposed);
    }

    private sealed class FailingHost : IApiHost
    {
        private readonly FakeHost _inner;

        public FailingHost(Microsoft.Extensions.Time.Testing.FakeTimeProvider time)
        {
            _inner = new FakeHost(time);
            TouchSink = new FailingSink(_inner.TouchSink);
        }

        public FailingSink TouchSink { get; }

        public FakeLease? Lease => _inner.Lease;

        public ITouchSink Touch => TouchSink;

        public IKeyboardSink Keyboard => _inner.Keyboard;

        public IHomeButtonSink HomeButton => _inner.HomeButton;

        public ApiHostSnapshot GetSnapshot() => _inner.GetSnapshot();

        public ApiStatus GetStatus() => _inner.GetStatus();

        public ValueTask<IInputLease> AcquireInputAsync(CancellationToken cancellationToken) => _inner.AcquireInputAsync(cancellationToken);

        public void SetRotation(Rotation? landscape, Rotation? portrait) => _inner.SetRotation(landscape, portrait);

        public bool ChooseTarget(string? deviceId) => _inner.ChooseTarget(deviceId);
    }

    private sealed class FailingSink(FakeTouchSink inner) : ITouchSink
    {
        private int _sent;

        public int FailAfter { get; set; } = int.MaxValue;

        public bool IsSubscribed => inner.IsSubscribed;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
        {
            // Only the moves fail (the link broke); the lift must still be tried and may go through.
            if (contacts.Length > 0 && contacts.Span[0].Tip && ++_sent > FailAfter)
            {
                throw new InvalidOperationException("link lost");
            }

            return inner.SendAsync(contacts, cancellationToken);
        }
    }
}
