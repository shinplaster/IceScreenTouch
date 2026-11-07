using System.Diagnostics;
using System.Drawing;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using IceScreenTouch.Video;

namespace IceScreenTouch.Windows.Tests;

// PrintWindow capture and picture detection against a real window: a black stand-in with a colored block where
// a receiver would draw the phone picture. No input is injected.
public sealed class WindowCaptureTests : IDisposable
{
    private static readonly Color Picture = Color.FromArgb(200, 120, 60);
    private static readonly double Aspect = Geometry.FallbackVideoSize.Width / Geometry.FallbackVideoSize.Height;

    private readonly StandInWindow _window = StandInWindow.Show(new Rectangle(200, 160, 860, 360), topMost: false, color: Color.Black);

    public void Dispose() => _window.Dispose();

    private static Rectangle Round(RectD r) =>
        Rectangle.FromLTRB((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Left + r.Width), (int)Math.Round(r.Top + r.Height));

    private static (byte R, byte G, byte B) PixelAt(ReadOnlySpan<byte> bgra, int stride, int x, int y)
    {
        var i = (y * stride) + (x * 4);
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    private static RectD WaitForPicture(WindowVideoSource source, Func<RectD, bool> done)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            var window = source.FindWindow();
            if (window != 0 && Win32Windows.ClientRectOnScreen(window) is { } client)
            {
                var picture = source.LocatePicture(window, new SizeD(client.Width, client.Height));
                if (picture is { } p && source.Picture.LastPicture is not null && done(p))
                {
                    return p;
                }
            }

            Thread.Sleep(20);
        }

