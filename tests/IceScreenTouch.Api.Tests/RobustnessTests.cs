// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

/// <summary>Round-1 review items for the controller: stuck gate, threads of the cancellation, user gestures, logging.</summary>
public class RobustnessTests : ApiTestBase
{
    private static readonly TapRequest Tap = new() { X = 0.5, Y = 0.5, Space = "device", HoldMs = 70 };

    [Fact]
    public async Task AFailingGiveBackOfTheInputDoesNotLeaveTheGateClosed()
    {
        Host.LeaseDisposeThrows = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => Time.Drive(Controller.TapAsync(Tap)));

        Assert.False(Controller.IsOperating);
        Host.LeaseDisposeThrows = false;
        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);
    }

    [Fact]
    public async Task AFailingGiveBackAtTheEndOfASessionDoesNotLeaveTheGateClosed()
    {
        var session = (await Controller.TryOpenSessionAsync()).Value!;
        Host.Lease!.ThrowOnDispose = true;

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await session.DisposeAsync());

        Assert.False(Controller.IsOperating);
        Assert.True((await Controller.HomeAsync()).IsSuccess);
    }

    [Fact]
    public async Task TheLiftAfterACancelRunsOnAnotherThreadThanTheCaller()
    {
        var running = Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5, Space = "device", HoldMs = 5000 }).AsTask();
        await Eventually(() => Touch.Downs.Any(), "the finger going down");
        var callerThread = 0;
        var caller = new Thread(() =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            Controller.CancelCurrent();
            SpinWait.SpinUntil(() => Touch.Frames[^1].IsLift || Touch.Frames[^1].IsEmpty, TimeSpan.FromSeconds(10));
        });

        caller.Start();
        caller.Join();
        AssertError(await running, ApiErrorCodes.Cancelled);

        Assert.NotEqual(callerThread, Touch.ThreadIds[^1]);
    }

    [Fact]
    public async Task TheLiftAfterUserInputRunsOnAnotherThreadThanTheUsersThread()
    {
        var running = Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5, Space = "device", HoldMs = 5000 }).AsTask();
        await Eventually(() => Touch.Downs.Any(), "the finger going down");
        var userThread = 0;
        var user = new Thread(() =>
        {
            userThread = Environment.CurrentManagedThreadId;
            Host.Lease!.SimulateUserInput();
            SpinWait.SpinUntil(() => Touch.Frames[^1].IsLift || Touch.Frames[^1].IsEmpty, TimeSpan.FromSeconds(10));
        });

        user.Start();
        user.Join();
        AssertError(await running, ApiErrorCodes.UserInterrupted);

        Assert.NotEqual(userThread, Touch.ThreadIds[^1]);
    }

    [Fact]
    public async Task CancellingWhileOperationsEndNeverThrows()
    {
        using var stop = new CancellationTokenSource();
        var canceller = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                Controller.CancelCurrent();
            }
        });

        for (var i = 0; i < 300; i++)
        {
            var result = await Time.Drive(Controller.HomeAsync());
            Assert.True(result.IsSuccess || result.Error!.Code == ApiErrorCodes.Cancelled);
        }

        await stop.CancelAsync();
        await canceller;
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task AGestureInProgressRefusesInputOperations()
    {
        Host.UserInputActive = true;

        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.UserActive);
        AssertError(await Controller.PressKeyAsync(new KeyPressRequest { Key = "a" }), ApiErrorCodes.UserActive);
        AssertError(await Controller.HomeAsync(), ApiErrorCodes.UserActive);
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.UserActive);
        Assert.Empty(Host.Leases);
        Assert.Empty(Touch.Frames);
        Assert.False(Controller.IsOperating);

        Host.UserInputActive = false;
        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);
    }

    [Fact]
    public async Task AGestureInProgressDoesNotStopSettingsAndStatus()
    {
        Host.UserInputActive = true;

        Assert.True((await Controller.GetStatusAsync()).IsSuccess);
        Assert.True((await Controller.SetRotationAsync(new RotationRequest { Landscape = 270 })).IsSuccess);
    }

    [Fact]
    public async Task TheInterruptedTokenCanBeReadAfterTheSessionEnded()
    {
        var session = (await Controller.TryOpenSessionAsync()).Value!;
        await session.DisposeAsync();

        Assert.False(session.Interrupted.IsCancellationRequested);
        Assert.Null(session.InterruptionError);
    }

    [Fact]
    public async Task ARefusalIsLoggedOnceUntilTheIntervalPasses()
    {
        Host.Running = false;
        for (var i = 0; i < 50; i++)
        {
            AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.NotRunning);
        }

        Host.Running = true;
        AssertError(await Controller.TapAsync(new TapRequest { X = 5, Y = 5 }), ApiErrorCodes.InvalidRequest);
        Assert.Equal(["[api] tap not_running", "[api] tap invalid_request"], Log);

        Time.Advance(TimeSpan.FromSeconds(11));
        Host.Running = false;
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.NotRunning);
        Assert.Equal("[api] tap not_running (x50)", Log[^1]);
    }

    [Fact]
    public async Task SuccessesAndInterruptionsAreStillLoggedEveryTime()
    {
        for (var i = 0; i < 3; i++)
        {
            await Time.Drive(Controller.HomeAsync());
        }

        Assert.Equal(["[api] home ok 0 ms", "[api] home ok 0 ms", "[api] home ok 0 ms"], Log);
    }

    [Fact]
    public async Task HeldBackCountsAreReportedWhenTheControllerIsDisposed()
    {
        Host.Running = false;
        for (var i = 0; i < 4; i++)
        {
            await Controller.HomeAsync();
        }

        Controller.Dispose();

        Assert.Equal(["[api] home not_running", "[api] home not_running (x3)"], Log);
    }
}
