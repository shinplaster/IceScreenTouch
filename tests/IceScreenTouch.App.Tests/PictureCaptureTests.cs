// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using IceScreenTouch.App.Services;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.App.Tests;

// What a screenshot cuts out of a capture, and the gate and time limit around the capture itself (no Windows involved).
public sealed class PictureCaptureMathTests
{
    private static readonly RectI Client = new(100, 50, 390, 900);

    [Fact]
    public void ThePictureIsCutInTheCoordinatesOfTheClientArea()
    {
        var crop = PictureCaptureMath.CropFor(new RectD(110, 70, 370, 800), Client, 390, 900);

        Assert.Equal(new RectI(10, 20, 370, 800), crop);
    }

    [Fact]
    public void AClientAreaThatWasResizedSinceMeansNoPicture()
    {
        var picture = new RectD(110, 70, 370, 800);

        Assert.Null(PictureCaptureMath.CropFor(picture, Client, 391, 900));
        Assert.Null(PictureCaptureMath.CropFor(picture, Client, 390, 899));
        Assert.Null(PictureCaptureMath.CropFor(picture, Client, 800, 400)); // turned: another shape
    }

    [Fact]
    public void AWindowThatOnlyMovedGivesTheSamePixels()
    {
        // The crop is relative to the client area the picture was measured in (the capture is of the window wherever it now is), so
        // the same picture on the same client area one place over gives the same crop, and only the size of the client area is compared.
        var picture = new RectD(110, 70, 370, 800);
        var elsewhere = Client with { Left = 700, Top = 10 };

        Assert.Equal(new RectI(10, 20, 370, 800), PictureCaptureMath.CropFor(picture, Client, 390, 900));
        Assert.Equal(new RectI(10, 20, 370, 800), PictureCaptureMath.CropFor(new RectD(710, 30, 370, 800), elsewhere, 390, 900));
    }

    [Fact]
    public void ARectangleThatSticksOutByMoreThanTheRoundingIsNoPictureNotASmallerImage()
    {
        // 30 px of the picture are outside the client area: the status says 390 wide, the cut would be 360.
        Assert.Null(PictureCaptureMath.CropFor(new RectD(130, 50, 390, 900), Client, 390, 900));

        // The same rectangle one pixel out only because of the rounding is moved back in, not refused.
        Assert.Equal(new RectI(0, 0, 390, 900), PictureCaptureMath.CropFor(new RectD(99.6, 50, 390.4, 900), Client, 390, 900));
    }

    [Fact]
    public void NothingOfThePictureInsideTheClientAreaIsNoPicture()
    {
        Assert.Null(PictureCaptureMath.CropFor(new RectD(5000, 5000, 100, 100), Client, 390, 900));
        Assert.Null(PictureCaptureMath.CropFor(new RectD(110, 70, 0, 0), Client, 390, 900));
    }