        throw new TimeoutException("the picture was not found");
    }

    [Fact]
    public void TheClientAreaIsCapturedEvenWhenCovered()
    {
        _window.ShowBlock(new Rectangle(100, 50, 200, 100), Picture);
        var c = _window.ClientOnScreen;
        using var cover = StandInWindow.Show(new Rectangle(c.X + 50, c.Y + 20, 300, 200), color: Color.DarkRed);
        using var capture = new WindowCapture();

        var seen = capture.Capture(_window.Handle, (bgra, width, height, stride) =>
            (width, height, Block: PixelAt(bgra, stride, 200, 100), Bar: PixelAt(bgra, stride, 10, 10), Covered: PixelAt(bgra, stride, 120, 60)));

        Assert.Equal((860, 360), (seen.width, seen.height));
        Assert.Equal((Picture.R, Picture.G, Picture.B), seen.Block);
        Assert.Equal(((byte)0, (byte)0, (byte)0), seen.Bar);
        Assert.Equal((Picture.R, Picture.G, Picture.B), seen.Covered); // under the red cover on screen
    }

    [Fact]
    public void AMinimizedOrMissingWindowGivesNothing()
    {
        using var capture = new WindowCapture();

        Assert.Equal(0, capture.Capture(0, (_, w, _, _) => w));
        Assert.Equal(0, capture.Capture(0x7fff_fff0, (_, w, _, _) => w));
    }

    [Fact]
    public void TheVideoSourceFindsALetterboxedPortraitPicture()
    {
        var expected = Geometry.VideoRect(860, 360, new SizeD(Aspect, 1));
        _window.ShowBlock(Round(expected), Picture);
        using var self = Process.GetCurrentProcess();
        using var source = new WindowVideoSource(self.ProcessName, new WindowVideoSourceOptions { CaptureInterval = TimeSpan.FromMilliseconds(50) });

        var picture = WaitForPicture(source, _ => true);

        Assert.Equal(_window.Handle, source.FindWindow());
        Assert.Equal(PictureFit.Portrait, source.Picture.Fit);
        Assert.InRange(picture.Left, expected.Left - 1.5, expected.Left + 1.5);
        Assert.InRange(picture.Width, expected.Width - 1.5, expected.Width + 1.5);
        Assert.Null(source.VideoSize); // never resize a window that is not ours
    }

    [Fact]
    public void TheVideoSourceFollowsARotationAndAResize()
    {
        _window.ShowBlock(Round(Geometry.VideoRect(860, 360, new SizeD(Aspect, 1))), Picture);
        using var self = Process.GetCurrentProcess();
        using var source = new WindowVideoSource(self.ProcessName, new WindowVideoSourceOptions { CaptureInterval = TimeSpan.FromMilliseconds(50) });
        WaitForPicture(source, _ => true);

        // The phone turns: a landscape picture between thin bars.
        var landscape = Geometry.VideoRect(860, 360, new SizeD(1, Aspect));
        _window.ShowBlock(Round(landscape), Picture);
        var picture = WaitForPicture(source, p => p.IsLandscape);
        Assert.Equal(PictureFit.Landscape, source.Picture.Fit);
        Assert.InRange(picture.Width, landscape.Width - 1.5, landscape.Width + 1.5);

        // The window is resized: the picture is found again in the new client area.
        _window.SetClientSize(new Size(600, 500));
        var resized = Geometry.VideoRect(600, 500, new SizeD(1, Aspect));
        _window.ShowBlock(Round(resized), Picture);
        picture = WaitForPicture(source, p => Math.Abs(p.Width - resized.Width) < 1.5 && Math.Abs(p.Top - resized.Top) < 1.5);
        Assert.True(picture.IsLandscape);
    }

    [Fact]
    public void WithFitWindowALetterboxedPictureAsksForTheWindowToBeReshaped()
    {
        // A wide window that kept its shape after the phone turned to portrait (a uxplay.exe started by hand).
        _window.ShowBlock(Round(Geometry.VideoRect(860, 360, new SizeD(Aspect, 1))), Picture);
        using var self = Process.GetCurrentProcess();
        using var source = new WindowVideoSource(self.ProcessName, new WindowVideoSourceOptions
        {
            CaptureInterval = TimeSpan.FromMilliseconds(50),
            FitWindow = true,
            FitWindowSettleTime = TimeSpan.FromMilliseconds(200),
        });
        WaitForPicture(source, _ => true);

        var watch = Stopwatch.StartNew();
        while (source.WindowSize is null && watch.Elapsed < TimeSpan.FromSeconds(5))
        {
            source.FindWindow();
            Thread.Sleep(20);
        }

        // The long side (860) stays; the shape becomes the picture's (166x360 drawn -> 397x860).
        var asked = Assert.NotNull(source.WindowSize);
        Assert.Equal(860, asked.Height);
        Assert.InRange(asked.Width, 860 * Aspect - 6, 860 * Aspect + 6);
        Assert.Null(source.VideoSize);
    }

    [Fact]
    public void WithoutFitWindowTheWindowIsNeverReshaped()
    {
        _window.ShowBlock(Round(Geometry.VideoRect(860, 360, new SizeD(Aspect, 1))), Picture);
        using var self = Process.GetCurrentProcess();
        using var source = new WindowVideoSource(self.ProcessName, new WindowVideoSourceOptions { CaptureInterval = TimeSpan.FromMilliseconds(50) });
        WaitForPicture(source, _ => true);
        Thread.Sleep(300);

        Assert.Null(source.WindowSize);
    }

    [Fact]
    public void MaximizedAndFullScreenWindowsAreRecognized()
    {
        Assert.False(Win32Windows.IsMaximizedOrFullScreen(_window.Handle));
        Assert.False(Win32Windows.IsMaximizedOrFullScreen(0));

        _window.SetMaximized(true);
        Assert.True(Win32Windows.IsMaximizedOrFullScreen(_window.Handle));

        _window.SetMaximized(false);
        _window.CoverMonitor();
        Assert.True(Win32Windows.IsMaximizedOrFullScreen(_window.Handle));
    }

    [Fact]
    public void WithoutDetectionThePictureIsAGuessFromTheWindowShape()
    {
        using var self = Process.GetCurrentProcess();
        using var source = new WindowVideoSource(self.ProcessName, new WindowVideoSourceOptions { DetectPicture = false });
        var window = source.FindWindow();

        Assert.Equal(Geometry.VideoRect(860, 360, new SizeD(Aspect, 1)), source.LocatePicture(window, new SizeD(860, 360)));
        Assert.Equal(new RectD(0, 0, 852, 393), source.LocatePicture(window, new SizeD(852, 393)));
        Assert.Null(source.Picture.LastPicture);
    }
}
