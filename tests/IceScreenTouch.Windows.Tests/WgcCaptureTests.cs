// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Drawing;
using IceScreenTouch.Platform.Windows;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Windows.Tests;

// Windows.Graphics.Capture against real windows: a stand-in for the receiver, and others above it. Skipped (with the reason) where
// the capture cannot run: no interactive desktop, or Windows.Graphics.Capture is not supported.
public sealed class WgcCaptureTests : IDisposable
{
    private static readonly Color Picture = Color.FromArgb(200, 120, 60);
    private static readonly (byte R, byte G, byte B) PictureRgb = (Picture.R, Picture.G, Picture.B);
    private static readonly (byte R, byte G, byte B) Black = (0, 0, 0);

    private readonly StandInWindow _window = StandInWindow.Show(new Rectangle(200, 160, 860, 360), topMost: false, color: Color.Black);

    public void Dispose() => _window.Dispose();

    private static (byte R, byte G, byte B) PixelAt(ReadOnlySpan<byte> bgra, int stride, int x, int y)
    {
        var i = (y * stride) + (x * 4);
        return (bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    // The block is at (100, 50) 200 x 100 of the client area; the pixels just outside it are black.
    private record struct Probe((byte R, byte G, byte B) Inside, (byte R, byte G, byte B) Left, (byte R, byte G, byte B) Above, (byte R, byte G, byte B) LastInside, (byte R, byte G, byte B) Right, (byte R, byte G, byte B) Below, int Width, int Height);

    private static Probe Read(ReadOnlySpan<byte> bgra, int width, int height, int stride) => new(
        PixelAt(bgra, stride, 100, 50),
        PixelAt(bgra, stride, 99, 50),
        PixelAt(bgra, stride, 100, 49),
        PixelAt(bgra, stride, 299, 149),
        PixelAt(bgra, stride, 300, 149),
        PixelAt(bgra, stride, 299, 150),
        width,
        height);

    // A window paints on its own thread and DWM composes a moment later, so a shot taken right after a paint may still show the old one
    // (the capture always gives the last frame): ask again until the picture is what was drawn, for at most 5 s. The assertions then
    // judge the last shot, so a capture that is wrong for good still fails.
    private static PictureCaptureResult<T> ShootUntil<T>(WindowPictureCapture capture, nint window, CapturedPixelsReader<T> read, Func<T, bool> done)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var shot = capture.Capture(window, read);
            if ((shot.Source == PictureSource.GraphicsCapture && shot.Value is { } value && done(value)) || watch.Elapsed > TimeSpan.FromSeconds(5))
            {
                return shot;
            }

            Thread.Sleep(50);
        }
    }

    private void ShowBlockAndWait()
    {
        _window.ShowBlock(new Rectangle(100, 50, 200, 100), Picture);
        Thread.Sleep(300); // let the compositor draw it
    }

    [WgcFact]
    public void TheClientAreaIsCapturedExactlyAndAnOpaqueWindowAboveItIsNotInIt()
    {
        ShowBlockAndWait();
        var c = _window.ClientOnScreen;
        // An opaque, topmost window over the middle of the block: a screen copy would show red there.
        using var cover = StandInWindow.Show(new Rectangle(c.X + 150, c.Y + 70, 300, 200), topMost: true, color: Color.DarkRed);
        Thread.Sleep(300);
        using var capture = new WindowPictureCapture();

        var shot = ShootUntil(
            capture,
            _window.Handle,
            (bgra, width, height, stride) => (Probe: Read(bgra, width, height, stride), UnderCover: PixelAt(bgra, stride, 200, 100), CoverOnly: PixelAt(bgra, stride, 400, 200)),
            seen => seen.Probe.Inside == PictureRgb);

        Assert.Equal(PictureSource.GraphicsCapture, shot.Source);
        var seen = shot.Value;
        Assert.Equal((860, 360), (seen.Probe.Width, seen.Probe.Height));
        Assert.Equal(PictureRgb, seen.UnderCover); // the window's own pixels, not the cover's
        Assert.Equal(Black, seen.CoverOnly);

        // The block's edges are where they were drawn: the offset of the client area in the frame is right (to the pixel).
        Assert.Equal(PictureRgb, seen.Probe.Inside);
        Assert.Equal(Black, seen.Probe.Left);
        Assert.Equal(Black, seen.Probe.Above);
        Assert.Equal(PictureRgb, seen.Probe.LastInside);
        Assert.Equal(Black, seen.Probe.Right);
        Assert.Equal(Black, seen.Probe.Below);
    }

    [WgcFact]
    public void AClickThroughOverlayAboveTheWindowIsNotInTheCapture()
    {
        ShowBlockAndWait();
        var c = _window.ClientOnScreen;
        using var overlay = new ClickThroughOverlay(new Rectangle(c.X + 150, c.Y + 80, 100, 100), Color.FromArgb(220, 30, 30));
        using var capture = new WindowPictureCapture();

        var shot = ShootUntil(capture, _window.Handle, (bgra, _, _, stride) => PixelAt(bgra, stride, 200, 130), pixel => pixel == PictureRgb);

        Assert.Equal(PictureSource.GraphicsCapture, shot.Source);
        Assert.Equal(PictureRgb, shot.Value);
    }

    [WgcFact]
    public void ASessionIsKeptBetweenShotsAndClosedAfterTenSecondsWithoutUse()
    {
        ShowBlockAndWait();
        var time = new FakeTimeProvider();
        using var capture = new WindowPictureCapture(time);

        Assert.Equal(PictureSource.GraphicsCapture, capture.Capture(_window.Handle, (_, w, _, _) => w).Source);
        Assert.Equal(1, capture.OpenSessions);
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(860, capture.Capture(_window.Handle, (_, w, _, _) => w).Value); // the same session, a static window gives its last frame
        }

