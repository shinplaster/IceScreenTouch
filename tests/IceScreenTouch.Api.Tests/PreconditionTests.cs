// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

public class PreconditionTests : ApiTestBase
{
    private static readonly TapRequest Tap = new() { X = 0.5, Y = 0.5 };

    [Fact]
    public async Task LicenseComesFirstAndAppliesToEverything()
    {
        Allowed = false;
        Host.Running = false;
        Host.ProbeSuspended = true;

        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.PurchaseRequired);
        AssertError(await Controller.TapAsync(null), ApiErrorCodes.PurchaseRequired);
        AssertError(Controller.GetStatus(), ApiErrorCodes.PurchaseRequired);
        AssertError(Controller.GetRotation(), ApiErrorCodes.PurchaseRequired);
        AssertError(await Controller.HomeAsync(), ApiErrorCodes.PurchaseRequired);
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.PurchaseRequired);
        Assert.Empty(Touch.Frames);
        Assert.Empty(Host.Leases);
        Assert.Equal(403, ApiErrorCodes.StatusOf(ApiErrorCodes.PurchaseRequired));
    }

    [Fact]
    public async Task InvalidRequestComesBeforeTheStateChecks()
    {
        Host.Running = false;
        var bad = new TapRequest { X = 2, Y = 0.5 };

        AssertError(await Controller.TapAsync(bad), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.TapAsync(null), ApiErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task NotRunningComesBeforeTheProbe()
    {
        Host.Running = false;
        Host.ProbeSuspended = true;

        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.NotRunning);
        Host.Running = true;
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.Busy);
    }

    [Fact]
    public async Task ErrorsComeInTheDocumentedOrder()
    {
        // Everything is wrong at once; fix one thing at a time in the documented order.
        Host.ProbeSuspended = false;
        Host.UserJustActed(Ms(100));
        Host.TouchSink.IsSubscribed = false;
        Host.Picture = null;
        Host.KeyboardSink.HasKeyboard = false;
        Host.HomeSink.HasHomeButton = false;

        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.UserActive);
        AssertError(await Controller.PressKeyAsync(new KeyPressRequest { Key = "a" }), ApiErrorCodes.UserActive);

        Host.LastUserInput = null;
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.NotConnected);
        AssertError(await Controller.PressKeyAsync(new KeyPressRequest { Key = "a" }), ApiErrorCodes.NotConnected);
        AssertError(await Controller.HomeAsync(), ApiErrorCodes.NotConnected);

        Host.TouchSink.IsSubscribed = true;
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.NoPicture);
        AssertError(await Controller.PressKeyAsync(new KeyPressRequest { Key = "a" }), ApiErrorCodes.NoKeyboard);
        AssertError(await Controller.TypeTextAsync(new TypeTextRequest { Text = "a" }), ApiErrorCodes.NoKeyboard);
        AssertError(await Controller.HomeAsync(), ApiErrorCodes.NoHomeButton);

        // Nothing was sent and no lease was taken by any refusal.
        Assert.Empty(Touch.Frames);
        Assert.Empty(Keys.Reports);
        Assert.Empty(Host.Leases);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task DeviceSpaceNeedsNoPicture()
    {
        Host.Picture = null;
        var device = new TapRequest { X = 0.5, Y = 0.5, Space = "device" };

        Assert.True((await Time.Drive(Controller.TapAsync(device))).IsSuccess);
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.NoPicture);
        AssertError(await Controller.TapAsync(new TapRequest { X = 5, Y = 5, Space = "pixel", Ref = new PixelRef { Width = 10, Height = 10 } }), ApiErrorCodes.NoPicture);
    }

    [Fact]
    public async Task KeysAndHomeNeedNoPicture()
    {
        Host.Picture = null;

        Assert.True((await Time.Drive(Controller.PressKeyAsync(new KeyPressRequest { Key = "a" }))).IsSuccess);
        Assert.True((await Time.Drive(Controller.HomeAsync())).IsSuccess);
    }

    [Theory]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    [InlineData(2500, false)]
    public async Task UserActiveLastsOneSecond(int agoMs, bool refused)
    {
        Host.UserJustActed(Ms(agoMs));

        var result = await Time.Drive(Controller.TapAsync(Tap));

        if (refused)
        {
            AssertError(result, ApiErrorCodes.UserActive);
        }
        else
        {
            Assert.True(result.IsSuccess);
        }
    }

    [Fact]
    public async Task UserActiveIsMeasuredWithTheInjectedClock()
    {
        Host.UserJustActed(TimeSpan.Zero);
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.UserActive);

        Time.Advance(Ms(1000));

        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);
    }

    [Fact]
    public async Task OnlyOneOperationRunsAtATime()
    {
        var running = Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5 }).AsTask();
        Assert.True(Controller.IsOperating);

        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.Busy);
        AssertError(await Controller.HomeAsync(), ApiErrorCodes.Busy);
        AssertError(await Controller.SetRotationAsync(new RotationRequest { Landscape = 270 }), ApiErrorCodes.Busy);
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.Busy);

        // Status and rotation are not operations; status says one is running.
        Assert.True(Controller.GetStatus().Value!.Operating);
        Assert.True(Controller.GetRotation().IsSuccess);

        Time.Wait(800);
        Assert.True((await running).IsSuccess);
        Assert.False(Controller.IsOperating);
        Assert.False(Controller.GetStatus().Value!.Operating);
        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);

        // The refusals did not send anything: one long press and one tap only.
        Assert.Equal(2, Touch.Frames.Count(f => f.IsDown));
    }

    [Fact]
    public async Task AnOpenSessionBlocksOperationsButNotStatus()
    {
        var opened = await Controller.TryOpenSessionAsync();
        Assert.True(opened.IsSuccess);
        await using var session = opened.Value!;

        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.Busy);
        AssertError(await Controller.PressKeyAsync(new KeyPressRequest { Key = "a" }), ApiErrorCodes.Busy);
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.Busy);
        AssertError(await Controller.SetTargetAsync(new TargetRequest { DeviceId = null }), ApiErrorCodes.Busy);
        Assert.True(Controller.GetStatus().Value!.Operating);
        Assert.True(Controller.GetRotation().IsSuccess);
        Assert.Empty(Touch.Frames);

        await session.DisposeAsync();

        Assert.False(Controller.IsOperating);
        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);
    }

    [Fact]
    public async Task ASessionNeedsTheSamePreconditionsAsTouchInput()
    {
        Host.Running = false;
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.NotRunning);
        Host.Running = true;
        Host.UserJustActed(Ms(10));
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.UserActive);
        Host.LastUserInput = null;
        Host.TouchSink.IsSubscribed = false;
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.NotConnected);
        Host.TouchSink.IsSubscribed = true;
        Host.Picture = null; // the session itself needs no picture

        var opened = await Controller.TryOpenSessionAsync();

        Assert.True(opened.IsSuccess);
        Assert.False(Host.Lease!.Disposed);
        await opened.Value!.DisposeAsync();
        Assert.True(Host.Lease.Disposed);
    }

    [Fact]
    public async Task ARefusedOperationLeavesTheGateFree()
    {
        Host.TouchSink.IsSubscribed = false;
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.NotConnected);
        Assert.False(Controller.IsOperating);

        Host.TouchSink.IsSubscribed = true;
        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);
    }

    [Fact]
    public void ErrorCodesCarryTheStatusesOfTheDesign()
    {
        var expected = new Dictionary<string, int>
        {
            [ApiErrorCodes.InvalidRequest] = 400,
            [ApiErrorCodes.Unauthorized] = 401,
            [ApiErrorCodes.ForbiddenOrigin] = 403,
            [ApiErrorCodes.PurchaseRequired] = 403,
            [ApiErrorCodes.NotRunning] = 409,
            [ApiErrorCodes.NotConnected] = 409,
            [ApiErrorCodes.NoPicture] = 409,
            [ApiErrorCodes.Busy] = 409,
            [ApiErrorCodes.UserInterrupted] = 409,
            [ApiErrorCodes.UserActive] = 409,
            [ApiErrorCodes.NoKeyboard] = 409,
            [ApiErrorCodes.NoHomeButton] = 409,
            [ApiErrorCodes.PictureChanged] = 409,
            [ApiErrorCodes.InternalError] = 500,
            [ApiErrorCodes.RateLimited] = 429,
            [ApiErrorCodes.Cancelled] = 499,
        };

        foreach (var (code, status) in expected)
        {
            Assert.Equal(status, ApiError.Create(code, "x").Status);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => ApiErrorCodes.StatusOf("nope"));
    }
}
