// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;

namespace IceScreenTouch.Api.Tests.Ocr;

public class OcrImagePreparerTests
{
    // The Windows engine's hints (16.10.2), with the spike's MaxImageDimension.
    private static readonly OcrImageHints Windows = new(1300, 3, 2600, 10_000, 40);

    [Theory]
    [InlineData(498, 1080, 1300.0 / 498)] // a real UxPlay picture: enlarged to a 1300 px short side
    [InlineData(300, 600, 3.0)] // at most 3 times
    [InlineData(1300, 2800, 1.0)] // already there
    [InlineData(2000, 4000, 1.0)] // between the target and the largest short side: as it is
    [InlineData(5200, 6000, 0.5)] // a short side above 2600 is shrunk to 2600
    public void TheScaleFollowsTheEnginesHints(int width, int height, double expected) =>
        Assert.Equal(expected, OcrImagePreparer.ScaleFor(width, height, Windows), 6);

    [Fact]
    public void TheLongSideNeverExceedsTheLargestDimension()
    {
        // A tall strip: 3 x would make the long side 15000.
        var scale = OcrImagePreparer.ScaleFor(400, 5000, Windows);
        Assert.Equal(2.0, scale, 6);

        // A picture longer than the engine takes is shrunk below 1.
        Assert.Equal(10_000.0 / 16_000, OcrImagePreparer.ScaleFor(1000, 16_000, Windows), 6);
    }

    [Fact]
    public void AnEngineThatScalesItselfGetsThePictureAsItIs() =>
        Assert.Equal(1.0, OcrImagePreparer.ScaleFor(498, 1080, new OcrImageHints(null, 1, null, 10_000, 1)));

    [Fact]
    public void ThePixelsNeverExceedTheLimit() =>
        Assert.True(OcrImagePreparer.ScaleFor(8000, 8000, new OcrImageHints(4096, 4, null, 16_384, 1)) * 8000 * 8000 * OcrImagePreparer.ScaleFor(8000, 8000, new OcrImageHints(4096, 4, null, 16_384, 1)) <= OcrImagePreparer.MaxPixels + 1);

    [Fact]
    public void APictureWithAlphaZeroAndPaddedRowsBecomesPackedAndOpaque()
    {
        var frame = OcrBuild.Frame(5, 3, stride: 32, alpha: 0, color: (10, 20, 30));
        var prepared = OcrImagePreparer.Prepare(frame, null, FakeOcrEngine.Capabilities());
        Assert.Equal((5, 3), (prepared.Width, prepared.Height));
        Assert.Equal(5 * 3 * 4, prepared.Bgra.Length);
        for (var i = 0; i < prepared.Bgra.Length; i += 4)
        {
            Assert.Equal(new byte[] { 10, 20, 30, 255 }, prepared.Bgra[i..(i + 4)]);
        }
    }

    [Fact]
    public void ARegionIsCutWithAMarginAndScaled()
    {
        var frame = OcrBuild.Frame(500, 1000);
        var prepared = OcrImagePreparer.Prepare(frame, new OcrRect(100, 200, 50, 20), FakeOcrEngine.Capabilities(hints: Windows));

        // 8 px around, plus one for a region that does not start on a whole pixel.
        Assert.Equal(new OcrRect(92, 192, 67, 37), prepared.Source);
        Assert.Null(prepared.Region);
        Assert.Equal(3.0, prepared.ScaleX, 2); // 37 px short side: enlarged the most allowed
        Assert.Equal((201, 111), (prepared.ContentWidth, prepared.ContentHeight));
    }

    [Fact]
    public void ARegionAtTheEdgeIsCutAtThePicture()
    {
        var frame = OcrBuild.Frame(100, 100);
        var prepared = OcrImagePreparer.Prepare(frame, new OcrRect(0, 90, 100, 10), FakeOcrEngine.Capabilities());
        Assert.Equal(new OcrRect(0, 82, 100, 18), prepared.Source);
    }

    [Fact]
    public void AnEngineThatCropsItselfGetsTheWholePictureAndTheRegion()
    {
        var frame = OcrBuild.Frame(200, 400);
        var prepared = OcrImagePreparer.Prepare(frame, new OcrRect(20, 40, 50, 60), FakeOcrEngine.Capabilities(region: true, hints: new OcrImageHints(800, 4, null, 10_000, 1)));
        Assert.Equal(new OcrRect(0, 0, 200, 400), prepared.Source);
        Assert.Equal(new OcrRect(80, 160, 200, 240), prepared.Region);
    }

    [Fact]
    public void ASmallImageIsPaddedAndPaddingIsNotThePicture()
    {
        var frame = OcrBuild.Frame(20, 10);
        var prepared = OcrImagePreparer.Prepare(frame, null, FakeOcrEngine.Capabilities(hints: new OcrImageHints(null, 1, null, 10_000, 40)));
        Assert.Equal((40, 40), (prepared.Width, prepared.Height));
        Assert.Equal((20, 10), (prepared.ContentWidth, prepared.ContentHeight));
        Assert.All(Enumerable.Range(0, 40 * 40), i => Assert.Equal(255, prepared.Bgra[(i * 4) + 3]));

        // A box on the padding is not on the picture; one across its edge is cut to it.
        Assert.Null(prepared.ToPicture(new OcrRect(25, 5, 5, 5)));
        Assert.Equal(new OcrRect(15, 0, 5, 10), prepared.ToPicture(new OcrRect(15, 0, 20, 30)));
    }

    [Fact]
    public void BoxesAreScaledAndMovedBackToThePicture()
    {
        var frame = OcrBuild.Frame(500, 1000);
        var prepared = OcrImagePreparer.Prepare(frame, new OcrRect(100, 200, 50, 20), FakeOcrEngine.Capabilities(hints: Windows));
        var back = prepared.ToPicture(new OcrRect(24, 24, 150, 60))!.Value;
        Assert.Equal(92 + (24 / prepared.ScaleX), back.X, 6);
        Assert.Equal(192 + (24 / prepared.ScaleY), back.Y, 6);
        Assert.Equal(150 / prepared.ScaleX, back.Width, 6);
    }

    [Fact]
    public void EveryCallGetsANewBuffer()
    {
        var frame = OcrBuild.Frame(10, 10);
        var a = OcrImagePreparer.Prepare(frame, null, FakeOcrEngine.Capabilities());
        var b = OcrImagePreparer.Prepare(frame, null, FakeOcrEngine.Capabilities());
        Assert.NotSame(a.Bgra, b.Bgra);
    }

    [Fact]
    public void ScalingKeepsThePicture()
    {
        // Left half black, right half white: the enlarged image keeps the halves.
        var frame = OcrBuild.Frame(20, 10, color: (0, 0, 0));
        for (var y = 0; y < 10; y++)
        {
            for (var x = 10; x < 20; x++)
            {
                frame.Bgra.AsSpan(((y * 20) + x) * 4, 3).Fill(255);
            }
        }

        var prepared = OcrImagePreparer.Prepare(frame, null, FakeOcrEngine.Capabilities(hints: new OcrImageHints(30, 3, null, 10_000, 1)));
        Assert.Equal((60, 30), (prepared.Width, prepared.Height));
        Assert.Equal(0, prepared.Bgra[((15 * 60) + 5) * 4]);
        Assert.Equal(255, prepared.Bgra[((15 * 60) + 55) * 4]);
    }
}
