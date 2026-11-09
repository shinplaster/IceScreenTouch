// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Threading.Channels;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

// The wheel scroll: one finger follows the wheel smoothly, re-grabs at the margin, and lifts with or without momentum.
public class WheelScrollTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingSink _sink;
    private readonly FakeDesktop _desktop = new();
    private readonly FakeVideo _video = new();
    private readonly Channel<MouseInputEvent> _input = Channel.CreateUnbounded<MouseInputEvent>();
    private readonly MirrorBridge _bridge;

    public WheelScrollTests()
    {
        _sink = new RecordingSink(_time);
        _bridge = new MirrorBridge(new TouchGestureEngine(_sink, _time), _video, _desktop, _input.Reader, new BridgeOptions { FollowVideoSize = false }, _time);
    }

    private static RectD Portrait => Geometry.VideoRect(400, 600, new SizeD(498, 1080)).Offset(300, 150);

    private static PointD At(RectD r, double u, double v) => new(r.Left + (r.Width * u), r.Top + (r.Height * v));

    private TouchGestureEngine Engine => _bridge.Engine;

    // The expected positions below assume 10% per notch.
    private void Momentum(double m) => Engine.Wheel = WheelOptions.Default with { Momentum = m, StepPerNotch = 0.10 };

    private void Wheel(PointD at, int delta, bool horizontal = false)
    {
        _desktop.CursorPosition = at;
        _input.Writer.TryWrite(MouseInputEvent.Wheel(at, delta, horizontal));
    }

    private Task Tick() => _time.Drive(_bridge.TickAsync());

    /// <summary>Runs the bridge in 4 ms ticks for <paramref name="ms"/>, or until the scroll ends.</summary>
    private async Task Run(double ms = 3000, bool untilDone = true)
    {
        await Tick();
        for (double t = 0; t < ms && (!untilDone || Engine.IsScrolling); t += 4)
        {
            _time.Wait(4);
            await Tick();
        }
    }

    private List<Frame> Lifts => [.. _sink.Frames.Where(f => f.IsLift)];

    private static void Near(int expected, int actual, int tolerance = 6) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected} +/- {tolerance}, got {actual}");

    [Fact]
    public async Task WheelDownMovesTheFingerUpSmoothlyAndStopsWithoutMomentum()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.5), -120);

        await Run();

        var downs = _sink.Downs.ToList();
        Assert.Equal(new AxisPoint(5000, 5000), downs[0].First);
        var moving = downs.TakeWhile(f => f.First.Y > 4005).ToList();
        Assert.True(moving.Count >= 5, $"only {moving.Count} frames: not smooth");
        Assert.All(downs.Zip(downs.Skip(1)).Take(moving.Count), p => Assert.True(p.Second.First.Y <= p.First.First.Y));
        var lift = Assert.Single(Lifts);
        Near(4000, lift.First.Y);
        Assert.Equal(5000, lift.First.X);

        // It rests before lifting, wiggling by 4 units so that iOS sees the finger stop.
        var rest = downs.Where(f => f.At > lift.At - TimeSpan.FromMilliseconds(150)).ToList();
        Assert.True(rest.Count >= 6, $"{rest.Count} rest frames");
        Assert.Contains(rest, f => f.First.X == 5004);
        Assert.False(Engine.IsScrolling);
        Assert.Equal(1, Engine.Stats.Scrolls);
    }

    [Fact]
    public async Task WheelUpMovesTheFingerDown()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.5), 120);

        await Run();

        Near(6000, Assert.Single(Lifts).First.Y);
    }

    [Fact]
    public async Task MoreTurnsWhileScrollingExtendTheSameTouch()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.5), -120);
        await Run(60, untilDone: false);
        Wheel(At(Portrait, 0.5, 0.5), -120);
        await Run(60, untilDone: false);
        Wheel(At(Portrait, 0.5, 0.5), -120);

        await Run();

        Near(2000, Assert.Single(Lifts).First.Y);
        Assert.Equal(1, Engine.Stats.Scrolls);
    }

    [Fact]
    public async Task TheStepSettingScalesTheTravel()
    {
        Engine.Wheel = WheelOptions.Default with { Momentum = 0, StepPerNotch = 0.25 };
        Wheel(At(Portrait, 0.5, 0.5), -120);

        await Run();

        Near(2500, Assert.Single(Lifts).First.Y);
    }

    [Fact]
    public async Task AtTheMarginTheFingerIsPutDownAgainAtTheStartAndCarriesOn()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.5), -120 * 10); // 100% of the picture: more than fits between the margins

        await Run();

        // 0.5 -> 0.05 (lift), 0.5 -> 0.05 (lift), 0.5 -> 0.4: 0.45 + 0.45 + 0.1 = 1.0
        var lifts = Lifts;
        Assert.Equal(3, lifts.Count);
        Near(500, lifts[0].First.Y); // stopped at the margin (and rested) before lifting
        Near(500, lifts[1].First.Y);
        Near(4000, lifts[2].First.Y);
        Assert.Equal(3, _sink.Frames.Count(f => f.IsDown && f.First.Y == 5000)); // put down at the start each time
        Assert.All(_sink.Downs, f => Assert.InRange((int)f.First.Y, 500, 9510));
        Assert.Equal(1, Engine.Stats.Scrolls);
    }

    [Fact]
    public async Task WithMomentumTheFingerGlidesOnThenStillRestsBeforeLifting()
    {
        Momentum(1);
        Wheel(At(Portrait, 0.5, 0.5), -120); // 0.1 in the 200 ms window -> glide 0.1 / 200 x 400 = 0.2

        await Run();

        var lift = Assert.Single(Lifts);
        Near(2000, lift.First.Y, 10);
        var rest = _sink.Downs.Where(f => f.At > lift.At - TimeSpan.FromMilliseconds(150)).ToList();
        Assert.Contains(rest, f => f.First.X == 5004); // never lifted while moving
    }

    [Theory]
    [InlineData(0.0, 4000)]
    [InlineData(0.25, 3500)]
    [InlineData(0.5, 3000)]
    public async Task MomentumScalesTheGlide(double momentum, int expectedY)
    {
        Momentum(momentum);
        Wheel(At(Portrait, 0.5, 0.5), -120);

        await Run();

        Near(expectedY, Assert.Single(Lifts).First.Y, 10);
    }

    [Fact]
    public async Task AFastSpinGlidesFurtherThanOneNotch()
    {
        Momentum(0.5);
        for (var i = 0; i < 3; i++)
        {
            Wheel(At(Portrait, 0.5, 0.8), -120);
            await Run(40, untilDone: false);
        }

        await Run();

        // 0.3 from the wheel + 0.5 x (0.3 / 200 ms) x 400 ms = 0.3 of glide: 0.8 -> 0.2
        Near(2000, Assert.Single(Lifts).First.Y, 10);
    }

    [Fact]
    public async Task AClickEndsTheScrollAtOnce()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.5), -120 * 3);
        await Run(40, untilDone: false);
        Assert.True(Engine.IsScrolling);

        var p = At(Portrait, 0.2, 0.2);
        _desktop.CursorPosition = p;
        _input.Writer.TryWrite(MouseInputEvent.Press(MouseButton.Left, p));
        _input.Writer.TryWrite(MouseInputEvent.Release(MouseButton.Left, p));
        await Tick();

        Assert.False(Engine.IsScrolling);
        Assert.Equal(2, Lifts.Count); // the scroll's lift, then the tap's
        Assert.Equal(new AxisPoint(2000, 2000), Lifts[1].First);
    }

    [Fact]
    public async Task LosingTheWindowEndsTheScroll()
    {
        Wheel(At(Portrait, 0.5, 0.5), -120 * 3);
        await Run(40, untilDone: false);

        _video.Window = 0;
        await Tick();

        Assert.False(Engine.IsScrolling);
        Assert.Single(Lifts);
        Assert.True(_sink.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task ShiftWheelOrTiltMovesTheFingerSideways()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.5), 120, horizontal: true); // scroll right = the finger moves left

        await Run();

        var lift = Assert.Single(Lifts);
        Near(4000, lift.First.X);
        Assert.Equal(5000, lift.First.Y);
    }

    [Fact]
    public async Task ALandscapeScrollIsRotated()
    {
        Momentum(0);
        _video.VideoSize = new SizeD(1920, 886);
        var pic = Geometry.VideoRect(400, 600, new SizeD(1920, 886)).Offset(300, 150);

        Wheel(At(pic, 0.5, 0.5), -120);
        await Run();

        // Picture (0.5, 0.5) -> (0.5, 0.4); rotated by 90: (1 - v, u).
        Assert.Equal(new AxisPoint(5000, 5000), _sink.Downs.First().First);
        var lift = Assert.Single(Lifts);
        Near(6000, lift.First.X);
        Assert.Equal(5000, lift.First.Y);
    }

    [Fact]
    public async Task AScrollNearTheEdgeStartsInsideTheMargin()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.995), -120); // the home gesture area

        await Run();

        Assert.Equal(new AxisPoint(5000, 9500), _sink.Downs.First().First);
        Near(8500, Assert.Single(Lifts).First.Y);
    }

    [Fact]
    public async Task AScrollStartedOnTheMarginItHeadsForMovesFromTheMiddle()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.97), 120); // wheel up near the bottom: the finger has to go further down

        await Run();

        // First the finger rests on the margin it cannot leave; the next stroke starts from the middle and moves.
        Assert.Equal(new AxisPoint(5000, 9500), _sink.Downs.First().First);
        Assert.Equal(2, Lifts.Count);
        Assert.Equal(new AxisPoint(5000, 5000), _sink.Downs.First(d => d.First.Y < 9000).First);
        Near(6000, Lifts[^1].First.Y); // one notch further down, as from anywhere else
        Assert.False(Engine.IsScrolling);
    }

    [Fact]
    public async Task AScrollStartedJustBeforeTheMarginDoesNotCrawl()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.94), 5 * 120); // five notches up, 0.01 above the margin

        await Run();

        // One short stroke to the margin, then the rest from the middle: not dozens of 1 % strokes.
        Assert.InRange(Lifts.Count, 2, 3);
        Assert.False(Engine.IsScrolling);
    }

    [Fact]
    public async Task AHighResolutionWheelMovesProportionally()
    {
        Momentum(0);
        Wheel(At(Portrait, 0.5, 0.5), -30); // a quarter notch

        await Run();

        Near(4750, Assert.Single(Lifts).First.Y);
    }

    [Fact]
    public async Task HomeIsRefusedWhileScrolling()
    {
        Wheel(At(Portrait, 0.5, 0.5), -120);
        await Run(20, untilDone: false);

        Assert.False(await _time.Drive(Engine.HomeAsync(Portrait)));
    }

    [Fact]
    public void BadOptionsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine.Wheel = WheelOptions.Default with { StepPerNotch = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine.Wheel = WheelOptions.Default with { Momentum = -0.1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => Engine.Wheel = WheelOptions.Default with { FollowTime = TimeSpan.Zero });
    }
}
