// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.RegularExpressions;
using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

public class SessionAndSettingsTests : ApiTestBase
{
    // ---- Session ----

    [Fact]
    public async Task ASessionHoldsTheLeaseUntilItIsDisposed()
    {
        var opened = await Controller.TryOpenSessionAsync();
        Assert.True(opened.IsSuccess);
        var session = opened.Value!;

        Assert.False(session.Interrupted.IsCancellationRequested);
        Assert.Null(session.InterruptionError);
        Assert.False(Host.Lease!.Disposed);
        Assert.True(Controller.IsOperating);

        await session.DisposeAsync();
        await session.DisposeAsync(); // twice is fine

        Assert.True(Host.Lease.Disposed);
        Assert.Equal(1, Host.Journal.Count(j => j == "lease:disposed"));
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task TheUserInterruptsASession()
    {
        await using var session = (await Controller.TryOpenSessionAsync()).Value!;

        Host.Lease!.SimulateUserInput();

        await Eventually(() => session.Interrupted.IsCancellationRequested, "the session being interrupted");
        Assert.Equal(ApiErrorCodes.UserInterrupted, session.InterruptionError!.Code);
    }

    [Fact]
    public async Task CancelStopsASession()
    {
        await using var session = (await Controller.TryOpenSessionAsync()).Value!;

        Assert.True(Controller.CancelCurrent());

        await Eventually(() => session.Interrupted.IsCancellationRequested, "the session being interrupted");
        Assert.Equal(ApiErrorCodes.Cancelled, session.InterruptionError!.Code);
    }

    [Fact]
    public async Task ASessionEndsWhenItsConnectionIsCancelled()
    {
        using var connection = new CancellationTokenSource();
        await using var session = (await Controller.TryOpenSessionAsync(connection.Token)).Value!;

        await connection.CancelAsync();

        await Eventually(() => session.Interrupted.IsCancellationRequested, "the session being interrupted");
    }

    [Fact]
    public async Task ASessionResolvesFramesPerMessageAgainstTheCurrentState()
    {
        await using var session = (await Controller.TryOpenSessionAsync()).Value!;
        Host.Picture = new PictureInfo(844, 390);
        Host.Landscape = Rotation.R270;

        var screen = session.ResolveFrame(null, null);
        Assert.True(screen.IsSuccess);
        Assert.Equal(Expected(0.1, 0.9, Rotation.R270), screen.Value!.ToAxis(new PointD(0.1, 0.9)));

        Assert.True(session.ResolveFrame("device", null).IsSuccess);
        AssertError(session.ResolveFrame("pixel", null), ApiErrorCodes.InvalidRequest);
        AssertError(session.ResolveFrame("moon", null), ApiErrorCodes.InvalidRequest);

        Host.Picture = null;
        AssertError(session.ResolveFrame("screen", null), ApiErrorCodes.NoPicture);
        Assert.True(session.ResolveFrame("device", null).IsSuccess);
    }

    [Fact]
    public async Task OpeningASessionWithACancelledTokenFailsAndFreesTheGate()
    {
        using var connection = new CancellationTokenSource();
        await connection.CancelAsync();

        var opened = await Controller.TryOpenSessionAsync(connection.Token);

        AssertError(opened, ApiErrorCodes.Cancelled);
        Assert.False(Controller.IsOperating);
    }

    // ---- Rotation, target, status ----

    [Fact]
    public async Task SetRotationChangesTheHostAndReturnsTheNewValues()
    {
        var result = await Controller.SetRotationAsync(new RotationRequest { Landscape = 270 });

        Assert.True(result.IsSuccess);
        Assert.Equal(new RotationInfo(270, 0), result.Value);
        Assert.Equal([((Rotation?)Rotation.R270, (Rotation?)null)], Host.RotationSets);

        var both = await Controller.SetRotationAsync(new RotationRequest { Landscape = 90, Portrait = 180 });
        Assert.Equal(new RotationInfo(90, 180), both.Value);
        Assert.Equal(new RotationInfo(90, 180), Controller.GetRotation().Value);
    }

    [Fact]
    public async Task SetRotationRejectsOtherValues()
    {
        AssertError(await Controller.SetRotationAsync(new RotationRequest { Landscape = 0 }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.SetRotationAsync(new RotationRequest { Landscape = 180 }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.SetRotationAsync(new RotationRequest { Portrait = 90 }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.SetRotationAsync(new RotationRequest { Portrait = 270 }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.SetRotationAsync(new RotationRequest()), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.SetRotationAsync(null), ApiErrorCodes.InvalidRequest);
        Assert.Empty(Host.RotationSets);
    }

    [Fact]
    public async Task SetRotationNeedsNeitherInputNorPictureNorADevice()
    {
        Host.Picture = null;
        Host.TouchSink.IsSubscribed = false;
        Host.UserJustActed(TimeSpan.Zero);

        var result = await Controller.SetRotationAsync(new RotationRequest { Portrait = 180 });

        Assert.True(result.IsSuccess);
        Assert.Empty(Host.Leases);
    }

    [Fact]
    public async Task SetTargetChoosesAKnownDeviceOrAutomatic()
    {
        Assert.True((await Controller.SetTargetAsync(new TargetRequest { DeviceId = "iphone-1" })).IsSuccess);
        Assert.True((await Controller.SetTargetAsync(new TargetRequest { DeviceId = null })).IsSuccess);
        Assert.Equal(["iphone-1", null], Host.TargetChoices);

        AssertError(await Controller.SetTargetAsync(new TargetRequest { DeviceId = "nobody" }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.SetTargetAsync(null), ApiErrorCodes.InvalidRequest);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task SetTargetNeedsARunningSession()
    {
        Host.Running = false;

        AssertError(await Controller.SetTargetAsync(new TargetRequest { DeviceId = "iphone-1" }), ApiErrorCodes.NotRunning);
        Assert.Empty(Host.TargetChoices);
    }

    [Fact]
    public void StatusComesFromTheHostWithTheOperatingFlagFromTheController()
    {
        var status = Controller.GetStatus();

        Assert.True(status.IsSuccess);
        Assert.Equal(1, status.Value!.ApiVersion);
        Assert.Equal("iphone-1", status.Value.TargetDeviceId);
        Assert.Equal("portrait", status.Value.Picture!.Orientation);
        Assert.Equal(new RotationInfo(90, 0), status.Value.Rotation);
        Assert.False(status.Value.Operating);
    }

    // ---- Logging ----

    [Fact]
    public async Task LogLinesHaveOnlyTheKindTheResultAndTheTime()
    {
        const string Secret = "secret-hunter2";
        await Time.Drive(Controller.SwipeAsync(new SwipeRequest
        {
            From = new ApiPoint { X = 0.123456, Y = 0.654321 },
            To = new ApiPoint { X = 0.777777, Y = 0.888888 },
            Space = "device",
        }));
        await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = Secret }));
        await Time.Drive(Controller.PressKeyAsync(new KeyPressRequest { Key = "q", Modifiers = ["cmd"] }));
        await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.31415, Y = 0.27182, Space = "device" }));
        await Controller.TapAsync(new TapRequest { X = 9.87654, Y = 0.5 });
        await Time.Drive(Controller.HomeAsync());
        Host.UserJustActed(TimeSpan.Zero);
        await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 });

