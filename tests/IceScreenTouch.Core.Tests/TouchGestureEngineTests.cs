// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

// The logical part of poc/poc3-integrated/test_bridge.py, with a fake clock instead of real time and Win32 input.
public class TouchGestureEngineTests
{
    private static readonly RectD Portrait = new(100, 50, 200, 400);  // picture in screen px
    private static readonly RectD Landscape = new(100, 50, 400, 200);
    private static readonly PointD Center = new(200, 250);
    private static readonly PointD LetterboxBar = new(95, 250);       // inside the window, outside the picture
    private static readonly string[] TapFrames = ["down", "lift", "empty"];
    private static readonly byte[] PinchIds = [0, 1];

    private readonly FakeTimeProvider _time = new();
    private readonly RecordingSink _sink;
    private readonly TouchGestureEngine _engine;

    public TouchGestureEngineTests()
    {
        _sink = new RecordingSink(_time);
        _engine = new TouchGestureEngine(_sink, _time);
    }

    private static PointD At(RectD r, double u, double v) => new(r.Left + (r.Width * u), r.Top + (r.Height * v));

    private async Task Click(PointD p, RectD picture, bool pinch = false)
    {
        await _time.Drive(_engine.PressAsync(p, picture, pinch));
        await _time.Drive(_engine.ReleaseAsync(p, picture));
    }

    private async Task Drag(RectD picture, PointD[] path, double stepMs = 30, bool pinch = false)
    {
        await _time.Drive(_engine.PressAsync(path[0], picture, pinch));
        foreach (var p in path.Skip(1))
        {
            _time.Wait(stepMs);
            await _time.Drive(_engine.MoveAsync(p, picture));
        }

        await _time.Drive(_engine.ReleaseAsync(path[^1], picture));
    }

    [Fact]
    public async Task TapInTheCenterIsDownLiftEmpty()
    {
        await Click(Center, Portrait);

        Assert.Equal(3, _sink.Frames.Count);
        Assert.Equal([new TouchContact(0, 5000, 5000)], _sink.Frames[0].Contacts);
        Assert.Equal([new TouchContact(0, 5000, 5000, Tip: false)], _sink.Frames[1].Contacts);
        Assert.True(_sink.Frames[2].IsEmpty);
        Assert.False(_engine.IsDown);
        Assert.Equal(1, _engine.Stats.Gestures);
    }

    [Fact]
    public async Task AQuickClickIsHeldForTheMinimumHold()
    {
        await Click(Center, Portrait);
        Assert.Equal(TimeSpan.FromMilliseconds(70), _sink.Frames[1].At - _sink.Frames[0].At);
        Assert.Equal(TimeSpan.FromMilliseconds(70), _engine.Stats.LastHold);
    }

