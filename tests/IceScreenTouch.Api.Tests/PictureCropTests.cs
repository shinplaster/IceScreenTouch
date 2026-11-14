// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>The rectangle a screenshot cuts out, and its size as the status shows it.</summary>
public class PictureCropTests
{
    private static readonly RectI Client = new(100, 50, 400, 800);

    [Fact]
    public void TheSizeIsRoundedSideBySideLikeTheStatus()
    {
        Assert.Equal(new PictureInfo(390, 844), PictureCrop.InfoOf(new RectD(0, 0, 390.4, 843.6)));
        Assert.Equal(new PictureInfo(2, 4), PictureCrop.InfoOf(new RectD(0, 0, 2.5, 3.5))); // halves go to even, as Math.Round
        Assert.Equal(new PictureInfo(1, 1), PictureCrop.InfoOf(new RectD(10, 10, 0.6, 1.4)));
    }

    [Theory]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(0, 0, 0.4, 100)] // rounds to nothing
    [InlineData(0, 0, 100, 0.49)]
    [InlineData(0, 0, double.NaN, 100)]
    [InlineData(0, 0, double.PositiveInfinity, 100)]
    [InlineData(0, 0, -5, 100)]
    public void NoAreaIsNoPicture(double left, double top, double width, double height)
    {
        Assert.Null(PictureCrop.InfoOf(new RectD(left, top, width, height)));
        Assert.Null(PictureCrop.ToClient(new RectD(left, top, width, height), Client));
    }

    [Fact]
    public void NoPictureRectangleIsNoPicture()
    {
        Assert.Null(PictureCrop.InfoOf(null));
    }

    [Fact]
    public void TheRectangleIsMovedByTheClientOrigin()
    {
        // The bridge's rectangle is in screen pixels: left 100 + 20, top 50 + 30 in the client area.
        var crop = PictureCrop.ToClient(new RectD(120, 80, 300, 600), Client);

        Assert.Equal(new RectI(20, 30, 300, 600), crop);
    }

    [Fact]
    public void ThePictureThatFillsTheClientAreaIsTheWholeClientArea()
    {
        Assert.Equal(new RectI(0, 0, 400, 800), PictureCrop.ToClient(new RectD(100, 50, 400, 800), Client));
    }

    [Fact]
    public void TheCropHasTheSizeTheStatusSays()
    {
        var picture = new RectD(147.4, 61.6, 290.4, 700.6);

        var crop = PictureCrop.ToClient(picture, Client)!.Value;

        var info = PictureCrop.InfoOf(picture)!;
        Assert.Equal((info.Width, info.Height), (crop.Width, crop.Height));
        Assert.Equal((47, 12), (crop.Left, crop.Top)); // 47.4 and 11.6 to the nearest pixel
    }

    [Theory]
    [InlineData(100, 50, 400.4, 800.4)] // sizes round down to the client size
    [InlineData(99.6, 49.6, 400, 800)] // start rounds to 0
    [InlineData(100.4, 50.4, 400, 800)]
    public void ARoundingOverhangOfAPixelIsMovedBackNotCut(double left, double top, double width, double height)
    {
        Assert.Equal(new RectI(0, 0, 400, 800), PictureCrop.ToClient(new RectD(left, top, width, height), Client));
    }

    [Fact]
    public void OnePixelOverTheRightEdgeAfterRoundingMovesLeft()
    {
        // 20.4 + 380.6 would be 401.0 wide: the start rounds to 20, the width to 381, so the end is 401 > 400.
        var crop = PictureCrop.ToClient(new RectD(120.4, 50, 380.6, 800), Client);

        Assert.Equal(new RectI(19, 0, 381, 800), crop);
    }

    [Fact]
    public void OnePixelBeforeTheLeftEdgeAfterRoundingMovesRight()
    {
        var crop = PictureCrop.ToClient(new RectD(99.4, 50, 300, 800), Client);

        Assert.Equal(new RectI(0, 0, 300, 800), crop);
    }

    [Fact]
    public void APictureReallyPartlyOutsideIsCutAtTheEdges()
    {
        // 60 pixels past the right edge and 100 above the top.
        var crop = PictureCrop.ToClient(new RectD(260, -50, 300, 600), Client);

        Assert.Equal(new RectI(160, 0, 240, 500), crop); // x 160..400, y 0..500 (the picture's top is 100 above the client's)
    }

    [Fact]
    public void APictureLargerThanTheClientAreaIsCutToIt()
    {
        var crop = PictureCrop.ToClient(new RectD(0, 0, 2000, 3000), Client);

        Assert.Equal(new RectI(0, 0, 400, 800), crop);
    }

    [Theory]
    [InlineData(500, 50)] // right of the client area
    [InlineData(-300, 50)] // left
    [InlineData(100, 850)] // below
    [InlineData(100, -200)] // above
    public void APictureOutsideTheClientAreaIsNoPicture(double left, double top)
    {
        Assert.Null(PictureCrop.ToClient(new RectD(left, top, 100, 100), Client));
    }

    [Fact]
    public void AnEmptyClientAreaIsNoPicture()
    {
        Assert.Null(PictureCrop.ToClient(new RectD(0, 0, 100, 100), new RectI(0, 0, 0, 800)));
        Assert.Null(PictureCrop.ToClient(new RectD(0, 0, 100, 100), new RectI(0, 0, 400, -1)));
    }

    [Fact]
    public void AMonitorLeftOfThePrimaryHasNegativeScreenCoordinates()
    {
        var client = new RectI(-1500, 200, 600, 1000);

        Assert.Equal(new RectI(10, 20, 500, 900), PictureCrop.ToClient(new RectD(-1490, 220, 500, 900), client));
    }

    [Fact]
    public void AbsurdCoordinatesDoNotOverflow()
    {
        Assert.Null(PictureCrop.ToClient(new RectD(1e300, 0, 100, 100), Client));
        Assert.Null(PictureCrop.ToClient(new RectD(-1e300, 0, 100, 100), Client));
        Assert.Null(PictureCrop.ToClient(new RectD(double.NaN, 0, 100, 100), Client));
        Assert.NotNull(PictureCrop.InfoOf(new RectD(0, 0, 1e300, 1e300))); // capped, not wrapped
    }
}
