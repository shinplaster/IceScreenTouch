// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Api.Tests;

// The adapter between the core's ApiInputArbiter and the controller's IApiHost. The bridge's answer is played by
// the test (CompleteAcquire); the bridge itself is tested in IceScreenTouch.Core.Tests.
public class ArbiterInputTests : ApiTestBase
{
    private readonly ApiInputArbiter _arbiter;
    private readonly ArbiterInput _input;

    public ArbiterInputTests()
    {
        _arbiter = new ApiInputArbiter(Time);
        _input = new ArbiterInput(_arbiter);
    }

    private async Task<IInputLease> AcquireAsync()
    {
        var pending = _input.AcquireAsync(CancellationToken.None).AsTask();
        Assert.True(_arbiter.CompleteAcquire(true));
        return await pending;
    }

    [Fact]
    public async Task TheLeaseCarriesTheUserInputTokenAndGivesTheInputBackOnDispose()
    {
        var lease = await AcquireAsync();
        Assert.True(_arbiter.HoldsInput);
        Assert.False(lease.UserInput.IsCancellationRequested);

        _arbiter.NotifyUserInput();
        Assert.True(lease.UserInput.IsCancellationRequested);

        await lease.DisposeAsync();
        await lease.DisposeAsync(); // idempotent
        Assert.False(_arbiter.HoldsInput);
        Assert.Equal(ApiInputRefusal.None, (await TryAgain()).Refusal);
    }

    private async Task<ApiInputAcquisition> TryAgain()
    {
        var pending = _arbiter.TryAcquireAsync().AsTask();
        _arbiter.CompleteAcquire(true);
        return await pending;
    }

    [Fact]
    public async Task AUserGestureIsRefusedAsUserActive()
    {
        _arbiter.SetTouchActive(true);

        var ex = await Assert.ThrowsAsync<InputRefusedException>(async () => await _input.AcquireAsync(CancellationToken.None));

        Assert.Equal(ApiErrorCodes.UserActive, ex.Code);
        Assert.True(_input.UserInputActive);
    }

    [Fact]
    public async Task ASuspendedBridgeOrASecondLeaseIsRefusedAsBusy()
    {
        _arbiter.SetSuspendWanted(true);
        var suspended = await Assert.ThrowsAsync<InputRefusedException>(async () => await _input.AcquireAsync(CancellationToken.None));
        Assert.Equal(ApiErrorCodes.Busy, suspended.Code);

        _arbiter.SetSuspendWanted(false);
        await using var lease = await AcquireAsync();
        var second = await Assert.ThrowsAsync<InputRefusedException>(async () => await _input.AcquireAsync(CancellationToken.None));
        Assert.Equal(ApiErrorCodes.Busy, second.Code);
    }

    [Fact]
    public void LastUserInputIsStampedWithTheSharedClock()
    {
        Assert.Null(_input.LastUserInput);
        Time.Advance(Ms(500));

        _arbiter.StampUserInput();

        Assert.Equal(Time.GetUtcNow(), _input.LastUserInput);
    }

    [Fact]
    public async Task TheControllerTurnsARefusalIntoUserActiveOrBusy()
    {
        Host.RefuseInput = new InputRefusedException(ApiErrorCodes.UserActive, "the user is using the mirror");
        AssertError(await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 }), ApiErrorCodes.UserActive);
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.UserActive);
        Assert.Empty(Touch.Frames);
        Assert.False(Controller.IsOperating); // the gate opened again

        Host.RefuseInput = new InputRefusedException(ApiErrorCodes.Busy, "a measurement is running");
        AssertError(await Controller.HomeAsync(), ApiErrorCodes.Busy);

        Host.RefuseInput = null;
        Assert.True((await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 }))).IsSuccess);
    }

    [Fact]
    public async Task AClosedArbiterIsRefusedAsNotRunning()
    {
        _arbiter.Close();

        var ex = await Assert.ThrowsAsync<InputRefusedException>(async () => await _input.AcquireAsync(CancellationToken.None));

        Assert.Equal(ApiErrorCodes.NotRunning, ex.Code);
    }

    [Fact]
    public async Task CancelCurrentEndsAnAcquisitionThatNeverGetsAnswered()
    {
        Host.AcquireBlocks = true;
        var running = Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 }).AsTask();
        Assert.True(Controller.IsOperating);

        Assert.True(Controller.CancelCurrent());

        AssertError(await running.WaitAsync(TimeSpan.FromSeconds(10)), ApiErrorCodes.Cancelled);
        Assert.False(Controller.IsOperating); // the gate is open again
        Host.AcquireBlocks = false;
        Assert.True((await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 }))).IsSuccess);
    }

    [Fact]
    public async Task DisposingTheControllerEndsASessionOpeningThatNeverGetsAnswered()
    {
        Host.AcquireBlocks = true;
        var opening = Controller.TryOpenSessionAsync().AsTask();
        Assert.True(Controller.IsOperating);

        Controller.Dispose();

        AssertError(await opening.WaitAsync(TimeSpan.FromSeconds(10)), ApiErrorCodes.Cancelled);
        Assert.False(Controller.IsOperating);
    }
}