    [Fact]
    public async Task ALongPressIsNotExtended()
    {
        await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false));
        _time.Wait(500);
        await _time.Drive(_engine.ReleaseAsync(Center, Portrait));
        Assert.Equal(TimeSpan.FromMilliseconds(500), _sink.Frames[1].At - _sink.Frames[0].At);
    }

    [Fact]
    public async Task DragIsMonotonicFromQuarterToThreeQuartersAndEndsWithALift()
    {
        var p1 = At(Portrait, 0.25, 0.25);
        var p2 = At(Portrait, 0.75, 0.75);
        var path = Enumerable.Range(0, 9)
            .Select(i => new PointD(p1.X + ((p2.X - p1.X) * i / 8), p1.Y + ((p2.Y - p1.Y) * i / 8)))
            .ToArray();

        await Drag(Portrait, path);

        var downs = _sink.Downs.Select(f => f.First).ToList();
        Assert.Equal(9, downs.Count);
        Assert.All(downs.Zip(downs.Skip(1)), pair => Assert.True(pair.Second.X >= pair.First.X && pair.Second.Y >= pair.First.Y));
        Assert.Equal(new AxisPoint(2500, 2500), downs[0]);
        Assert.Equal(new AxisPoint(7500, 7500), downs[^1]);
        Assert.Equal(new AxisPoint(7500, 7500), _sink.Frames[^2].First); // lifted at the last position
        Assert.True(_sink.Frames[^2].IsLift && _sink.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task ClickInTheLetterboxBarIsIgnored()
    {
        Assert.Equal(PressResult.Ignored, await _time.Drive(_engine.PressAsync(LetterboxBar, Portrait, pinch: false)));
        await _time.Drive(_engine.ReleaseAsync(LetterboxBar, Portrait));
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task DragEnteringFromOutsideIsIgnored()
    {
        await Drag(Portrait, [LetterboxBar, Center]);
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task DragLeavingThePictureClampsToTheEdge()
    {
        await Drag(Portrait, [Center, new PointD(Portrait.Left + Portrait.Width + 60, Center.Y)]);
        Assert.Equal(10000, _sink.Downs.Last().First.X);
        Assert.True(_sink.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task PinchSendsTwoContactsMirroredThroughTheCenter()
    {
        await Drag(Portrait, [At(Portrait, 0.6, 0.5), At(Portrait, 0.8, 0.5)], pinch: true);

        var downs = _sink.Downs.ToList();
        Assert.NotEmpty(downs);
        Assert.All(downs, f =>
        {
            Assert.Equal(2, f.Contacts.Length);
            Assert.Equal(10000, f.Contacts[0].X + f.Contacts[1].X);
            Assert.Equal(10000, f.Contacts[0].Y + f.Contacts[1].Y);
            Assert.Equal(PinchIds, f.Contacts.Select(c => c.Id));
        });
        Assert.Equal(2, _sink.Frames[^2].Contacts.Length); // both fingers lifted
        Assert.True(_sink.Frames[^2].IsLift);
    }

    [Fact]
    public async Task FastClicksOnASlowLinkAreNeverLostAndEachIsHeld()
    {
        // The symptom fixed in PoC 3: quick repeated clicks while a BLE send is slow.
        _sink.SendDelay = TimeSpan.FromMilliseconds(60);
        const int n = 8;
        for (var i = 0; i < n; i++)
        {
            await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false));
            _time.Wait(12); // a 12 ms click
            await _time.Drive(_engine.ReleaseAsync(Center, Portrait));
            _time.Wait(25);
        }

        var kinds = _sink.Frames.Select(f => f.IsDown ? "down" : f.IsLift ? "lift" : "empty");
        Assert.Equal(Enumerable.Repeat(TapFrames, n).SelectMany(k => k), kinds);
        var holds = _sink.Frames.Chunk(3).Select(c => c[1].At - c[0].At);
        Assert.All(holds, h => Assert.True(h >= TimeSpan.FromMilliseconds(70), $"held {h.TotalMilliseconds} ms"));
        Assert.Equal(n, _engine.Stats.Gestures);
        Assert.Equal(TimeSpan.FromMilliseconds(120), _engine.Stats.MaxSend); // a lift is 2 reports of 60 ms
    }

    [Fact]
    public async Task ANewTouchWaitsForTheMinimumGapAfterALift()
    {
        await Click(Center, Portrait);
        await Click(Center, Portrait); // immediately again
        Assert.Equal(TimeSpan.FromMilliseconds(25), _sink.Frames[3].At - _sink.Frames[2].At);
    }

    [Fact]
    public async Task AFastSweepIsRateLimitedButEndsExactlyAtTheFinalPosition()
    {
        var start = At(Portrait, 0.2, 0.5);
        await _time.Drive(_engine.PressAsync(start, Portrait, pinch: false));
        for (var i = 1; i <= 60; i++) // 60 position changes in 120 ms
        {
            _time.Wait(2);
            await _time.Drive(_engine.MoveAsync(new PointD(start.X + (Portrait.Width * 0.6 * i / 60), start.Y), Portrait));
        }

        var final = new PointD(start.X + (Portrait.Width * 0.6), start.Y);
        await _time.Drive(_engine.ReleaseAsync(final, Portrait));

        var downs = _sink.Downs.ToList();
        Assert.InRange(downs.Count, 2, (120 / 16) + 2);
        Assert.Equal(new AxisPoint(8000, 5000), downs[^1].First);
        Assert.All(downs.Zip(downs.Skip(1)).Take(downs.Count - 2), p => Assert.True(p.Second.At - p.First.At >= TimeSpan.FromMilliseconds(16)));
    }

    [Fact]
    public async Task MoveWithoutChangeSendsNothing()
    {
        await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false));
        _time.Wait(50);
        await _time.Drive(_engine.MoveAsync(Center, Portrait));
        Assert.Single(_sink.Frames);
    }

    [Theory]
    [InlineData(Rotation.R90)]
    [InlineData(Rotation.R180)]
    [InlineData(Rotation.R270)]
    public async Task LandscapeClickIsRotatedIntoThePortraitFrame(Rotation rotation)
    {
        _engine.LandscapeRotation = rotation;
        await Click(At(Landscape, 0.75, 0.25), Landscape);

        // Worked out by hand from the formulas for the picture point (0.75, 0.25).
        var expected = rotation switch
        {
            Rotation.R90 => new AxisPoint(7500, 7500),  // (1-v, u)
            Rotation.R180 => new AxisPoint(2500, 7500), // (1-u, 1-v)
            _ => new AxisPoint(2500, 2500),             // (v, 1-u)
        };
        Assert.Equal(expected, _sink.Frames[0].First);
    }

    [Fact]
    public async Task LandscapeWithRotationZeroIsAPlainMapping()
    {
        _engine.LandscapeRotation = Rotation.R0;
        await Click(At(Landscape, 0.75, 0.25), Landscape);
        Assert.Equal(new AxisPoint(7500, 2500), _sink.Frames[0].First);
    }

    [Fact]
    public async Task PortraitPictureIgnoresTheLandscapeRotation()
    {
        _engine.LandscapeRotation = Rotation.R90;
        await Click(At(Portrait, 0.25, 0.75), Portrait);
        Assert.Equal(new AxisPoint(2500, 7500), _sink.Frames[0].First);
    }

    [Fact]
    public async Task RotationIsFixedForTheWholeGesture()
    {
        _engine.LandscapeRotation = Rotation.R90;
        await _time.Drive(_engine.PressAsync(At(Landscape, 0.5, 0.5), Landscape, pinch: false));
        Assert.Equal(Rotation.R90, _engine.ActiveRotation);
        _engine.ToggleLandscapeRotation(); // R pressed mid-drag
        _time.Wait(20);
        await _time.Drive(_engine.MoveAsync(At(Landscape, 0.75, 0.25), Landscape));
        Assert.Equal(new AxisPoint(7500, 7500), _sink.Frames[^1].First); // still rotated by 90
    }

    [Fact]
    public void RKeyTogglesBetweenTheTwoLandscapes()
    {
        Assert.Equal(Rotation.R90, _engine.LandscapeRotation);
        Assert.Equal(Rotation.R270, _engine.ToggleLandscapeRotation());
        Assert.Equal(Rotation.R90, _engine.ToggleLandscapeRotation());
        _engine.LandscapeRotation = Rotation.R180; // set by hand (console rot)
        Assert.Equal(Rotation.R90, _engine.ToggleLandscapeRotation());
        Assert.Throws<ArgumentOutOfRangeException>(() => _engine.LandscapeRotation = (Rotation)45);
    }

    [Fact]
    public async Task AnUpsideDownPortraitIsRotatedBy180()
    {
        Assert.Equal(Rotation.R0, _engine.PortraitRotation);
        Assert.Equal(Rotation.R180, _engine.TogglePortraitRotation());
        Assert.Equal(Rotation.R180, _engine.RotationFor(Portrait));
        Assert.Equal(Rotation.R90, _engine.RotationFor(Landscape)); // the landscape setting is separate
        Assert.Throws<ArgumentOutOfRangeException>(() => _engine.PortraitRotation = Rotation.R90);

        await Click(At(Portrait, 0.25, 0.75), Portrait);
        Assert.Equal(new AxisPoint(7500, 2500), _sink.Frames[0].First);
        Assert.Equal(Rotation.R0, _engine.TogglePortraitRotation());
    }

    [Fact]
    public async Task HomeSwipesUpFromTheBottomOfThePortraitPicture()
    {
        Assert.True(await _time.Drive(_engine.HomeAsync(Portrait)));
        var downs = _sink.Downs.ToList();
        Assert.Equal(new AxisPoint(5000, 9950), downs[0].First);
        Assert.Equal(new AxisPoint(5000, 6500), downs[^1].First);
        Assert.Equal(21, downs.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(220), downs[^1].At - downs[0].At); // 20 steps of 11 ms
        Assert.True(_sink.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task HomeInLandscapeFollowsTheRotatedBottomEdge()
    {
        _engine.LandscapeRotation = Rotation.R90;
        Assert.Equal((new AxisPoint(50, 5000), new AxisPoint(3500, 5000)), _engine.HomeSwipe(Landscape));
        Assert.True(await _time.Drive(_engine.HomeAsync(Landscape)));
        Assert.Equal(new AxisPoint(50, 5000), _sink.Frames[0].First);
    }

    private sealed class HomeButtonSink(FakeTimeProvider time) : ITouchSink, IHomeButtonSink
    {
        public RecordingSink Touches { get; } = new(time);

        public bool HasHomeButton { get; set; } = true;

        public int Presses { get; private set; }

        public bool IsSubscribed => Touches.IsSubscribed;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken) =>
            Touches.SendAsync(contacts, cancellationToken);

        public ValueTask PressHomeAsync(CancellationToken cancellationToken)
        {
            Presses++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task WithAHomeButtonHomePressesItInsteadOfSwiping()
    {
        // iPadOS 18 scrolled on the swipe; the consumer Menu key went home.
        var sink = new HomeButtonSink(_time);
        var engine = new TouchGestureEngine(sink, _time) { LandscapeRotation = Rotation.R90 };

        Assert.True(engine.HomeIsKey);
        Assert.True(await _time.Drive(engine.HomeAsync(Portrait)));
        Assert.True(await _time.Drive(engine.HomeAsync(Landscape)));

        Assert.Equal(2, sink.Presses);
        Assert.Empty(sink.Touches.Frames);

        sink.HasHomeButton = false; // an older HID map: the swipe again
        Assert.False(engine.HomeIsKey);
        Assert.True(await _time.Drive(engine.HomeAsync(Portrait)));
        Assert.NotEmpty(sink.Touches.Frames);
    }

    [Fact]
    public async Task HomeIsRefusedDuringATouchOrWithoutSubscription()
    {
        await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false));
        Assert.False(await _time.Drive(_engine.HomeAsync(Portrait)));
        await _time.Drive(_engine.ReleaseAsync(Center, Portrait));
        _sink.IsSubscribed = false;
        Assert.False(await _time.Drive(_engine.HomeAsync(Portrait)));
    }

    [Fact]
    public async Task PressWithoutSubscriptionIsCountedAsDropped()
    {
        _sink.IsSubscribed = false;
        Assert.Equal(PressResult.NotSubscribed, await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false)));
        Assert.Equal(1, _engine.Stats.Dropped);
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task PausedIgnoresPresses()
    {
        _engine.Paused = true;
        Assert.Equal(PressResult.Ignored, await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false)));
        Assert.Empty(_sink.Frames);
    }

    [Fact]
    public async Task SecondPressWhileDownIsIgnored()
    {
        await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false));
        Assert.Equal(PressResult.Ignored, await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false)));
        Assert.Single(_sink.Frames);
    }

    [Fact]
    public async Task AbortLiftsAtOnceWhenTheWindowDisappears()
    {
        await _time.Drive(_engine.PressAsync(Center, Portrait, pinch: false));
        await _time.Drive(_engine.AbortAsync());
        Assert.False(_engine.IsDown);
        Assert.Equal(TimeSpan.Zero, _sink.Frames[1].At - _sink.Frames[0].At); // no minimum hold
        Assert.True(_sink.Frames[1].IsLift && _sink.Frames[2].IsEmpty);
    }

    [Fact]
    public async Task AFailedLinkDoesNotLeaveTheEngineStuckDown()
    {
        var failing = new FailingSink();
        var engine = new TouchGestureEngine(failing, _time);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _time.Drive(engine.PressAsync(Center, Portrait, pinch: false)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _time.Drive(engine.ReleaseAsync(Center, Portrait)));
        Assert.False(engine.IsDown);
    }

    private sealed class FailingSink : ITouchSink
    {
        public bool IsSubscribed => true;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("link lost"));
    }
}
