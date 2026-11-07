using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

/// <summary>A BGRA image to draw receiver windows with (black bars, a busy picture, overlays).</summary>
internal sealed class TestImage
{
    public TestImage(int width, int height, (byte R, byte G, byte B)? fill = null)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
        Fill(new RectD(0, 0, width, height), fill ?? (0, 0, 0));
    }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public byte[] Pixels { get; }

    public TestImage Fill(RectD r, (byte R, byte G, byte B) c)
    {
        ForEach(r, (x, y) => Set(x, y, c));
        return this;
    }

    /// <summary>A home-screen-like picture: colorful tiles everywhere, nothing near the background color.</summary>
    public TestImage Busy(RectD r)
    {
        ForEach(r, (x, y) => Set(x, y, ((byte)(100 + ((x * 7) % 150)), (byte)(80 + ((y * 5) % 170)), (byte)(120 + (((x + y) * 3) % 130)))));
        return this;
    }

    public PictureEstimate? Locate(double aspect = PictureLocatorTests.Aspect, PictureFit? previous = null) =>
        PictureLocator.Locate(Pixels, Width, Height, Stride, aspect, previous);

    private void ForEach(RectD r, Action<int, int> action)
    {
        var x0 = Math.Max(0, (int)Math.Round(r.Left));
        var y0 = Math.Max(0, (int)Math.Round(r.Top));
        var x1 = Math.Min(Width, (int)Math.Round(r.Left + r.Width));
        var y1 = Math.Min(Height, (int)Math.Round(r.Top + r.Height));
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                action(x, y);
            }
        }
    }

    private void Set(int x, int y, (byte R, byte G, byte B) c)
    {
        var i = (y * Stride) + (x * 4);
        (Pixels[i], Pixels[i + 1], Pixels[i + 2], Pixels[i + 3]) = (c.B, c.G, c.R, 255);
    }
}

// Shapes seen with AirServer 5.7 (2026-11-05): full screen 3440x1440 letterboxes the picture with black bars and
// draws a gray "TRIAL" badge in the bottom-right corner; a window is shaped like the picture.
public class PictureLocatorTests
{
    public const double Aspect = 393.0 / 852;

    private static readonly (byte, byte, byte) White = (255, 255, 255);
    private static readonly (byte, byte, byte) BadgeGray = (60, 60, 60);

    private static RectD Portrait(int w, int h) => Geometry.VideoRect(w, h, new SizeD(Aspect, 1));

    private static RectD Landscape(int w, int h) => Geometry.VideoRect(w, h, new SizeD(1, Aspect));

    private static void AssertNear(RectD expected, RectD actual, double tolerance = 1.5)
    {
        Assert.InRange(actual.Left, expected.Left - tolerance, expected.Left + tolerance);
        Assert.InRange(actual.Top, expected.Top - tolerance, expected.Top + tolerance);
        Assert.InRange(actual.Width, expected.Width - tolerance, expected.Width + tolerance);
        Assert.InRange(actual.Height, expected.Height - tolerance, expected.Height + tolerance);
    }

    // The receiver's badge: a gray disc (square here) with a white icon, in a bar or over the picture's corner.
    private static TestImage Badge(TestImage image, double left, double top) =>
        image.Fill(new RectD(left, top, 70, 70), BadgeGray).Fill(new RectD(left + 20, top + 15, 30, 25), White);

