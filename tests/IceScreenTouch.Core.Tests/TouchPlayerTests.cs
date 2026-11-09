// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

public class TouchPlayerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly RecordingSink _sink;
    private readonly TouchPlayer _player;

    public TouchPlayerTests()
    {
        _sink = new RecordingSink(_time);
        _player = new TouchPlayer(_sink, _time);
    }

    [Fact]
    public async Task TapIsDownHoldLiftAtTheSamePlaceThenEmpty()
    {
        await _time.Drive(_player.TapAsync(new AxisPoint(3000, 7000), TimeSpan.FromMilliseconds(120)));

        Assert.Equal(3, _sink.Frames.Count);
        Assert.Equal([new TouchContact(0, 3000, 7000)], _sink.Frames[0].Contacts);
        Assert.Equal([new TouchContact(0, 3000, 7000, Tip: false)], _sink.Frames[1].Contacts); // never (0, 0)
        Assert.True(_sink.Frames[2].IsEmpty);
        Assert.Equal(TimeSpan.FromMilliseconds(120), _sink.Frames[1].At - _sink.Frames[0].At);
        Assert.False(_player.IsDown);
    }

    [Fact]
    public async Task LiftWithoutFingersOnlySendsTheEmptyReport()
    {
        await _time.Drive(_player.LiftAsync());
        Assert.Single(_sink.Frames);
        Assert.True(_sink.Frames[0].IsEmpty);
    }

    [Fact]
    public async Task SwipeInterpolatesLikeThePoc()
    {
        await _time.Drive(_player.SwipeAsync(new AxisPoint(5000, 7500), new AxisPoint(5000, 2500), TimeSpan.FromMilliseconds(600)));

        var downs = _sink.Downs.ToList();
        Assert.Equal(21, downs.Count);
        Assert.Equal(new AxisPoint(5000, 7500), downs[0].First);
        Assert.Equal(new AxisPoint(5000, 7250), downs[1].First);
        Assert.Equal(new AxisPoint(5000, 2500), downs[^1].First);
        Assert.Equal(TimeSpan.FromMilliseconds(30), downs[1].At - downs[0].At);
        Assert.True(_sink.Frames[^2].IsLift);
        Assert.Equal(new AxisPoint(5000, 2500), _sink.Frames[^2].First); // lifted where it ended
        Assert.True(_sink.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task PinchMovesTwoFingersApartAndLiftsBoth()
    {
        await _time.Drive(_player.PinchAsync(new AxisPoint(5000, 5000), 800, 2800, TimeSpan.FromMilliseconds(600)));

        var downs = _sink.Downs.ToList();
        Assert.Equal([new TouchContact(0, 4200, 5000), new TouchContact(1, 5800, 5000)], downs[0].Contacts);
        Assert.Equal([new TouchContact(0, 2200, 5000), new TouchContact(1, 7800, 5000)], downs[^1].Contacts);
        Assert.Equal([new TouchContact(0, 2200, 5000, false), new TouchContact(1, 7800, 5000, false)], _sink.Frames[^2].Contacts);
    }

    [Fact]
    public async Task PinchBeyondTheEdgeIsClamped()
    {
        await _time.Drive(_player.PinchAsync(new AxisPoint(1000, 5000), 2000, 2000, TimeSpan.Zero, steps: 1));
        Assert.Equal([new TouchContact(0, 0, 5000), new TouchContact(1, 3000, 5000)], _sink.Frames[0].Contacts);
    }
}
