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

// The time limits run on a fake clock: a limit passes only when a test advances the clock, never because a runner is slow.
public sealed class CaptureRunnerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Ms = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(10); // a real-time bound for a thread to get somewhere

    private static (CaptureRunner Runner, List<CaptureIssue> Issues, FakeTimeProvider Time) Make(TimeSpan? timeout = null)
    {
        List<CaptureIssue> issues = [];
        var time = new FakeTimeProvider();
        return (new CaptureRunner(timeout ?? Limit, issue => { lock (issues) { issues.Add(issue); } }, time), issues, time);
    }

    private static CaptureIssue[] Reported(List<CaptureIssue> issues)
    {
        lock (issues)
        {
            return [.. issues];
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < Long, "the condition was never met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ACaptureRunsAndItsResultIsReturned()
    {
        var (runner, issues, _) = Make();

        Assert.Equal("pixels", await runner.RunAsync(() => "pixels", CancellationToken.None));
        Assert.Equal("again", await runner.RunAsync(() => "again", CancellationToken.None));
        Assert.Empty(Reported(issues));
    }

    [Fact]
    public async Task AFailedCaptureThrowsAndFreesTheGate()
    {
        var (runner, issues, _) = Make();

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunAsync<string>(() => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Equal("next", await runner.RunAsync(() => "next", CancellationToken.None));
        Assert.Empty(Reported(issues));
        Assert.False(runner.IsStuck);
    }

    [Fact]
    public async Task CapturesNeverOverlap()
    {
        var (runner, issues, _) = Make();
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

        // The fake clock stands still, so no limit passes however long the six take in real time.
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => runner.RunAsync(Work, CancellationToken.None).AsTask()));

        Assert.Equal(1, most);
        Assert.Empty(Reported(issues));
    }

    [Fact]
    public async Task ATimedOutCaptureAnswersNullAndTheNextRequestIsRefusedAtOnce()
    {
        var (runner, issues, time) = Make();
        using var release = new ManualResetEventSlim();

        var first = runner.RunAsync(() => { release.Wait(); return "late"; }, CancellationToken.None).AsTask();
        time.Advance(Limit);

        Assert.Null(await first);
        Assert.Equal([CaptureIssue.TimedOut], Reported(issues));
        Assert.True(runner.IsStuck);

        // Refused without waiting: the answer is there before the clock moves at all.
        var second = runner.RunAsync(() => "never", CancellationToken.None);
        Assert.True(second.IsCompleted);
        Assert.Null(await second);
        Assert.Equal([CaptureIssue.TimedOut, CaptureIssue.StillStuck], Reported(issues));

        // The stuck capture ends at last: the gate is free and the runner works again.
        release.Set();
        await WaitUntil(() => !runner.IsStuck);
        Assert.Equal("fine", await runner.RunAsync(() => "fine", CancellationToken.None));
    }

    [Fact]
    public async Task AFailureAfterATimeoutAlsoFreesTheGate()
    {
        var (runner, _, time) = Make();
        using var release = new ManualResetEventSlim();

        var first = runner.RunAsync<string>(() => { release.Wait(); throw new InvalidOperationException("late failure"); }, CancellationToken.None).AsTask();
        time.Advance(Limit);
        Assert.Null(await first);
        release.Set();
        await WaitUntil(() => !runner.IsStuck);

        Assert.Equal("fine", await runner.RunAsync(() => "fine", CancellationToken.None));
    }

    [Fact]
    public async Task ACallerThatCancelsGetsACancellationNotATimeoutAndTheGateIsFreedWhenTheCaptureEnds()
    {
        var (runner, issues, _) = Make();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var cancel = new CancellationTokenSource();

        var call = runner.RunAsync<string>(() => { started.Set(); release.Wait(); return "late"; }, cancel.Token).AsTask();
        Assert.True(started.Wait(Long));
        await cancel.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(cancel.Token, ex.CancellationToken);
        Assert.Empty(Reported(issues));
        Assert.False(runner.IsStuck);

        release.Set();
        Assert.Equal("fine", await runner.RunAsync(() => "fine", CancellationToken.None));
    }

    [Fact]
    public async Task ARequestThatCancelsBeforeItsTurnDoesNotRunItsCapture()
    {
        var (runner, issues, _) = Make();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var first = runner.RunAsync<string>(() => { started.Set(); release.Wait(); return "first"; }, CancellationToken.None).AsTask();
        Assert.True(started.Wait(Long));
        using var cancel = new CancellationTokenSource();
        var ran = false;

        var second = runner.RunAsync<string>(() => { ran = true; return "second"; }, cancel.Token).AsTask();
        await cancel.CancelAsync();

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(cancel.Token, ex.CancellationToken); // the caller's own cancellation, not the time limit's
        release.Set();
        Assert.Equal("first", await first);
        Assert.False(ran);
        Assert.Empty(Reported(issues));
    }

    [Fact]
    public async Task ARequestThatHasAlreadyCancelledDoesNotRunItsCapture()
    {
        var (runner, issues, _) = Make();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        var ran = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.RunAsync<string>(() => { ran = true; return "never"; }, cancel.Token));

        Assert.False(ran);
        Assert.Empty(Reported(issues));
        Assert.Equal("fine", await runner.RunAsync(() => "fine", CancellationToken.None));
    }

    [Fact]
    public async Task TheTimeLimitStartsWhenTheCaptureStartsNotWhenTheRequestArrives()
    {
        // Each capture takes 600 ms of the 1 s limit. The second one asks at the same time as the first, waits 600 ms for its turn
        // and is done 1.2 s after it asked, which is more than the limit, but only its own 600 ms count: waiting is not "the window
        // did not answer".
        var (runner, issues, time) = Make();
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();

        var first = runner.RunAsync(() => { releaseFirst.Wait(); return "first"; }, CancellationToken.None).AsTask();
        var second = runner.RunAsync(() => { secondStarted.Set(); releaseSecond.Wait(); return "second"; }, CancellationToken.None).AsTask();

        time.Advance(600 * Ms);
        releaseFirst.Set();
        Assert.Equal("first", await first);
        Assert.True(secondStarted.Wait(Long)); // its turn came at 600 ms

        time.Advance(600 * Ms);
        releaseSecond.Set();
        Assert.Equal("second", await second);
        Assert.Empty(Reported(issues));
        Assert.False(runner.IsStuck);
    }

    [Fact]
    public async Task ARequestThatWaitsAsLongAsACaptureMayTakeForItsTurnIsRefusedAsStuckNotAsTimedOut()
    {
        // The second request comes 100 ms after the first, while the first is running but not stuck yet: it waits for its turn.
        var (runner, issues, time) = Make();
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        var first = runner.RunAsync<string>(() => { started.Set(); release.Wait(); return "first"; }, CancellationToken.None).AsTask();
        Assert.True(started.Wait(Long));
        time.Advance(100 * Ms);
        var second = runner.RunAsync(() => "second", CancellationToken.None).AsTask();

        // At 1 s the first one times out and is stuck; the second one is still waiting for its turn.
        time.Advance(900 * Ms);
        Assert.Null(await first);
        Assert.Equal([CaptureIssue.TimedOut], Reported(issues));
        Assert.True(runner.IsStuck);
        time.Advance(99 * Ms);
        Assert.False(second.IsCompleted);

        // At 1.1 s it has waited as long as a capture may take: it never got to run, and what it reports is that the one in front
        // has not finished, not a timeout of its own.
        time.Advance(Ms);
        Assert.Null(await second);
        Assert.Equal([CaptureIssue.TimedOut, CaptureIssue.StillStuck], Reported(issues));

        release.Set();
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
