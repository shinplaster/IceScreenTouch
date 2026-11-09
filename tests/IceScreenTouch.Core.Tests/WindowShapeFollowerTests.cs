// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

public class WindowShapeFollowerTests
{
    private const nint Window = 0x42;

    // A uxplay.exe started by hand: a 498x1080 window; after the phone turns, a 1920x886 picture is drawn in it as a
    // 498x230 band (as seen on the iPhone 15 Pro).
    private static readonly SizeD Tall = new(498, 1080);
    private static readonly RectD PortraitFull = new(0, 0, 498, 1080);
    private static readonly RectD LandscapeBand = new(0, 425, 498, 230);

    private readonly FakeTimeProvider _time = new();
    private readonly List<string> _log = [];
    private readonly WindowShapeFollower _follower;

    public WindowShapeFollowerTests()
    {
        _follower = new WindowShapeFollower(TimeSpan.FromSeconds(1), _time, _log.Add);
    }

    private void See(SizeD client, RectD picture, bool ambiguous = false, bool resizable = true, nint window = Window) =>
        _follower.Observe(window, client, picture, ambiguous, resizable);

    private void SeeFor(TimeSpan duration, SizeD client, RectD picture, bool ambiguous = false, bool resizable = true)
    {
        See(client, picture, ambiguous, resizable);
        for (var t = TimeSpan.Zero; t < duration; t += TimeSpan.FromMilliseconds(500))
        {
            _time.Advance(TimeSpan.FromMilliseconds(500));
            See(client, picture, ambiguous, resizable);
        }
    }

    [Fact]
    public void AMatchingPictureAsksForNothing()
    {
        SeeFor(TimeSpan.FromSeconds(3), Tall, PortraitFull);

        Assert.Null(_follower.Desired);
    }

    [Fact]
    public void ALandscapePictureInATallWindowAsksForAWideWindowAfterTheSettleTime()
    {
        See(Tall, PortraitFull);
        See(Tall, LandscapeBand);
        _time.Advance(TimeSpan.FromMilliseconds(500));
        See(Tall, LandscapeBand);
        Assert.Null(_follower.Desired); // not yet: a receiver that reshapes itself would be done by now

        _time.Advance(TimeSpan.FromMilliseconds(500));
        See(Tall, LandscapeBand);

        // The long side stays, the shape becomes the picture's.
        Assert.Equal(new SizeD(1080, 499), _follower.Desired);
        Assert.Contains(_log, l => l.Contains("reshaping the window to 1080x499", StringComparison.Ordinal));
    }

    [Fact]
    public void TurningBackAsksForATallWindowAgain()
    {
        SeeFor(TimeSpan.FromSeconds(1), Tall, LandscapeBand);
        var wide = new SizeD(1080, 498);
        SeeFor(TimeSpan.FromSeconds(1), wide, new RectD(0, 0, 1080, 498)); // the bridge resized it: no bars

        SeeFor(TimeSpan.FromSeconds(2), wide, new RectD(425, 0, 230, 498)); // the phone turned back (after the cooldown)

        Assert.Equal(new SizeD(499, 1080), _follower.Desired);
    }

    [Fact]
    public void AReceiverThatReshapesItsOwnWindowIsLeftAlone()
    {
        See(Tall, PortraitFull);
        See(Tall, LandscapeBand); // AirServer right after the rotation
        _time.Advance(TimeSpan.FromMilliseconds(300));
        SeeFor(TimeSpan.FromSeconds(3), new SizeD(1333, 615), new RectD(0, 0, 1333, 615)); // ... and after it reshaped

        Assert.Null(_follower.Desired);
        Assert.True(_follower.ReshapesItself);
    }

    [Fact]
    public void OnceTheReceiverReshapedItselfLaterDisagreementsAreLeftToIt()
    {
        // AirServer with an iPad: slower than the settle time once, after it resized its window itself.
        See(Tall, LandscapeBand);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        See(new SizeD(1180, 820), new RectD(0, 0, 1180, 820));

        SeeFor(TimeSpan.FromSeconds(5), new SizeD(1180, 820), new RectD(305, 0, 570, 820)); // slow to turn back

        Assert.Null(_follower.Desired);
    }

    [Fact]
    public void RightAfterAReshapeAFrameOfTheOldOrientationDoesNotFlipItBack()
    {
        // Seen with AirServer and an iPad: after the reshape, the old picture was drawn in the new shape for a while.
        SeeFor(TimeSpan.FromSeconds(1), Tall, LandscapeBand);
        var acted = _follower.Desired;
        var wide = new SizeD(1080, 499);
        SeeFor(TimeSpan.FromSeconds(1), wide, new RectD(425, 0, 230, 499)); // stale portrait frames within the cooldown

        Assert.Equal(acted, _follower.Desired);
    }

