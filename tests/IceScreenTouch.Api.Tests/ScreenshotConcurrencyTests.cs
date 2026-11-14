// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

/// <summary>How many screenshots are taken and encoded at once, and how many wait: the memory a burst of requests can hold.</summary>
public sealed class ScreenshotConcurrencyTests : ApiTestBase
{
    // An encoder that counts how many run at once and holds every call until released.
    private sealed class HoldingEncoder : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly SemaphoreSlim _started = new(0);
        private int _running;
        private int _most;

        public int Most => Volatile.Read(ref _most);

        public int Running => Volatile.Read(ref _running);

        public ScreenshotImage Encode(ApiPictureFrame frame, ScreenshotOptions options)
        {
            var now = Interlocked.Increment(ref _running);
            int seen;
            do
            {
                seen = Volatile.Read(ref _most);
            }
            while (now > seen && Interlocked.CompareExchange(ref _most, now, seen) != seen);

            _ = _started.Release();
            _release.Wait();
            _ = Interlocked.Decrement(ref _running);
            return new ScreenshotImage([1, 2, 3], "image/png", "portrait", 0, frame.Width, frame.Height);
        }

        public async Task WaitForStarted(int count)
        {
            for (var i = 0; i < count; i++)
            {
                Assert.True(await _started.WaitAsync(TimeSpan.FromSeconds(15)), "an encode never started");
            }
        }

        public void Release() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _release.Dispose();
            _started.Dispose();
        }
    }

    [Fact]
    public async Task NoMoreThanTwoScreenshotsAreEncodedAtOnceAndTheOthersHoldNoPicture()
    {
        using var encoder = new HoldingEncoder();
        Controller.Encoder = encoder.Encode;
        var all = ApiController.MaxConcurrentEncodes + ApiController.MaxWaitingScreenshots;

        var calls = Enumerable.Range(0, all).Select(_ => Controller.ScreenshotAsync([]).AsTask()).ToList();

        await encoder.WaitForStarted(ApiController.MaxConcurrentEncodes);
        await Task.Delay(200); // the others would have started by now if the limit did not hold them
        Assert.Equal(ApiController.MaxConcurrentEncodes, encoder.Running);
        Assert.Equal(ApiController.MaxConcurrentEncodes, Host.Captures); // the waiting ones have not captured: no picture waits for an encoder

        encoder.Release();
        var results = await Task.WhenAll(calls);

        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(ApiController.MaxConcurrentEncodes, encoder.Most); // reached, never exceeded
        Assert.Equal(all, Host.Captures);
    }

    [Fact]
    public async Task ARequestBeyondTheWaitingLimitIsRefusedAtOnceWithoutCapturing()
    {
        using var encoder = new HoldingEncoder();
        Controller.Encoder = encoder.Encode;
        var all = ApiController.MaxConcurrentEncodes + ApiController.MaxWaitingScreenshots;
        var calls = Enumerable.Range(0, all).Select(_ => Controller.ScreenshotAsync([]).AsTask()).ToList();
        await encoder.WaitForStarted(ApiController.MaxConcurrentEncodes);

        var refused = await Controller.ScreenshotAsync([]);

        AssertError(refused, ApiErrorCodes.RateLimited);
        Assert.Equal(ApiController.MaxConcurrentEncodes, Host.Captures);

        encoder.Release();
        Assert.All(await Task.WhenAll(calls), r => Assert.True(r.IsSuccess));

        // Everything is given back: the next request is not refused.
        Time.Advance(TimeSpan.FromSeconds(2)); // the rate limit counts the refused ones as well
        Assert.True((await Controller.ScreenshotAsync([])).IsSuccess);
    }

    [Fact]
    public async Task ARequestThatIsCancelledWhileWaitingGivesItsPlaceBack()
    {
        using var encoder = new HoldingEncoder();
        Controller.Encoder = encoder.Encode;
        var running = Enumerable.Range(0, ApiController.MaxConcurrentEncodes).Select(_ => Controller.ScreenshotAsync([]).AsTask()).ToList();
        await encoder.WaitForStarted(ApiController.MaxConcurrentEncodes);
        using var cancel = new CancellationTokenSource();
        var waiting = Controller.ScreenshotAsync([], cancel.Token).AsTask();

        await cancel.CancelAsync();

        AssertError(await waiting, ApiErrorCodes.Cancelled);
        Assert.Equal(ApiController.MaxConcurrentEncodes, Host.Captures);
        encoder.Release();
        Assert.All(await Task.WhenAll(running), r => Assert.True(r.IsSuccess));
        Time.Advance(TimeSpan.FromSeconds(2));
        Assert.True((await Controller.ScreenshotAsync([])).IsSuccess); // the cancelled request left neither its turn nor its place behind
    }
}
