// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

public class TouchOperationTests : ApiTestBase
{
    private static readonly ApiPoint Mid = new() { X = 0.5, Y = 0.5 };

    // ---- Limits and minimum hold ----

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(69)]
    public async Task ShortHoldsAreRaisedToSeventyMilliseconds(int holdMs)
    {
        var result = await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, HoldMs = holdMs }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Ms(70), Touch.Frames[1].At - Touch.Frames[0].At);
    }

    [Fact]
    public async Task AHoldOfSeventyOrMoreIsKept()
    {
        await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, HoldMs = 250 }));

        Assert.Equal(Ms(250), Touch.Frames[1].At - Touch.Frames[0].At);
    }

    [Fact]
    public async Task TapDefaultsToEightyMilliseconds()
    {
        await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 }));

        Assert.Equal(Ms(80), Touch.Frames[1].At - Touch.Frames[0].At);
    }

    [Fact]
    public async Task LongPressDefaultsToEightHundredMilliseconds()
    {
        var result = await Time.Drive(Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5 }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Ms(800), Touch.Frames[1].At - Touch.Frames[0].At);
        Assert.True(Touch.Frames[1].IsLift);
    }

    [Fact]
    public async Task LongPressBelowTheMinimumIsRaised()
    {
        await Time.Drive(Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5, HoldMs = 5 }));

        Assert.Equal(Ms(70), Touch.Frames[1].At - Touch.Frames[0].At);
    }

    [Fact]
    public async Task TapCountSendsDownLiftEmptyForEachTapWithTheInterval()
    {
        var result = await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.4, Y = 0.6, Space = "device", Count = 3, HoldMs = 80, IntervalMs = 120 }));

        Assert.True(result.IsSuccess);
        var kinds = Touch.Frames.Select(f => f.IsDown ? "down" : f.IsLift ? "lift" : "empty").ToList();
        Assert.Equal(["down", "lift", "empty", "down", "lift", "empty", "down", "lift", "empty"], kinds);
        Assert.Equal([0.0, 80, 80, 200, 280, 280, 400, 480, 480], Touch.Frames.Select(f => f.At.TotalMilliseconds));
        Assert.All(Touch.Frames.Where(f => !f.IsEmpty), f => Assert.Equal(new AxisPoint(4000, 6000), f.First));
    }

    [Fact]
    public async Task TapIntervalHasAFloorOfTheMinimumGap()
    {
        await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, Count = 2, HoldMs = 80, IntervalMs = 1 }));

        var downs = Touch.Downs.ToList();
        Assert.Equal(Ms(80 + 25), downs[1].At - downs[0].At);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    [InlineData(-1)]
    public async Task TapCountIsOneToTen(int count)
    {
        AssertError(await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, Count = count }), ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task TenTapsAreAllowed()
    {
        var result = await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, Count = 10 }));

        Assert.True(result.IsSuccess);
        Assert.Equal(10, Touch.Downs.Count());
    }

    [Fact]
    public async Task NoOperationMayTakeMoreThanSixtySeconds()
    {
        var cases = new List<ApiResult>
        {
            await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, HoldMs = 60_001 }),
            await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, HoldMs = 7_000, Count = 10 }),
            await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, HoldMs = 80, IntervalMs = 7_000, Count = 10 }),
            await Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5, HoldMs = 60_001 }),
            await Controller.SwipeAsync(new SwipeRequest { From = Mid, To = Mid, DurationMs = 60_001 }),
            await Controller.SwipeAsync(new SwipeRequest { From = Mid, To = Mid, DurationMs = 30_000, HoldBeforeMs = 20_000, HoldAfterMs = 10_001 }),
            await Controller.PinchAsync(new PinchRequest { Center = Mid, StartRadius = 0.1, EndRadius = 0.2, DurationMs = 60_001 }),
            await Controller.PressKeyAsync(new KeyPressRequest { Key = "a", HoldMs = 60_000 }),
            await Controller.PathAsync(new PathRequest { Points = [new PathPoint { X = 0.5, Y = 0.5, T = 0 }, new PathPoint { X = 0.6, Y = 0.5, T = 60_001 }] }),
        };

        foreach (var r in cases)
        {
            AssertError(r, ApiErrorCodes.InvalidRequest);
        }

        Assert.Empty(Touch.Frames);
        Assert.Empty(Keys.Reports);
        Assert.Empty(Host.Leases);
    }

    [Fact]
    public async Task ExactlySixtySecondsIsAllowed()
    {
        var result = await Time.Drive(Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5, HoldMs = 60_000 }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Ms(60_000), Touch.Frames[1].At - Touch.Frames[0].At);
    }

    [Fact]
    public async Task NegativeValuesAreInvalid()
    {
        var results = new[]
        {
            await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, HoldMs = -1 }),
            await Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, IntervalMs = -1 }),
            await Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5, HoldMs = -1 }),
            await Controller.SwipeAsync(new SwipeRequest { From = Mid, To = Mid, DurationMs = -1 }),
            await Controller.SwipeAsync(new SwipeRequest { From = Mid, To = Mid, HoldBeforeMs = -1 }),
            await Controller.SwipeAsync(new SwipeRequest { From = Mid, To = Mid, HoldAfterMs = -1 }),
            await Controller.PinchAsync(new PinchRequest { Center = Mid, StartRadius = -0.1, EndRadius = 0.1 }),
            await Controller.PinchAsync(new PinchRequest { Center = Mid, StartRadius = 0.1, EndRadius = 0.1, DurationMs = -5 }),
            await Controller.PressKeyAsync(new KeyPressRequest { Key = "a", HoldMs = -1 }),
        };

        Assert.All(results, r => AssertError(r, ApiErrorCodes.InvalidRequest));
        Assert.Empty(Touch.Frames);
    }

    // ---- Swipe ----

    [Fact]
    public async Task SwipeSendsAFrameEverySixteenMillisecondsAndLiftsAtTheEnd()
    {
        var result = await Time.Drive(Controller.SwipeAsync(new SwipeRequest
        {
            From = new ApiPoint { X = 0.5, Y = 0.8 },
            To = new ApiPoint { X = 0.5, Y = 0.2 },
        }));

        Assert.True(result.IsSuccess);
        var downs = Touch.Downs.ToList();
        var times = downs.Select(f => f.At.TotalMilliseconds).ToList();
        Assert.Equal(Enumerable.Range(0, 19).Select(i => i * 16.0).Append(300), times); // 0, 16 ... 288, 300
        Assert.Equal(new AxisPoint(5000, 8000), downs[0].First);
        Assert.Equal(new AxisPoint(5000, 2000), downs[^1].First);
        Assert.Equal((ushort)(8000 - Math.Round(6000 * 16 / 300.0)), downs[1].First.Y);
        Assert.True(downs.Zip(downs.Skip(1)).All(p => p.Second.First.Y <= p.First.First.Y)); // monotonic

        // The lift is at the last position (never (0, 0)), at the end time, then the empty report.
        Assert.Equal(downs.Count + 2, Touch.Frames.Count);
        Assert.True(Touch.Frames[^2].IsLift);
        Assert.Equal(new AxisPoint(5000, 2000), Touch.Frames[^2].First);
        Assert.Equal(Ms(300), Touch.Frames[^2].At);
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task SwipeHoldBeforeStaysAtFromAndHoldAfterStaysAtTo()
    {
        await Time.Drive(Controller.SwipeAsync(new SwipeRequest
        {
            From = new ApiPoint { X = 0.2, Y = 0.5 },
            To = new ApiPoint { X = 0.8, Y = 0.5 },
            Space = "device",
            DurationMs = 200,
            HoldBeforeMs = 100,
            HoldAfterMs = 50,
        }));

        var downs = Touch.Downs.ToList();
        var times = downs.Select(f => f.At.TotalMilliseconds).ToList();
        Assert.Equal(Enumerable.Range(0, 22).Select(i => i * 16.0).Append(350), times); // every 16 ms to 336, then 350
        foreach (var f in downs)
        {
            var t = f.At.TotalMilliseconds;
            if (t <= 100)
            {
                Assert.Equal(new AxisPoint(2000, 5000), f.First);
            }
            else if (t >= 300)
            {
                Assert.Equal(new AxisPoint(8000, 5000), f.First);
            }
            else
            {
                Assert.InRange(f.First.X, 2001, 7999);
            }
        }

        Assert.Equal(Ms(350), Touch.Frames[^2].At);
        Assert.Equal(new AxisPoint(8000, 5000), Touch.Frames[^2].First);
    }

    [Fact]
    public async Task SwipeWithNoDurationJumpsAndStillHoldsAtLeastSeventyMilliseconds()
    {
        var result = await Time.Drive(Controller.SwipeAsync(new SwipeRequest { From = Mid, To = new ApiPoint { X = 0.9, Y = 0.9 }, Space = "device", DurationMs = 0 }));

        Assert.True(result.IsSuccess);
        var downs = Touch.Downs.ToList();
        Assert.Equal(new AxisPoint(9000, 9000), downs[^1].First);
        Assert.Equal(Ms(70), Touch.Frames[^2].At);
        Assert.True(Touch.Frames[^2].IsLift);
    }

    [Fact]
    public async Task SwipeIsConvertedPerRotation()
    {
        Host.Picture = new PictureInfo(844, 390);
        Host.Landscape = Rotation.R270;

        await Time.Drive(Controller.SwipeAsync(new SwipeRequest { From = new ApiPoint { X = 0.1, Y = 0.5 }, To = new ApiPoint { X = 0.9, Y = 0.5 } }));

        var downs = Touch.Downs.ToList();
        Assert.Equal(Expected(0.1, 0.5, Rotation.R270), downs[0].First);
        Assert.Equal(Expected(0.9, 0.5, Rotation.R270), downs[^1].First);
    }

    // ---- Path ----

    [Fact]
    public async Task PathInterpolatesBetweenPointsAndEndsOnTheLastPoint()
    {
        var result = await Time.Drive(Controller.PathAsync(new PathRequest
        {
            Space = "device",
            Points =
            [
                new PathPoint { X = 0.1, Y = 0.1, T = 0 },
                new PathPoint { X = 0.5, Y = 0.5, T = 100 },
                new PathPoint { X = 0.9, Y = 0.1, T = 200 },
            ],
        }));

        Assert.True(result.IsSuccess);
        var downs = Touch.Downs.ToList();
        Assert.Equal(Enumerable.Range(0, 13).Select(i => i * 16.0).Append(200), downs.Select(f => f.At.TotalMilliseconds)); // 0 ... 192, 200
        Assert.Equal(new AxisPoint(1000, 1000), downs[0].First);
        Assert.Equal(new AxisPoint(2920, 2920), downs[3].First); // t = 48: 1000 + 0.48 * 4000
        Assert.Equal(new AxisPoint(9000, 1000), downs[^1].First);
        Assert.Equal(new AxisPoint(9000, 1000), Touch.Frames[^2].First); // lifted at the last point
        Assert.True(Touch.Frames[^2].IsLift);
        Assert.Equal(Ms(200), Touch.Frames[^2].At);
    }

    [Fact]
    public async Task PathWithALateFirstTimeWaitsAtTheFirstPoint()
    {
        await Time.Drive(Controller.PathAsync(new PathRequest
        {
            Space = "device",
            Points = [new PathPoint { X = 0.2, Y = 0.2, T = 100 }, new PathPoint { X = 0.4, Y = 0.2, T = 200 }],
        }));

        var downs = Touch.Downs.ToList();
        Assert.All(downs.Where(f => f.At <= Ms(100)), f => Assert.Equal(new AxisPoint(2000, 2000), f.First));
        Assert.Equal(new AxisPoint(4000, 2000), downs[^1].First);
        Assert.Equal(Ms(200), downs[^1].At);
    }

    [Fact]
    public async Task APathOfOnePointHoldsAtLeastSeventyMilliseconds()
    {
        var result = await Time.Drive(Controller.PathAsync(new PathRequest { Space = "device", Points = [new PathPoint { X = 0.3, Y = 0.3, T = 0 }] }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Ms(70), Touch.Frames[^2].At);
        Assert.Equal(new AxisPoint(3000, 3000), Touch.Frames[^2].First);
    }

    [Fact]
    public async Task PathTimesMayRepeat()
    {
        var result = await Time.Drive(Controller.PathAsync(new PathRequest
        {
            Space = "device",
            Points = [new PathPoint { X = 0.1, Y = 0.1, T = 0 }, new PathPoint { X = 0.9, Y = 0.9, T = 0 }, new PathPoint { X = 0.9, Y = 0.9, T = 50 }],
        }));

        Assert.True(result.IsSuccess);
        Assert.Equal(new AxisPoint(9000, 9000), Touch.Frames[^2].First);
    }

    [Fact]
    public async Task PathLimits()
    {
        var tooMany = Enumerable.Range(0, ApiController.MaxPathPoints + 1).Select(i => new PathPoint { X = 0.5, Y = 0.5, T = i }).ToList();
        var maxPoints = Enumerable.Range(0, ApiController.MaxPathPoints).Select(i => new PathPoint { X = 0.5, Y = 0.5, T = i }).ToList();

        AssertError(await Controller.PathAsync(new PathRequest { Points = tooMany }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PathAsync(new PathRequest { Points = [] }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PathAsync(new PathRequest { Points = [new PathPoint { X = 0.5, Y = 0.5, T = -1 }] }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PathAsync(new PathRequest { Points = [new PathPoint { X = 0.5, Y = 0.5, T = 10 }, new PathPoint { X = 0.5, Y = 0.5, T = 9 }] }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PathAsync(new PathRequest { Points = [new PathPoint { X = 1.5, Y = 0.5, T = 0 }] }), ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);

        Assert.True((await Time.Drive(Controller.PathAsync(new PathRequest { Space = "device", Points = maxPoints }))).IsSuccess);
        Assert.Equal(ApiController.MaxPathPoints, maxPoints.Count);
    }

    // ---- Pinch ----

    [Fact]
    public async Task PinchMovesTwoFingersAlongTheHorizontalAndLiftsBoth()
    {
        var result = await Time.Drive(Controller.PinchAsync(new PinchRequest { Center = Mid, StartRadius = 0.1, EndRadius = 0.4 }));

        Assert.True(result.IsSuccess);
        var downs = Touch.Downs.ToList();
        Assert.All(downs, f => Assert.Equal([(byte)0, (byte)1], f.Contacts.Select(c => c.Id)));
        Assert.Equal(Enumerable.Range(0, 25).Select(i => i * 16.0).Append(400), downs.Select(f => f.At.TotalMilliseconds)); // 0 ... 384, 400
        // Portrait picture, no rotation: x = 0.5 -/+ radius (the short side is the width), y = 0.5.
        Assert.Equal([new TouchContact(0, 4000, 5000), new TouchContact(1, 6000, 5000)], downs[0].Contacts);
        Assert.Equal([new TouchContact(0, 1000, 5000), new TouchContact(1, 9000, 5000)], downs[^1].Contacts);

        var lift = Touch.Frames[^2];
        Assert.True(lift.IsLift);
        Assert.Equal([new TouchContact(0, 1000, 5000, false), new TouchContact(1, 9000, 5000, false)], lift.Contacts); // last positions
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task PinchRadiusIsAFractionOfTheShortSideOfALandscapePicture()
    {
        Host.Picture = new PictureInfo(844, 390);
        Host.Landscape = Rotation.R90;
        var half = 0.2 * 390 / 844; // in screen x

        var result = await Time.Drive(Controller.PinchAsync(new PinchRequest { Center = Mid, StartRadius = 0.2, EndRadius = 0.2 }));

        Assert.True(result.IsSuccess);
        var expected = new[]
        {
            Geometry.UnitToAxis(Geometry.Rotate(new PointD(0.5 - half, 0.5), Rotation.R90)),
            Geometry.UnitToAxis(Geometry.Rotate(new PointD(0.5 + half, 0.5), Rotation.R90)),
        };
        var first = Touch.Downs.First();
        Assert.Equal(expected, first.Contacts.Select(c => new AxisPoint(c.X, c.Y)));
    }

    [Fact]
    public async Task PinchInPixelSpaceUsesTheAspectOfRef()
    {
        // The screenshot is landscape 2000 x 1000: a radius of 0.2 is 200 px, 0.1 of the width.
        Host.Picture = new PictureInfo(844, 390);
        Host.Landscape = Rotation.R270;
        var reference = new PixelRef { Width = 2000, Height = 1000 };

        var result = await Time.Drive(Controller.PinchAsync(new PinchRequest
        {
            Center = new ApiPoint { X = 1000, Y = 500 },
            Space = "pixel",
            Ref = reference,
            StartRadius = 0.2,
            EndRadius = 0.2,
        }));

        Assert.True(result.IsSuccess);
        var expected = new[]
        {
            Geometry.UnitToAxis(Geometry.Rotate(new PointD(0.4, 0.5), Rotation.R270)),
            Geometry.UnitToAxis(Geometry.Rotate(new PointD(0.6, 0.5), Rotation.R270)),
        };
        Assert.Equal(expected, Touch.Downs.First().Contacts.Select(c => new AxisPoint(c.X, c.Y)));
    }

    [Fact]
    public async Task PinchInDeviceSpaceTakesTheRadiusOfTheDigitizerXAxisWithOrWithoutAPicture()
    {
        Host.Picture = new PictureInfo(844, 390);
        Assert.True((await Time.Drive(Controller.PinchAsync(new PinchRequest { Center = Mid, Space = "device", StartRadius = 0.25, EndRadius = 0.25 }))).IsSuccess);
        Host.Picture = null;
        Assert.True((await Time.Drive(Controller.PinchAsync(new PinchRequest { Center = Mid, Space = "device", StartRadius = 0.25, EndRadius = 0.25 }))).IsSuccess);

        // Both pinches (a constant radius) put the fingers at x = 0.5 -/+ 0.25 on the digitizer: no picture needed or used.
        Assert.True(Touch.Downs.Count() > 2);
        Assert.All(Touch.Downs, f => Assert.Equal([new TouchContact(0, 2500, 5000), new TouchContact(1, 7500, 5000)], f.Contacts));
    }

    [Fact]
    public async Task PinchWithAFingerOffTheScreenIsInvalid()
    {
        // Portrait: center 0.9 with radius 0.2 puts the right finger at 1.1.
        AssertError(await Controller.PinchAsync(new PinchRequest { Center = new ApiPoint { X = 0.9, Y = 0.5 }, StartRadius = 0.05, EndRadius = 0.2 }), ApiErrorCodes.InvalidRequest);
        // The same radius is fine on the start side only if the end also fits: the check covers both ends.
        AssertError(await Controller.PinchAsync(new PinchRequest { Center = new ApiPoint { X = 0.5, Y = 0.5 }, StartRadius = 0.6, EndRadius = 0.1 }), ApiErrorCodes.InvalidRequest);
        // Landscape: the short side is only 390/844 of the width, so the same radius fits there.
        Host.Picture = new PictureInfo(844, 390);
        Assert.True((await Time.Drive(Controller.PinchAsync(new PinchRequest { Center = Mid, StartRadius = 0.1, EndRadius = 1.0 }))).IsSuccess);
        AssertError(await Controller.PinchAsync(new PinchRequest { Center = Mid, StartRadius = 0.1, EndRadius = 1.2 }), ApiErrorCodes.InvalidRequest);
        Assert.Equal(1, Touch.Downs.Count(f => f.At == TimeSpan.Zero)); // only the one that passed
    }

    [Fact]
    public async Task PinchRefusalsSendNothingAndKeepTheGateFree()
    {
        AssertError(await Controller.PinchAsync(new PinchRequest { Center = new ApiPoint { X = 0.95, Y = 0.5 }, StartRadius = 0.2, EndRadius = 0.2 }), ApiErrorCodes.InvalidRequest);

        Assert.Empty(Touch.Frames);
        Assert.Empty(Host.Leases);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task PinchWithRadiusZeroPutsBothFingersOnTheCenter()
    {
        await Time.Drive(Controller.PinchAsync(new PinchRequest { Center = Mid, Space = "device", StartRadius = 0, EndRadius = 0, DurationMs = 100 }));

        Assert.All(Touch.Downs, f => Assert.Equal([new TouchContact(0, 5000, 5000), new TouchContact(1, 5000, 5000)], f.Contacts));
    }

    // ---- Misc ----

    [Fact]
    public async Task NullPartsOfARequestAreInvalid()
    {
        AssertError(await Controller.SwipeAsync(new SwipeRequest { From = null!, To = Mid }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PathAsync(new PathRequest { Points = null! }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PathAsync(new PathRequest { Points = [null!] }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.PinchAsync(new PinchRequest { Center = null!, StartRadius = 0, EndRadius = 0 }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.TypeTextAsync(new TypeTextRequest { Text = null! }), ApiErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task TheLeaseIsHeldDuringAnOperationAndReturnedAfterTheLift()
    {
        await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5 }));

        Assert.Equal(["lease:acquired", "touch:down", "touch:lift", "touch:empty", "lease:disposed"], Host.Journal);
        Assert.True(Host.Lease!.Disposed);
    }
}
