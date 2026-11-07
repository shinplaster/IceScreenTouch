using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

// Same checks as poc/poc3-integrated/test_geometry.py.
public class GeometryTests
{
    private static readonly SizeD Portrait = new(498, 1080);
    private static readonly RectD Picture = new(100, 50, 200, 400);

    private static void Near(double expected, double actual, double tolerance = 0.51) =>
        Assert.InRange(actual, expected - tolerance, expected + tolerance);

    [Fact]
    public void LetterboxHeightLimitedIsCenteredHorizontally()
    {
        var r = Geometry.VideoRect(400, 600, Portrait);
        Near(600, r.Height);
        Near(498 * 600.0 / 1080, r.Width);
        Near(0, r.Top);
        Near((400 - r.Width) / 2, r.Left);
    }

    [Fact]
    public void LetterboxInAWideWindow()
    {
        var r = Geometry.VideoRect(1920, 1080, Portrait);
        Near(1080, r.Height);
        Near(498, r.Width);
        Near((1920 - 498) / 2.0, r.Left);
    }

    [Fact]
    public void LetterboxInATallWindowIsCenteredVertically()
    {
        var r = Geometry.VideoRect(300, 1000, Portrait);
        Near(300, r.Width);
        Near(0, r.Left);
        Near((1000 - r.Height) / 2, r.Top);
    }

    [Fact]
    public void LetterboxOfAnEmptyAreaIsEmpty()
    {
        Assert.Equal(RectD.Empty, Geometry.VideoRect(0, 600, Portrait));
        Assert.Equal(RectD.Empty, Geometry.VideoRect(400, 600, new SizeD(0, 1080)));
    }

    [Theory]
    [InlineData(100, 50, 0, 0)]
    [InlineData(300, 450, 10000, 10000)]
    [InlineData(200, 250, 5000, 5000)]
    [InlineData(-999, 9999, 0, 10000)] // clamped
    public void ScreenToAxisWithoutRotation(double x, double y, int ax, int ay)
    {
        Assert.Equal(new AxisPoint((ushort)ax, (ushort)ay), Geometry.ScreenToAxis(new PointD(x, y), Picture, Rotation.R0));
    }

    [Fact]
    public void ContainsIsHalfOpen()
    {
        Assert.True(Picture.Contains(new PointD(100, 50)));
        Assert.False(Picture.Contains(new PointD(300, 450)));
        Assert.False(Picture.Contains(new PointD(99, 100)));
        Assert.False(RectD.Empty.Contains(default));
    }

    [Fact]
    public void MirrorPointGoesThroughTheCenter()
    {
        Assert.Equal(new AxisPoint(7500, 3000), Geometry.MirrorPoint(new AxisPoint(2500, 7000)));
    }

    [Fact]
    public void ToUnitClamps()
    {
        Assert.Equal(new PointD(1, 1), Geometry.ToUnit(new PointD(300, 450), Picture));
        Assert.Equal(new PointD(0, 1), Geometry.ToUnit(new PointD(-5, 9999), Picture));
    }

    [Fact]
    public void UnitToAxisRoundsAndClamps()
    {
        Assert.Equal(new AxisPoint(5000, 10000), Geometry.UnitToAxis(new PointD(0.5, 1.2)));
        Assert.Equal(new AxisPoint(0, 0), Geometry.UnitToAxis(new PointD(-0.1, 0)));
    }

    // Expected values printed by Python's round(u * 10000) (half to even), as the PoC computes them.
    [Theory]
    [InlineData(0.00005, 0)]     // 0.5    -> 0
    [InlineData(0.00015, 1)]     // 1.4999 -> 1
    [InlineData(0.00025, 2)]     // 2.5    -> 2
    [InlineData(0.12345, 1234)]  // 1234.5 -> 1234
    [InlineData(0.33335, 3334)]  // 3333.5 -> 3334
    [InlineData(0.99995, 10000)] // 9999.5 -> 10000
    public void UnitToAxisRoundsLikeThePoc(double unit, int axis)
    {
        Assert.Equal(new AxisPoint((ushort)axis, (ushort)axis), Geometry.UnitToAxis(new PointD(unit, unit)));
    }

