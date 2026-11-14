// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>The sinks of the REST operations refuse a down the moment the user's token is cancelled (no thread-pool hop) and let every lift and release through.</summary>
public class InputGuardTests : ApiTestBase
{
    private (InputGuard Guard, FakeLease Lease) Guarded()
    {
        var lease = new FakeLease(Host.Journal);
        var guard = new InputGuard(Host) { Lease = lease };
        return (guard, lease);
    }

    private static readonly TouchContact Down = new(0, 100, 200);

    [Fact]
    public async Task EverythingPassesWhileTheUserHasNotActed()
    {
        var (guard, _) = Guarded();

        await guard.Touch.SendAsync(new[] { Down }, CancellationToken.None);
        await guard.Keyboard.SendKeyboardAsync(HidModifiers.LeftGui, new byte[] { 4 }, CancellationToken.None);
        await guard.Home.PressHomeAsync(CancellationToken.None);

        Assert.Single(Touch.Frames);
        Assert.Single(Keys.Reports);
        Assert.Equal(1, Host.HomeSink.Presses);
    }

    [Fact]
    public async Task AfterTheUserActedADownIsRefusedAtOnceButLiftsAndReleasesPass()
    {
        var (guard, lease) = Guarded();
        lease.SimulateUserInput();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await guard.Touch.SendAsync(new[] { Down }, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await guard.Keyboard.SendKeyboardAsync(0, new byte[] { 4 }, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await guard.Keyboard.SendKeyboardAsync(HidModifiers.LeftGui, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await guard.Home.PressHomeAsync(CancellationToken.None));
        Assert.Empty(Touch.Frames);
        Assert.Empty(Keys.Reports);
        Assert.Equal(0, Host.HomeSink.Presses);

        await guard.Touch.SendAsync(new[] { Down.Lifted() }, CancellationToken.None);
        await guard.Touch.SendAsync(ReadOnlyMemory<TouchContact>.Empty, CancellationToken.None);
        await guard.Keyboard.SendKeyboardAsync(0, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
        Assert.Equal(2, Touch.Frames.Count);
        Assert.Single(Keys.Reports);
    }

    [Fact]
    public async Task WithoutALeaseNothingIsRefused()
    {
        var guard = new InputGuard(Host);

        await guard.Touch.SendAsync(new[] { Down }, CancellationToken.None);

        Assert.Single(Touch.Frames);
    }
}