    [Fact]
    public void APortraitPictureBetweenBlackBarsIsFound()
    {
        var picture = Portrait(1720, 720);
        var e = new TestImage(1720, 720).Busy(picture).Locate();

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Portrait, e.Fit);
        Assert.False(e.Ambiguous);
        AssertNear(picture, e.Picture, 0.01);
        AssertNear(picture, new RectD(e.Measured.Left, e.Measured.Top, e.Measured.Width, e.Measured.Height));
    }

    [Fact]
    public void ATrialBadgeInTheBarIsIgnored()
    {
        var picture = Portrait(1720, 720);
        var image = Badge(new TestImage(1720, 720).Busy(picture), 1600, 620);

        var e = image.Locate();

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Portrait, e.Fit);
        AssertNear(picture, e.Picture, 0.01);
        AssertNear(picture, new RectD(e.Measured.Left, e.Measured.Top, e.Measured.Width, e.Measured.Height));
    }

    [Fact]
    public void ALandscapePictureWithTheBadgeOverItsCornerIsFound()
    {
        var picture = Landscape(1720, 720); // bars of about 80 px left and right
        var image = Badge(new TestImage(1720, 720).Busy(picture), picture.Left + picture.Width - 40, 620);

        var e = image.Locate(previous: PictureFit.Portrait);

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Landscape, e.Fit);
        AssertNear(picture, e.Picture, 0.01);
    }

    [Fact]
    public void AWhiteHeaderOnTopOfALandscapePictureIsNotTakenForTheBackground()
    {
        var picture = Landscape(1720, 720);
        var image = new TestImage(1720, 720).Busy(picture).Fill(picture with { Height = 60 }, White);

        var e = image.Locate();

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Landscape, e.Fit);
        AssertNear(picture, e.Picture, 0.01);
    }

    [Fact]
    public void AWindowShapedLikeThePictureIsAllPicture()
    {
        var e = new TestImage(615, 1333).Busy(new RectD(0, 0, 615, 1333)).Locate();

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Portrait, e.Fit);
        AssertNear(new RectD(0, 0, 615, 1333), e.Picture);
    }

    [Fact]
    public void ADarkScreenKeepsThePreviousOrientation()
    {
        // Dark mode: black everywhere except a small element in the middle. Both orientations explain it.
        var image = new TestImage(1720, 720).Fill(new RectD(800, 300, 120, 120), White);

        var fresh = image.Locate();
        var afterLandscape = image.Locate(previous: PictureFit.Landscape);

        Assert.NotNull(fresh);
        Assert.True(fresh.Ambiguous);
        Assert.Equal(PictureFit.Portrait, fresh.Fit);
        Assert.NotNull(afterLandscape);
        Assert.Equal(PictureFit.Landscape, afterLandscape.Fit);
        AssertNear(Landscape(1720, 720), afterLandscape.Picture, 0.01);
    }

    [Fact]
    public void APortraitPictureAfterLandscapeIsPortraitAgain()
    {
        var picture = Portrait(1720, 720);

        var e = new TestImage(1720, 720).Busy(picture).Locate(previous: PictureFit.Landscape);

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Portrait, e.Fit);
        Assert.False(e.Ambiguous);
    }

    [Fact]
    public void APictureThatIsNotCenteredIsMeasured()
    {
        // A receiver that puts the picture under its own (black) toolbar, left-aligned.
        var picture = new RectD(20, 120, 300, 650);
        var e = new TestImage(800, 800).Busy(picture).Locate();

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Measured, e.Fit);
        AssertNear(picture, e.Picture);
    }

    [Fact]
    public void A16By9VideoPlayedInLandscapeIsALandscapePicture()
    {
        // The phone pillarboxes the video with black of its own: only the 16:9 part differs from the bars.
        var picture = Landscape(1720, 720);
        var video = Geometry.VideoRect(picture.Width, picture.Height, new SizeD(16, 9)).Offset(picture.Left, picture.Top);

        var e = new TestImage(1720, 720).Busy(video).Locate();

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Landscape, e.Fit);
        AssertNear(picture, e.Picture, 0.01);
    }

    // A uxplay.exe started by hand (iPhone 15 Pro, 2026-11-05): the 498x1080 window keeps its shape and draws a
    // landscape picture as a 498x230 band between exact black bars.
    private static readonly RectD TallBand = Landscape(498, 1080);

    [Fact]
    public void ADarkModeLandscapePictureInATallWindowIsLandscape()
    {
        // A dark mode calendar: dark gray (about 30) everywhere, a few colored items not touching the sides.
        // With the usual tolerance (40) the gray looked like bar, and the result kept the previous orientation.
        var image = new TestImage(498, 1080).Fill(TallBand, (28, 28, 30))
            .Fill(new RectD(60, 470, 40, 20), (200, 80, 60)).Fill(new RectD(300, 560, 50, 20), (60, 160, 90));

        var e = image.Locate(previous: PictureFit.Portrait);

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Landscape, e.Fit);
        Assert.False(e.Ambiguous);
        AssertNear(TallBand, e.Picture, 0.01);
    }

    [Fact]
    public void ContentReachingBothBarsOfABandDecidesTheBand()
    {
        // A map whose left and right thirds are black: the content still spans the band from bar to bar.
        var content = new RectD(120, TallBand.Top, 260, TallBand.Height);

        var e = new TestImage(498, 1080).Busy(content).Locate(previous: PictureFit.Portrait);

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Landscape, e.Fit);
        Assert.False(e.Ambiguous);
    }

    [Fact]
    public void ContentReachingBothBarsOfAPortraitBandDecidesPortrait()
    {
        // Full screen: a dark portrait screen with one full-width row of content, after landscape.
        var picture = Portrait(1720, 720);
        var row = new RectD(picture.Left, 300, picture.Width, 60);

        var e = new TestImage(1720, 720).Busy(row).Locate(previous: PictureFit.Landscape);

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Portrait, e.Fit);
        Assert.False(e.Ambiguous);
    }

    [Fact]
    public void DarkGrayBarsKeepTheUsualTolerance()
    {
        // Bars that are not exact black (a receiver with a dark gray background): content within 40 of them is bar.
        var picture = Portrait(1720, 720);
        var image = new TestImage(1720, 720, (20, 20, 20)).Busy(picture).Fill(new RectD(10, 10, 300, 700), (40, 40, 40));

        var e = image.Locate();

        Assert.NotNull(e);
        Assert.Equal(PictureFit.Portrait, e.Fit);
        AssertNear(picture, e.Picture, 0.01);
    }

    // An iPad Air (820x1180 points): the picture is much less elongated than a phone's.
    private const double IPadAspect = 820.0 / 1180;

    [Theory]
    [InlineData(1720, 720, false)] // full screen on a wide monitor, portrait
    [InlineData(1720, 720, true)]  // ... landscape
    [InlineData(1180, 1180, true)] // a square window, landscape
    public void AnIPadPictureIsFoundWithTheIPadShape(int w, int h, bool landscape)
    {
        var picture = Geometry.VideoRect(w, h, landscape ? new SizeD(1, IPadAspect) : new SizeD(IPadAspect, 1));
        var image = new TestImage(w, h).Busy(picture);

        var e = image.Locate(IPadAspect);

        Assert.NotNull(e);
        Assert.Equal(landscape ? PictureFit.Landscape : PictureFit.Portrait, e.Fit);
        AssertNear(picture, e.Picture, 0.01);
    }

    [Fact]
    public void AnIPadPictureIsMissedWithThePhoneShape()
    {
        // Why the shape is a setting: with the phone's shape the iPad picture fits no candidate.
        var picture = Geometry.VideoRect(1720, 720, new SizeD(IPadAspect, 1));

        var e = new TestImage(1720, 720).Busy(picture).Locate(Aspect);

        Assert.True(e is null || Math.Abs(e.Picture.Width - picture.Width) > 10);
    }

    [Fact]
    public void AnAllBlackWindowTellsNothing()
    {
        Assert.Null(new TestImage(1720, 720).Locate());
        Assert.Null(Badge(new TestImage(1720, 720), 1600, 620).Locate()); // a black phone screen: only the badge
    }

    [Fact]
    public void BadInputIsRejected()
    {
        Assert.Null(PictureLocator.Locate(new byte[16], 2, 2, 8, Aspect));
        Assert.Null(PictureLocator.Locate(new byte[100], 100, 100, 400, Aspect)); // buffer too small
        Assert.Null(new TestImage(100, 100).Busy(new RectD(0, 0, 100, 100)).Locate(aspect: 0));
    }
}