    [Fact]
    public void TheCopyUsesTheStrideAndTheCropOriginAndMakesTheImageOpaque()
    {
        // 5 x 4 pixels, a row of 24 bytes (4 bytes of padding); pixel (x, y) = (x * 10 + y) in blue, green 1, red 2, alpha 0.
        const int width = 5, height = 4, stride = 24;
        var buffer = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * stride) + (x * 4);
                (buffer[i], buffer[i + 1], buffer[i + 2], buffer[i + 3]) = ((byte)((x * 10) + y), 1, 2, 0);
            }

            buffer[(y * stride) + 20] = buffer[(y * stride) + 23] = 0xEE; // padding that must not be copied
        }

        var cut = PictureCaptureMath.CopyCrop(buffer, width, height, stride, new RectI(1, 2, 3, 2));

        Assert.NotNull(cut);
        Assert.Equal((3, 2, 12), (cut.Width, cut.Height, cut.Stride));
        Assert.Equal(24, cut.Bgra.Length);
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                var i = (y * 12) + (x * 4);
                Assert.Equal([(byte)(((x + 1) * 10) + y + 2), (byte)1, (byte)2, (byte)0xFF], cut.Bgra[i..(i + 4)]);
            }
        }
    }

    [Fact]
    public void ACopyOfTheWholeBufferWithoutPaddingKeepsEveryRow()
    {
        var buffer = new byte[3 * 4 * 2];
        for (var i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = (byte)(i / 4);
        }

        var cut = PictureCaptureMath.CopyCrop(buffer, 3, 2, 12, new RectI(0, 0, 3, 2))!;

        Assert.Equal([0, 1, 2, 3, 4, 5], cut.Bgra.Where((_, i) => i % 4 == 0).ToArray());
    }

    [Theory]
    [InlineData(-1, 0, 2, 2)]
    [InlineData(0, -1, 2, 2)]
    [InlineData(4, 0, 2, 2)] // 4 + 2 > 5 wide
    [InlineData(0, 3, 2, 2)] // 3 + 2 > 4 high
    [InlineData(0, 0, 0, 2)]
    [InlineData(0, 0, 2, 0)]
    public void ACropOutsideTheBufferOrEmptyGivesNothing(int left, int top, int width, int height)
    {
        var buffer = new byte[24 * 4];

        Assert.Null(PictureCaptureMath.CopyCrop(buffer, 5, 4, 24, new RectI(left, top, width, height)));
    }

    [Fact]
    public void AnAbsurdSizeGivesNothingAndNeverOverflows()
    {
        // 20000 x 1 is wider than the limit; 8192 x 8192 is under the side limit but over the pixel limit; a huge stride and a huge
        // width do not wrap around in the size computations.
        Assert.Null(PictureCaptureMath.CopyCrop(new byte[80_000], 20_000, 1, 80_000, new RectI(0, 0, 20_000, 1)));
        Assert.Null(PictureCaptureMath.CopyCrop(new byte[16], 8_192, 8_192, 8_192 * 4, new RectI(0, 0, 8_192, 8_192)));
        Assert.Null(PictureCaptureMath.CopyCrop(new byte[16], int.MaxValue, int.MaxValue, int.MaxValue, new RectI(0, 0, 1, 1)));
        Assert.Null(PictureCaptureMath.CopyCrop(new byte[16], 2, 2, int.MaxValue, new RectI(0, 1, 1, 1))); // a stride that puts the second row far outside
        Assert.Null(PictureCaptureMath.CopyCrop(new byte[16], 0, 0, 0, new RectI(0, 0, 1, 1)));
    }

    [Fact]
    public void ABufferShorterThanItsDimensionsOrAShortStrideGivesNothing()
    {
        Assert.Null(PictureCaptureMath.CopyCrop(new byte[10], 5, 4, 24, new RectI(0, 0, 1, 1)));
        Assert.Null(PictureCaptureMath.CopyCrop(new byte[200], 5, 4, 16, new RectI(0, 0, 1, 1))); // stride below 5 * 4
    }
}

public sealed class CaptureRunnerTests
{
    private static readonly TimeSpan Short = TimeSpan.FromMilliseconds(250);

    private static (CaptureRunner Runner, List<CaptureIssue> Issues) Make(TimeSpan? timeout = null)
    {
        List<CaptureIssue> issues = [];
        return (new CaptureRunner(timeout ?? Short, issue => { lock (issues) { issues.Add(issue); } }), issues);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "the condition was never met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ACaptureRunsAndItsResultIsReturned()
    {
        var (runner, issues) = Make();

        Assert.Equal("pixels", await runner.RunAsync(() => "pixels", CancellationToken.None));
        Assert.Equal("again", await runner.RunAsync(() => "again", CancellationToken.None));
        Assert.Empty(issues);
    }

    [Fact]
    public async Task AFailedCaptureThrowsAndFreesTheGate()
    {
        var (runner, issues) = Make();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync<string>(() => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Equal("next", await runner.RunAsync(() => "next", CancellationToken.None));
        Assert.Empty(issues);
        Assert.False(runner.IsStuck);
    }

    [Fact]
    public async Task CapturesNeverOverlap()
    {
        var (runner, _) = Make(TimeSpan.FromSeconds(10));
        var inside = 0;
        var most = 0;

        string? Work()
        {
            var now = Interlocked.Increment(ref inside);
            most = Math.Max(most, now);
            Thread.Sleep(20);
            Interlocked.Decrement(ref inside);
            return "x";
        }

        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => runner.RunAsync(Work, CancellationToken.None).AsTask()));

        Assert.Equal(1, most);
    }

    [Fact]
    public async Task ATimedOutCaptureAnswersNullAndTheNextRequestIsRefusedAtOnce()
    {
        var (runner, issues) = Make();
        using var release = new ManualResetEventSlim();

        var first = await runner.RunAsync(() => { release.Wait(); return "late"; }, CancellationToken.None);

        Assert.Null(first);
        Assert.Equal([CaptureIssue.TimedOut], issues);
        Assert.True(runner.IsStuck);

        var watch = Stopwatch.StartNew();
        var second = await runner.RunAsync(() => "never", CancellationToken.None);

        Assert.Null(second);
        Assert.True(watch.Elapsed < Short / 2, $"the refusal took {watch.Elapsed}");
        Assert.Equal([CaptureIssue.TimedOut, CaptureIssue.StillStuck], issues);

        // The stuck capture ends at last: the gate is free and the runner works again.
        release.Set();
        await WaitUntil(() => !runner.IsStuck);
        Assert.Equal("fine", await runner.RunAsync(() => "fine", CancellationToken.None));
    }

