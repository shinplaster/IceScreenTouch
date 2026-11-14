// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Windows.Tests;

// The pure decisions of the window capture: the size PrintWindow draws a stretched window at, and the client area inside a capture frame.
public sealed class CaptureGeometryTests
{
    [Theory]
    [InlineData(676, 1466, 96u, 120u, 541, 1173)] // the sizes measured on a 125 % monitor (2026-11-14)
    [InlineData(1000, 500, 96u, 144u, 667, 333)]
    [InlineData(1000, 500, 120u, 96u, 1250, 625)] // a window that is bigger than the monitor's DPI (system aware, moved to a 100 % monitor)
    [InlineData(676, 1466, 120u, 120u, 676, 1466)] // follows the monitor: unchanged
    [InlineData(676, 1466, 0u, 120u, 676, 1466)] // unknown DPI: unchanged
    [InlineData(676, 1466, 96u, 0u, 676, 1466)]
    [InlineData(1, 1, 96u, 192u, 1, 1)] // never below one pixel
    public void TheSizeAWindowDrawsAtIsTheClientSizeScaledByTheDpiRatio(int w, int h, uint windowDpi, uint monitorDpi, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), CaptureGeometry.LogicalClientSize(w, h, windowDpi, monitorDpi));
    }

    // ---- Where the client area lies in a Windows.Graphics.Capture frame -------------------------------------------------

    [Fact]
    public void TheClientAreaIsAtTheOffsetOfItsTopLeftCornerFromTheFrameBoundsTopLeft()
    {
        // A window with a 1 px side border and a 31 px title bar: frame 862 x 391 at (200, 160), client 860 x 360 at (201, 191).
        var client = CaptureGeometry.ClientInFrame(new RectI(200, 160, 862, 391), new RectI(201, 191, 860, 360), 862, 391);

        Assert.Equal(new RectI(1, 31, 860, 360), client);
    }

    [Fact]
    public void AFramelessWindowIsTheWholeFrameAndANegativeScreenPositionWorks()
    {
        Assert.Equal(new RectI(0, 0, 500, 800), CaptureGeometry.ClientInFrame(new RectI(-1920, 100, 500, 800), new RectI(-1920, 100, 500, 800), 500, 800));
        Assert.Equal(new RectI(8, 31, 484, 761), CaptureGeometry.ClientInFrame(new RectI(-1920, 100, 500, 800), new RectI(-1912, 131, 484, 761), 500, 800));
    }

    [Theory]
    [InlineData(863, 391)] // the frame is wider than the bounds say (the window was resized)
    [InlineData(862, 390)]
    [InlineData(861, 391)]
    [InlineData(0, 391)]
    [InlineData(862, 0)]
    public void AFrameThatIsNotExactlyTheSizeOfTheBoundsIsNoPictureRatherThanAMisalignedOne(int frameWidth, int frameHeight)
    {
        Assert.Null(CaptureGeometry.ClientInFrame(new RectI(200, 160, 862, 391), new RectI(201, 191, 860, 360), frameWidth, frameHeight));
    }

    [Theory]
    [InlineData(199, 191, 860, 360)] // starts left of the frame
    [InlineData(201, 159, 860, 360)] // starts above it
    [InlineData(201, 191, 862, 360)] // ends right of it (201 + 862 > 200 + 862)
    [InlineData(201, 191, 860, 361)] // ends below it
    [InlineData(201, 191, 0, 360)] // empty
    [InlineData(201, 191, 860, 0)]
    public void AClientAreaThatIsNotInsideTheFrameIsNoPicture(int left, int top, int width, int height)
    {
        Assert.Null(CaptureGeometry.ClientInFrame(new RectI(200, 160, 862, 391), new RectI(left, top, width, height), 862, 391));
    }

    [Fact]
    public void TheSumsDoNotOverflow()
    {
        Assert.Null(CaptureGeometry.ClientInFrame(new RectI(int.MinValue, 0, 100, 100), new RectI(int.MaxValue, 0, 50, 50), 100, 100));
        Assert.Null(CaptureGeometry.ClientInFrame(new RectI(0, 0, 100, 100), new RectI(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue), 100, 100));
    }
}
