using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

public class PictureTrackerTests
{
    private const nint Window = 0x42;
    private const double Aspect = PictureLocatorTests.Aspect;

    private readonly List<string> _log = [];
    private readonly PictureTracker _tracker;

    public PictureTrackerTests()
    {
        _tracker = new PictureTracker(Aspect, _log.Add);
    }

    private static TestImage Letterboxed(int w, int h, SizeD shape) => new TestImage(w, h).Busy(Geometry.VideoRect(w, h, shape));

    private PictureEstimate? Feed(TestImage image, nint window = Window) =>
        _tracker.Update(window, image.Pixels, image.Width, image.Height, image.Stride);

    // LonelyScreen 1.2 (2026-11-06): a 16 px gray toolbar across the bottom of the client area, under the picture
    // area; the picture is centered in the rest. A portrait picture in a wide window lost to the toolbar's rows.
    private static TestImage LonelyScreen(int w, int h, SizeD shape) =>
        new TestImage(w, h).Busy(Geometry.VideoRect(w, h - 16, shape)).Fill(new RectD(0, h - 16, w, 16), (70, 70, 70));

    [Fact]
    public void ABottomToolbarIsLeftOut()
    {
        var portrait = new SizeD(Aspect, 1);
        // Without the inset, only the toolbar's rows count as content, and the picture is taken as the whole height.
        Assert.Equal(552, Feed(LonelyScreen(1196, 552, portrait))!.Picture.Height);

        _tracker.BottomInset = 16;
        var found = Feed(LonelyScreen(1196, 552, portrait));
        Assert.Equal(PictureFit.Portrait, found!.Fit);
        var expected = Geometry.VideoRect(1196, 536, portrait);
        Assert.InRange(Math.Abs(found.Picture.Height - expected.Height), 0, 2);
        Assert.InRange(Math.Abs(found.Picture.Left - expected.Left), 0, 2);

        // A tall window: the picture ends above the toolbar, not at the bottom of the window.
        found = Feed(LonelyScreen(720, 1196, portrait));
        Assert.InRange(found!.Picture.Top + found.Picture.Height, 1178, 1181);

        // Guesses leave it out as well, and answers are for the whole client area.
        Assert.Equal(Geometry.VideoRect(1000, 584, portrait), _tracker.Locate(Window, new SizeD(1000, 600), out var fresh));
        Assert.False(fresh);
        Assert.True(_tracker.Locate(Window, new SizeD(720, 1196), out fresh).HasArea);
        Assert.True(fresh);
    }

    [Fact]
    public void UnevenMarginsAreMeasuredNotCentered()
    {
        // A landscape picture in a 1196x568 LonelyScreen window: 24 px left, 36 right, 16 above, 10 below (measured).
        var real = new RectD(24, 16, 1136, 526);
        var image = new TestImage(1196, 568).Busy(real).Fill(new RectD(0, 552, 1196, 16), (70, 70, 70));
        var tracker = new PictureTracker(Aspect, options: PictureLocatorOptions.Default with { TrustMeasuredShape = true }) { BottomInset = 16 };

        var found = tracker.Update(Window, image.Pixels, image.Width, image.Height, image.Stride);

        Assert.Equal(PictureFit.Measured, found!.Fit);
        Assert.Equal(real, found.Picture);
        Assert.Equal(PictureFit.Landscape, tracker.Fit);

        // Without the option the whole area is taken (the picture fills 95 % of it): 2 % too wide.
        _tracker.BottomInset = 16;
        var centered = Feed(image)!.Picture;
        Assert.Equal((0, 1196), (centered.Left, centered.Width));

        // A dark screen with a bright spot in the middle has no phone shape: the centered models answer.
        var dark = new TestImage(1196, 568).Busy(new RectD(500, 200, 200, 100)).Fill(new RectD(0, 552, 1196, 16), (70, 70, 70));
        Assert.NotEqual(PictureFit.Measured, tracker.Update(Window, dark.Pixels, dark.Width, dark.Height, dark.Stride)!.Fit);
    }

    [Fact]
    public void BeforeAnyCaptureAWindowShapedLikeThePhoneIsAllPicture()
    {
        var picture = _tracker.Locate(Window, new SizeD(615, 1333), out var fresh);

        Assert.False(fresh);
        Assert.Equal(new RectD(0, 0, 615, 1333), picture);
        Assert.Equal(new RectD(0, 0, 1333, 615), _tracker.Locate(Window, new SizeD(1333, 615), out _));
    }

    [Fact]
    public void BeforeAnyCaptureAnotherShapeHoldsACenteredPortraitPicture()
    {
        var picture = _tracker.Locate(Window, new SizeD(1720, 720), out var fresh);

        Assert.False(fresh);
        Assert.Equal(Geometry.VideoRect(1720, 720, new SizeD(Aspect, 1)), picture);
    }

    [Fact]
    public void ACaptureAnswersForTheSameWindowAndSize()
    {
        var e = Feed(Letterboxed(1720, 720, new SizeD(1, Aspect)));

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Landscape, _tracker.Fit);
        Assert.Equal(e.Picture, _tracker.Locate(Window, new SizeD(1720, 720), out var fresh));
        Assert.True(fresh);
        Assert.Single(_log, l => l.Contains("picture landscape", StringComparison.Ordinal));

        // Another size (resized): a guess in the last orientation until the next capture.
        var guess = _tracker.Locate(Window, new SizeD(1600, 900), out fresh);
        Assert.False(fresh);
        Assert.Equal(Geometry.VideoRect(1600, 900, new SizeD(1, Aspect)), guess);

        // Another window: a guess too.
        _tracker.Locate(0x99, new SizeD(1720, 720), out fresh);
        Assert.False(fresh);
    }

    [Fact]
    public void AnImageThatTellsNothingKeepsTheLastPicture()
    {
        var e = Feed(Letterboxed(1720, 720, new SizeD(Aspect, 1)));

        Assert.Null(Feed(new TestImage(1720, 720)));
        Assert.NotNull(e);
        Assert.Equal(e.Picture, _tracker.Locate(Window, new SizeD(1720, 720), out var fresh));
        Assert.True(fresh);
    }

    [Fact]
    public void TheSamePictureIsLoggedOnce()
    {
        Feed(Letterboxed(1720, 720, new SizeD(Aspect, 1)));
        Feed(Letterboxed(1720, 720, new SizeD(Aspect, 1)));
        Feed(Letterboxed(1720, 720, new SizeD(1, Aspect)));

        Assert.Equal(2, _log.Count(l => l.Contains("[video] picture", StringComparison.Ordinal)));
    }

    [Fact]
    public void ResetForgetsTheCaptures()
    {
        Feed(Letterboxed(1720, 720, new SizeD(Aspect, 1)));

        _tracker.Reset();

        _tracker.Locate(Window, new SizeD(1720, 720), out var fresh);
        Assert.False(fresh);
        Assert.Null(_tracker.LastPicture);
    }
}