        Assert.Equal(1, capture.OpenSessions);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, capture.OpenSessions);
        Assert.Equal(860, capture.Capture(_window.Handle, (_, w, _, _) => w).Value); // and a new one starts when it is needed
        Assert.Equal(1, capture.OpenSessions);
    }

    [WgcFact]
    public void AResizedWindowGivesTheNewSize()
    {
        ShowBlockAndWait();
        var notes = new List<string>();
        using var capture = new WindowPictureCapture(null, (kind, message) => notes.Add(kind + ": " + message));
        Assert.Equal((860, 360), capture.Capture(_window.Handle, (_, w, h, _) => (w, h)).Value);

        _window.SetClientSize(new Size(600, 500));
        _window.ShowBlock(new Rectangle(100, 50, 200, 100), Picture);
        Thread.Sleep(300);
        var shot = ShootUntil(
            capture,
            _window.Handle,
            (bgra, width, height, stride) => (Probe: Read(bgra, width, height, stride), Width: width, Height: height),
            seen => seen.Width == 600 && seen.Probe.Inside == PictureRgb);

        Assert.True(shot.Source == PictureSource.GraphicsCapture, string.Join(" | ", notes));
        Assert.Equal((600, 500), (shot.Value.Width, shot.Value.Height));
        Assert.Equal(PictureRgb, shot.Value.Probe.Inside);
        Assert.Equal(Black, shot.Value.Probe.Right);
        Assert.Equal(1, capture.OpenSessions);
    }

    [WgcFact]
    public void AMinimizedWindowHasNoPictureAndIsNotPrinted()
    {
        ShowBlockAndWait();
        _window.SetMinimized(true);
        Thread.Sleep(300);
        using var capture = new WindowPictureCapture();

        var shot = capture.Capture(_window.Handle, (_, w, _, _) => w);

        Assert.Equal(PictureSource.None, shot.Source);
        Assert.Equal(0, shot.Value);
        Assert.Equal(0, capture.OpenSessions);

        // Restoring takes a moment (the animation, then the window is drawn again): poll for a good shot instead of waiting a fixed time.
        _window.SetMinimized(false);
        var deadline = Environment.TickCount64 + 10_000;
        PictureCaptureResult<int> restored;
        do
        {
            Thread.Sleep(100);
            restored = capture.Capture(_window.Handle, (_, w, _, _) => w);
        }
        while (restored.Source != PictureSource.GraphicsCapture && Environment.TickCount64 < deadline);

        Assert.Equal(PictureSource.GraphicsCapture, restored.Source);
        Assert.Equal(860, restored.Value);
    }

    [WgcFact]
    public void AnotherWindowClosesTheSessionOfTheFirst()
    {
        ShowBlockAndWait();
        using var other = StandInWindow.Show(new Rectangle(300, 260, 400, 300), topMost: false, color: Color.Black);
        Thread.Sleep(300);
        using var capture = new WindowPictureCapture();

        Assert.Equal(860, capture.Capture(_window.Handle, (_, w, _, _) => w).Value);
        Assert.Equal(400, capture.Capture(other.Handle, (_, w, _, _) => w).Value);

        Assert.Equal(1, capture.OpenSessions);
    }

    [WgcFact]
    public void ADisposedCaptureRefusesAndAGoneWindowHasNoPicture()
    {
        var capture = new WindowPictureCapture();
        Assert.Equal(PictureSource.None, capture.Capture(0x7fff_fff0, (_, w, _, _) => w).Source); // not a window

        capture.Dispose();
        Assert.Throws<ObjectDisposedException>(() => capture.Capture(_window.Handle, (_, w, _, _) => w));
    }

    [ScaledMonitorWgcFact]
    public void AStretchedWindowIsCapturedAtItsClientSizeAndFillsIt()
    {
        // A window that follows no DPI, on a monitor scaled above 100 %: DWM stretches it. The capture is the stretched picture at the
        // client size (the frame is the extended frame bounds), so it fills the whole client area, with no black band.
        using var window = StandInWindow.Show(new Rectangle(100, 100, 500, 800), topMost: true, color: Picture, dpiUnaware: true);
        var scaled = false;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Primary))
        {
            window.MoveTo(screen.WorkingArea.Location + new Size(40, 40));
            var (windowDpi, monitorDpi) = Win32Windows.DpiOf(window.Handle);
            if (windowDpi != 0 && monitorDpi > windowDpi)
            {
                scaled = true;
                break;
            }
        }

        Assert.True(scaled, "the stand-in window could not be put on a scaled monitor");
        Thread.Sleep(500);
        using var capture = new WindowPictureCapture();

        var shot = ShootUntil(
            capture,
            window.Handle,
            (bgra, width, height, stride) =>
                (width, height, TopLeft: PixelAt(bgra, stride, 3, 3), Middle: PixelAt(bgra, stride, width / 2, height / 2), BottomRight: PixelAt(bgra, stride, width - 4, height - 4)),
            seen => seen.TopLeft == PictureRgb && seen.Middle == PictureRgb && seen.BottomRight == PictureRgb);

        Assert.Equal(PictureSource.GraphicsCapture, shot.Source);
        var client = Win32Windows.ClientRectOnScreen(window.Handle)!.Value;
        Assert.Equal((client.Width, client.Height), (shot.Value.width, shot.Value.height));
        Assert.Equal(PictureRgb, shot.Value.TopLeft);
        Assert.Equal(PictureRgb, shot.Value.Middle);
        Assert.Equal(PictureRgb, shot.Value.BottomRight);
    }
}