    [Fact]
    public async Task AFailureAfterATimeoutAlsoFreesTheGate()
    {
        var (runner, _) = Make();
        using var release = new ManualResetEventSlim();

        Assert.Null(await runner.RunAsync<string>(() => { release.Wait(); throw new InvalidOperationException("late failure"); }, CancellationToken.None));
        release.Set();
        await WaitUntil(() => !runner.IsStuck);

        Assert.Equal("fine", await runner.RunAsync(() => "fine", CancellationToken.None));
    }

    [Fact]
    public async Task ACallerThatCancelsGetsACancellationNotATimeoutAndTheGateIsFreedWhenTheCaptureEnds()
    {
        var (runner, issues) = Make(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();

        var call = runner.RunAsync<string>(() => { started.Set(); release.Wait(); return "late"; }, cancel.Token).AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Empty(issues);
        Assert.False(runner.IsStuck);

        release.Set();
        Assert.Equal("fine", await runner.RunAsync(() => "fine", CancellationToken.None));
    }

    [Fact]
    public async Task ARequestThatCancelsBeforeItsTurnDoesNotRunItsCapture()
    {
        var (runner, issues) = Make(TimeSpan.FromSeconds(30));
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var first = runner.RunAsync<string>(() => { started.Set(); release.Wait(); return "first"; }, CancellationToken.None).AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
        using var cancel = new CancellationTokenSource();
        var ran = false;

        var second = runner.RunAsync<string>(() => { ran = true; return "second"; }, cancel.Token).AsTask();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.Set();
        Assert.Equal("first", await first);
        Assert.False(ran);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task TheTimeLimitStartsWhenTheCaptureStartsNotWhenTheRequestArrives()
    {
        // Each capture takes 600 ms of the 1 s limit; the second one waits 600 ms for its turn and is done after 1.2 s, which is
        // more than the limit, but only its own 600 ms count: waiting is not "the window did not answer".
        var (runner, issues) = Make(TimeSpan.FromSeconds(1));
        string? Slow(string name)
        {
            Thread.Sleep(600);
            return name;
        }

        var first = runner.RunAsync(() => Slow("first"), CancellationToken.None).AsTask();
        await Task.Delay(100); // the first one holds the gate
        var second = runner.RunAsync(() => Slow("second"), CancellationToken.None).AsTask();

        Assert.Equal("first", await first);
        Assert.Equal("second", await second);
        Assert.Empty(issues);
        Assert.False(runner.IsStuck);
    }

    [Fact]
    public async Task ARequestThatWaitsAsLongAsACaptureMayTakeForItsTurnIsRefusedAsStuckNotAsTimedOut()
    {
        var (runner, issues) = Make();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var first = runner.RunAsync<string>(() => { started.Set(); release.Wait(); return "first"; }, CancellationToken.None).AsTask();
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        Assert.Null(await runner.RunAsync(() => "second", CancellationToken.None));
        Assert.Contains(CaptureIssue.StillStuck, issues); // it never got to run: what it reports is that the one in front has not finished

        release.Set();
        Assert.Null(await first); // the first one timed out as well (it took longer than the limit)
        await WaitUntil(() => !runner.IsStuck);
        Assert.Equal("fine", await runner.RunAsync(() => "fine", CancellationToken.None));
    }
}

public sealed class LogThrottleTests
{
    [Fact]
    public void AKindIsLetThroughOncePerInterval()
    {
        var time = new FakeTimeProvider();
        var throttle = new LogThrottle(time, TimeSpan.FromSeconds(10));

        Assert.True(throttle.Allow("failure"));
        Assert.False(throttle.Allow("failure"));
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(throttle.Allow("failure"));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(throttle.Allow("failure"));
    }

    [Fact]
    public void KindsAreCountedApart()
    {
        var throttle = new LogThrottle(new FakeTimeProvider(), TimeSpan.FromSeconds(10));

        Assert.True(throttle.Allow("source"));
        Assert.True(throttle.Allow("failure"));
        Assert.False(throttle.Allow("source"));
    }
}