    [Theory]
    [InlineData(Rotation.R0)]
    [InlineData(Rotation.R90)]
    [InlineData(Rotation.R180)]
    [InlineData(Rotation.R270)]
    public void EveryRotationPermutesTheCorners(Rotation rotation)
    {
        PointD[] corners = [new(0, 0), new(1, 0), new(0, 1), new(1, 1)];
        var rotated = corners.Select(c => Geometry.Rotate(c, rotation)).ToHashSet();
        Assert.True(rotated.SetEquals(corners));
    }

    [Fact]
    public void RotationFormulas()
    {
        // Verified on a real iPhone: 90 for the landscape a game starts in, 270 for the opposite one.
        Assert.Equal(new PointD(0.25, 0.75), Geometry.Rotate(new PointD(0.25, 0.75), Rotation.R0));
        Assert.Equal(new PointD(0.75, 0.75), Geometry.Rotate(new PointD(0.75, 0.25), Rotation.R90));  // (1-v, u)
        Assert.Equal(new PointD(0.25, 0.75), Geometry.Rotate(new PointD(0.75, 0.25), Rotation.R180)); // (1-u, 1-v)
        Assert.Equal(new PointD(0.25, 0.25), Geometry.Rotate(new PointD(0.75, 0.25), Rotation.R270)); // (v, 1-u)
    }

    [Theory]
    [InlineData(0.2, 0.9)]
    [InlineData(0.6, 0.1)]
    public void RotationsCompose(double u, double v)
    {
        var p = new PointD(u, v);
        var undone = Geometry.Rotate(Geometry.Rotate(p, Rotation.R90), Rotation.R270);
        Near(u, undone.X, 1e-9);
        Near(v, undone.Y, 1e-9);
        var twice = Geometry.Rotate(Geometry.Rotate(p, Rotation.R90), Rotation.R90);
        var half = Geometry.Rotate(p, Rotation.R180);
        Near(half.X, twice.X, 1e-9);
        Near(half.Y, twice.Y, 1e-9);
    }

    [Fact]
    public void UnsupportedRotationThrows()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Geometry.Rotate(default, (Rotation)45));
        Assert.Throws<ArgumentOutOfRangeException>(() => Geometry.NextRotation((Rotation)45));
    }

    [Fact]
    public void NextRotationTriesTheLandscapesFirst()
    {
        Assert.Equal(
            [Rotation.R270, Rotation.R0, Rotation.R180, Rotation.R90],
            new[] { Rotation.R90, Rotation.R270, Rotation.R0, Rotation.R180 }.Select(Geometry.NextRotation));
    }

    [Fact]
    public void FitClientSizeKeepsTheNativeSizeWhenItFits()
    {
        Assert.Equal((498, 1080), Geometry.FitClientSize(Portrait, 3440, 1400, 16, 39));
        Assert.Equal((1920, 886), Geometry.FitClientSize(new SizeD(1920, 886), 3440, 1400, 16, 39));
    }

    [Fact]
    public void FitClientSizeScalesDownOnASmallMonitor()
    {
        var (w, h) = Geometry.FitClientSize(new SizeD(1920, 1080), 1280, 720, 16, 39);
        Assert.True(w + 16 <= (1280 * 0.9) + 1);
        Assert.True(h + 39 <= (720 * 0.9) + 1);
        Near(1920.0 / 1080, (double)w / h, 0.01);
    }

    [Fact]
    public void FitClientSizeRejectsAnEmptyPicture()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Geometry.FitClientSize(new SizeD(0, 10), 100, 100));
    }

    [Fact]
    public void ClampPositionMovesTheWindowBackInside()
    {
        var work = new RectI(0, 0, 3440, 1400);
        Assert.Equal((2840, 200), Geometry.ClampPosition(3000, 200, 600, 500, work));
        Assert.Equal((0, 900), Geometry.ClampPosition(-50, 1300, 400, 500, work));
        Assert.Equal((100, 100), Geometry.ClampPosition(100, 100, 400, 500, work));
    }

    [Fact]
    public void ClampPositionOnASecondMonitorWithNegativeOrigin()
    {
        var work = new RectI(-1920, 0, 1920, 1040);
        Assert.Equal((-1920, 540), Geometry.ClampPosition(-2500, 600, 400, 500, work));
    }
}