    [Fact]
    public void AfterTheCooldownATurnIsFollowedAgain()
    {
        SeeFor(TimeSpan.FromSeconds(1), Tall, LandscapeBand);
        var wide = new SizeD(1080, 499);

        SeeFor(TimeSpan.FromSeconds(3), wide, new RectD(425, 0, 230, 499)); // a real turn back

        Assert.Equal(new SizeD(498, 1080), _follower.Desired);
    }

    [Fact]
    public void AWindowTheUserReshapesByHandIsNotFoughtBack()
    {
        SeeFor(TimeSpan.FromSeconds(2), Tall, PortraitFull);

        // The user drags the window wide; the portrait picture is now letterboxed, but the phone did not turn.
        SeeFor(TimeSpan.FromSeconds(5), new SizeD(1600, 700), new RectD(639, 0, 323, 700));

        Assert.Null(_follower.Desired);
    }

    [Fact]
    public void AWindowFoundAlreadyLetterboxedIsReshapedOnce()
    {
        // The app started while the phone was already in landscape.
        SeeFor(TimeSpan.FromSeconds(5), Tall, LandscapeBand);

        Assert.Equal(new SizeD(1080, 499), _follower.Desired);
        Assert.Single(_log);
    }

    [Fact]
    public void MaximizedOrFullScreenWindowsAreNeverReshaped()
    {
        // AirServer full screen on a 3440x1440 monitor: a portrait picture with wide bars.
        SeeFor(TimeSpan.FromSeconds(3), new SizeD(3440, 1440), new RectD(1388, 0, 664, 1440), resizable: false);

        Assert.Null(_follower.Desired);
    }

    [Fact]
    public void ADarkScreenWhoseOrientationIsAGuessIsNotActedOn()
    {
        SeeFor(TimeSpan.FromSeconds(3), Tall, LandscapeBand, ambiguous: true);

        Assert.Null(_follower.Desired);
    }

    [Fact]
    public void AGuessInBetweenDoesNotRestartTheWaitAndMayEndIt()
    {
        // Seen on the iPhone: after turning back to portrait, dark frames (kept orientation) followed the first
        // clear one for seconds, and restarting the wait on each delayed the reshape by 11 s.
        See(Tall, LandscapeBand);
        Assert.True(_follower.Pending);
        _time.Advance(TimeSpan.FromMilliseconds(600));
        See(Tall, LandscapeBand, ambiguous: true);
        Assert.Null(_follower.Desired);

        _time.Advance(TimeSpan.FromMilliseconds(400));
        See(Tall, LandscapeBand, ambiguous: true);

        Assert.Equal(new SizeD(1080, 499), _follower.Desired);
        Assert.False(_follower.Pending);
    }

    [Fact]
    public void ADisagreementThatGoesAwayEndsTheWait()
    {
        See(Tall, LandscapeBand);
        _time.Advance(TimeSpan.FromMilliseconds(800));
        See(Tall, PortraitFull); // it was a glitch: portrait again
        _time.Advance(TimeSpan.FromMilliseconds(400));
        See(Tall, LandscapeBand); // a new wait starts here
        Assert.Null(_follower.Desired);

        _time.Advance(TimeSpan.FromSeconds(1));
        See(Tall, LandscapeBand);
        Assert.NotNull(_follower.Desired);
    }

    [Fact]
    public void TheDefaultSettleTimeIsShort()
    {
        var follower = new WindowShapeFollower(time: _time);
        follower.Observe(Window, Tall, LandscapeBand, ambiguous: false, resizable: true);
        _time.Advance(TimeSpan.FromMilliseconds(400));
        follower.Observe(Window, Tall, LandscapeBand, ambiguous: false, resizable: true);

        Assert.NotNull(follower.Desired);
    }

    [Fact]
    public void AnotherWindowIsDecidedAfresh()
    {
        SeeFor(TimeSpan.FromSeconds(1), Tall, LandscapeBand);
        var first = _follower.Desired;

        // uxplay recreated its window (a reconnect) in the old tall shape while the phone stays in landscape.
        See(Tall, LandscapeBand, window: 0x43);
        _time.Advance(TimeSpan.FromSeconds(1));
        See(Tall, LandscapeBand, window: 0x43);

        Assert.Equal(first, _follower.Desired);
        Assert.Equal(2, _log.Count);
    }

    [Theory]
    [InlineData(498, 1080, 0, 425, 498, 230, true)] // landscape band in a tall window
    [InlineData(1080, 498, 425, 0, 230, 498, true)] // portrait band in a wide window
    [InlineData(498, 1080, 0, 0, 498, 1080, false)] // fills the window
    [InlineData(1000, 950, 0, 0, 1000, 900, false)] // nearly square window, picture fills most of it
    public void DisagreesWhenTheOrientationsDifferAndThePictureIsSmall(double cw, double ch, double x, double y, double w, double h, bool expected) =>
        Assert.Equal(expected, WindowShapeFollower.Disagrees(new SizeD(cw, ch), new RectD(x, y, w, h)));
}