        Assert.Equal(7, Log.Count); // refusals have no time: they are rate limited
        Assert.All(Log, line => Assert.Matches(new Regex(@"^\[api\] [a-z-]+ [a-z_]+( \d+ ms)?$"), line));
        Assert.Equal(
            [
                "[api] swipe ok 300 ms",
                $"[api] key-type ok {Secret.Length * 50} ms",
                "[api] key-press ok 60 ms",
                "[api] tap ok 80 ms",
                "[api] tap invalid_request",
                "[api] home ok 0 ms",
                "[api] tap user_active",
            ],
            Log);
        var all = string.Concat(Log);
        Assert.DoesNotContain("0.123", all);
        Assert.DoesNotContain("0.654", all);
        Assert.DoesNotContain("secret", all);
        Assert.DoesNotContain("hunter", all);
        Assert.DoesNotContain("9.87", all);
        Assert.DoesNotContain("cmd", all);
    }

    [Fact]
    public async Task SessionsAndInterruptionsAreLoggedByCodeOnly()
    {
        var session = (await Controller.TryOpenSessionAsync()).Value!;
        Time.Advance(Ms(250));
        await session.DisposeAsync();

        Assert.Equal(["[api] session-open ok 0 ms", "[api] session closed 250 ms"], Log);
    }

    [Fact]
    public async Task AnInterruptedOperationIsLoggedWithItsCode()
    {
        var running = Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5 }).AsTask();
        Time.Wait(100);
        Host.Lease!.SimulateUserInput();
        await running;

        Assert.Equal(["[api] long-press user_interrupted 100 ms"], Log);
    }

    [Fact]
    public async Task AnExceptionIsLoggedByKindOnlyAndRethrown()
    {
        var host = new ThrowingHost(Time);
        using var controller = new ApiController(host, () => true, Time, Log.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await controller.HomeAsync());

        Assert.Equal(["[api] home failed"], Log);
        Assert.False(controller.IsOperating);
    }

    private sealed class ThrowingHost(Microsoft.Extensions.Time.Testing.FakeTimeProvider time) : IApiHost
    {
        private readonly FakeHost _inner = new(time);

        public ITouchSink Touch => _inner.Touch;

        public IKeyboardSink Keyboard => _inner.Keyboard;

        public IHomeButtonSink HomeButton => new ThrowingHome();

        public ApiHostSnapshot GetSnapshot() => _inner.GetSnapshot();

        public ApiStatus GetStatus() => _inner.GetStatus();

        public ValueTask<IInputLease> AcquireInputAsync(CancellationToken cancellationToken) => _inner.AcquireInputAsync(cancellationToken);

        public void SetRotation(Rotation? landscape, Rotation? portrait) => _inner.SetRotation(landscape, portrait);

        public bool ChooseTarget(string? deviceId) => _inner.ChooseTarget(deviceId);

        private sealed class ThrowingHome : IHomeButtonSink
        {
            public bool HasHomeButton => true;

            public ValueTask PressHomeAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("boom with a secret");
        }
    }
}
