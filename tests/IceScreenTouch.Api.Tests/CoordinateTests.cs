// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

public class CoordinateTests : ApiTestBase
{
    private static readonly PictureInfo Portrait = new(390, 844);
    private static readonly PictureInfo Landscape = new(844, 390);

    public static TheoryData<bool, Rotation> Orientations => new()
    {
        { false, Rotation.R0 },
        { false, Rotation.R180 },
        { true, Rotation.R90 },
        { true, Rotation.R270 },
    };

    [Theory]
    [MemberData(nameof(Orientations))]
    public async Task ScreenSpaceIsUnRotatedByTheRotationOfTheOrientation(bool landscape, Rotation rotation)
    {
        Host.Picture = landscape ? Landscape : Portrait;
        if (landscape)
        {
            Host.Landscape = rotation;
            Host.Portrait = Rotation.R180; // the other orientation's setting must not matter
        }
        else
        {
            Host.Portrait = rotation;
            Host.Landscape = Rotation.R270;
        }

        var result = await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.25, Y = 0.75 }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Expected(0.25, 0.75, rotation), Touch.Downs.First().First);
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public async Task PixelSpaceIsTheScreenshotPixelsConvertedToScreen(bool landscape, Rotation rotation)
    {
        Host.Picture = landscape ? Landscape : Portrait;
        Host.Landscape = landscape ? rotation : Rotation.R90;
        Host.Portrait = landscape ? Rotation.R0 : rotation;
        var size = landscape ? new PixelRef { Width = 2000, Height = 1000 } : new PixelRef { Width = 1000, Height = 2000 };

        // 25 % across and 75 % down the screenshot, whatever its size.
        var result = await Time.Drive(Controller.TapAsync(new TapRequest { X = size.Width * 0.25, Y = size.Height * 0.75, Space = "pixel", Ref = size }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Expected(0.25, 0.75, rotation), Touch.Downs.First().First);
    }

    [Theory]
    [MemberData(nameof(Orientations))]
    public async Task DeviceSpaceIsNeverRotated(bool landscape, Rotation rotation)
    {
        Host.Picture = landscape ? Landscape : Portrait;
        Host.Landscape = Rotation.R270;
        Host.Portrait = rotation == Rotation.R0 ? Rotation.R180 : Rotation.R0;

        var result = await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.25, Y = 0.75, Space = "device" }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Geometry.UnitToAxis(new PointD(0.25, 0.75)), Touch.Downs.First().First);
    }

    [Fact]
    public async Task EdgesRoundLikeGeometry()
    {
        Assert.True((await Time.Drive(Controller.TapAsync(new TapRequest { X = 0, Y = 1, Space = "device" }))).IsSuccess);
        Assert.True((await Time.Drive(Controller.TapAsync(new TapRequest { X = 1, Y = 0, Space = "device" }))).IsSuccess);
        Assert.True((await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.00004, Y = 0.99996, Space = "device" }))).IsSuccess);

        var downs = Touch.Downs.Select(f => f.First).ToList();
        Assert.Equal(new AxisPoint(0, 10000), downs[0]);
        Assert.Equal(new AxisPoint(10000, 0), downs[1]);
        Assert.Equal(Geometry.UnitToAxis(new PointD(0.00004, 0.99996)), downs[2]);
    }

    [Fact]
    public async Task PixelEdgesAreInclusive()
    {
        var size = new PixelRef { Width = 1000, Height = 2000 };

        var result = await Time.Drive(Controller.TapAsync(new TapRequest { X = 1000, Y = 2000, Space = "pixel", Ref = size }));

        Assert.True(result.IsSuccess);
        Assert.Equal(new AxisPoint(10000, 10000), Touch.Downs.First().First);
    }

    [Theory]
    [InlineData(-0.001, 0.5, "screen")]
    [InlineData(1.001, 0.5, "screen")]
    [InlineData(0.5, -0.001, "device")]
    [InlineData(0.5, 1.001, "device")]
    [InlineData(double.NaN, 0.5, "screen")]
    [InlineData(0.5, double.PositiveInfinity, "screen")]
    [InlineData(0.5, 0.5, "world")]
    [InlineData(0.5, 0.5, "")]
    public async Task OutOfRangeAndUnknownSpacesAreInvalid(double x, double y, string space)
    {
        var result = await Controller.TapAsync(new TapRequest { X = x, Y = y, Space = space });

        AssertError(result, ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
        Assert.Empty(Host.Leases);
    }

    [Theory]
    [InlineData(1000.5, 10)]
    [InlineData(-1, 10)]
    [InlineData(10, 2000.5)]
    [InlineData(10, -0.5)]
    public async Task PixelOutOfTheScreenshotIsInvalid(double x, double y)
    {
        var size = new PixelRef { Width = 1000, Height = 2000 };

        var result = await Controller.TapAsync(new TapRequest { X = x, Y = y, Space = "pixel", Ref = size });

        AssertError(result, ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
    }


    [Theory]
    [InlineData(false, 2000, 1000)] // landscape screenshot, portrait picture
    [InlineData(true, 1000, 2000)] // portrait screenshot, landscape picture
    public async Task APixelRefOfTheOtherOrientationIsStale(bool pictureLandscape, int width, int height)
    {
        Host.Picture = pictureLandscape ? Landscape : Portrait;
        var stale = new PixelRef { Width = width, Height = height };

        var result = await Controller.TapAsync(new TapRequest { X = 10, Y = 10, Space = "pixel", Ref = stale });

        AssertError(result, ApiErrorCodes.PictureChanged);
        Assert.Contains("screenshot", result.Error!.Detail, StringComparison.Ordinal);
        Assert.Empty(Touch.Frames);
        Assert.Empty(Host.Leases);
        Assert.False(Controller.IsOperating);
        AssertError(await Controller.PinchAsync(new PinchRequest { Center = new ApiPoint { X = 500, Y = 500 }, Space = "pixel", Ref = stale, StartRadius = 0, EndRadius = 0 }), ApiErrorCodes.PictureChanged);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASquarePixelRefIsNeverStale(bool pictureLandscape)
    {
        Host.Picture = pictureLandscape ? Landscape : Portrait;

        var result = await Time.Drive(Controller.TapAsync(new TapRequest { X = 50, Y = 50, Space = "pixel", Ref = new PixelRef { Width = 100, Height = 100 } }));

        Assert.True(result.IsSuccess);
        Assert.Equal(Expected(0.5, 0.5, pictureLandscape ? Rotation.R90 : Rotation.R0), Touch.Downs.First().First);
    }

    [Fact]
    public async Task ASessionRefusesAStalePixelRefToo()
    {
        await using var session = (await Controller.TryOpenSessionAsync()).Value!;
        Host.Picture = Landscape;

        AssertError(session.ResolveFrame("pixel", new PixelRef { Width = 1000, Height = 2000 }), ApiErrorCodes.PictureChanged);
        Assert.True(session.ResolveFrame("pixel", new PixelRef { Width = 2000, Height = 1000 }).IsSuccess);
        Assert.True(session.ResolveFrame("pixel", new PixelRef { Width = 1000, Height = 1000 }).IsSuccess);
    }
    [Fact]
    public async Task PixelNeedsAValidRef()
    {
        AssertError(await Controller.TapAsync(new TapRequest { X = 5, Y = 5, Space = "pixel" }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.TapAsync(new TapRequest { X = 5, Y = 5, Space = "pixel", Ref = new PixelRef { Width = 0, Height = 10 } }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.TapAsync(new TapRequest { X = 5, Y = 5, Space = "pixel", Ref = new PixelRef { Width = 10, Height = -1 } }), ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task SpaceNamesAreCaseInsensitive()
    {
        Assert.True((await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, Space = "Device" }))).IsSuccess);
        Assert.True((await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.5, Y = 0.5, Space = "SCREEN" }))).IsSuccess);
    }

    [Fact]
    public async Task ASquarePictureCountsAsPortrait()
    {
        Host.Picture = new PictureInfo(500, 500);
        Host.Portrait = Rotation.R180;
        Host.Landscape = Rotation.R90;

        await Time.Drive(Controller.TapAsync(new TapRequest { X = 0.2, Y = 0.3 }));

        Assert.Equal(Expected(0.2, 0.3, Rotation.R180), Touch.Downs.First().First);
    }

    [Fact]
    public void PictureInfoDescribesOrientationAndAspect()
    {
        Assert.Equal("portrait", Portrait.Orientation);
        Assert.Equal("landscape", Landscape.Orientation);
        Assert.Equal(390.0 / 844.0, Portrait.Aspect, 9);
    }
}
