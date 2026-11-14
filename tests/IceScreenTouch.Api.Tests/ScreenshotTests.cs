// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;
using SkiaSharp;

namespace IceScreenTouch.Api.Tests;

/// <summary>GET /v1/screenshot through the real server: pipeline, query, order of the answers, rate limit, image and headers.</summary>
public class ScreenshotTests : ServerTestBase
{
    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes) ?? throw new InvalidOperationException("not an image");

    private static void AssertNear(SKColor actual, (byte B, byte G, byte R) expected, int tolerance = 0)
    {
        Assert.InRange(Math.Abs(actual.Red - expected.R), 0, tolerance);
        Assert.InRange(Math.Abs(actual.Green - expected.G), 0, tolerance);
        Assert.InRange(Math.Abs(actual.Blue - expected.B), 0, tolerance);
        Assert.Equal(255, actual.Alpha);
    }

    private static void AssertQuadrants(SKBitmap image, int tolerance)
    {
        int w = image.Width, h = image.Height;
        AssertNear(image.GetPixel(w / 4, h / 4), TestPictures.Red, tolerance);
        AssertNear(image.GetPixel(3 * w / 4, h / 4), TestPictures.Green, tolerance);
        AssertNear(image.GetPixel(w / 4, 3 * h / 4), TestPictures.Blue, tolerance);
        AssertNear(image.GetPixel(3 * w / 4, 3 * h / 4), TestPictures.Yellow, tolerance);
    }

    // ---- The pipeline ------------------------------------------------------------------------------------------

    [Fact]
    public async Task ItNeedsTheTokenTheOriginAndTheHostLikeEveryRoute()
    {
        await AssertProblem(await SendAsync(HttpMethod.Get, "/v1/screenshot", token: null), ApiErrorCodes.Unauthorized);
        await AssertProblem(await SendAsync(HttpMethod.Get, "/v1/screenshot", tweak: r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.example")), ApiErrorCodes.ForbiddenOrigin);
        await AssertProblem(await SendAsync(HttpMethod.Get, "/v1/screenshot", tweak: r => r.Headers.Host = "evil.example"), ApiErrorCodes.ForbiddenOrigin);

        Assert.Equal(0, Host.Captures);
    }

    [Fact]
    public async Task OnlyGetIsAllowed()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1/screenshot", "{}");

        await AssertProblem(response, ApiErrorCodes.MethodNotAllowed);
        Assert.Equal("GET", string.Join(", ", response.Content.Headers.Allow));
    }

    [Fact]
    public async Task WithoutLicenseItIsPurchaseRequiredBeforeTheQueryIsLooked()
    {
        Allowed = false;

        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.PurchaseRequired);
        await AssertProblem(await GetAsync("/v1/screenshot?format=gif"), ApiErrorCodes.PurchaseRequired);
        Assert.Equal(0, Host.Captures);
    }

    // ---- The query ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("format=gif")]
    [InlineData("format=PNG")]
    [InlineData("format=jpg")]
    [InlineData("format=")]
    [InlineData("format")]
    [InlineData("Format=png")]
    [InlineData("maxsize=100")]
    [InlineData("maxSize=15")]
    [InlineData("maxSize=8193")]
    [InlineData("maxSize=0")]
    [InlineData("maxSize=-100")]
    [InlineData("maxSize=%2B100")]
    [InlineData("maxSize=%20100")]
    [InlineData("maxSize=100.5")]
    [InlineData("maxSize=1e3")]
    [InlineData("maxSize=abc")]
    [InlineData("maxSize=")]
    [InlineData("maxSize=99999999999")]
    [InlineData("quality=80")]
    [InlineData("format=png&quality=80")]
    [InlineData("format=jpeg&quality=0")]
    [InlineData("format=jpeg&quality=101")]
    [InlineData("format=jpeg&quality=x")]
    [InlineData("format=jpeg&quality=")]
    [InlineData("width=100")]
    [InlineData("format=png&format=jpeg")]
    [InlineData("maxSize=100&maxSize=200")]
    public async Task ABadQueryIsInvalidRequestAndCapturesNothing(string query)
    {
        var response = await GetAsync("/v1/screenshot?" + query);

        var problem = await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        // The detail never repeats what was sent.
        Assert.DoesNotContain("evil", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Equal(0, Host.Captures);
    }

    [Theory]
    [InlineData("", "image/png")]
    [InlineData("format=png", "image/png")]
    [InlineData("format=png&maxSize=16", "image/png")]
    [InlineData("maxSize=8192", "image/png")]
    [InlineData("format=jpeg", "image/jpeg")]
    [InlineData("format=jpeg&quality=1", "image/jpeg")]
    [InlineData("format=jpeg&quality=100&maxSize=30", "image/jpeg")]
    [InlineData("quality=75&format=jpeg", "image/jpeg")]
    public async Task AValidQueryGivesAnImage(string query, string contentType)
    {
        var response = await GetAsync("/v1/screenshot?" + query);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        using var image = Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.True(image.Width > 0 && image.Height > 0);
    }

    [Fact]
    public async Task AnUnknownParameterIsRefusedWithoutNamingIt()
    {
        var problem = await AssertProblem(await GetAsync("/v1/screenshot?SECRETNAME=1"), ApiErrorCodes.InvalidRequest);

        Assert.DoesNotContain("SECRETNAME", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    // ---- The state ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NotRunningIsConflictAndNoPictureIsConflict()
    {
        Host.Running = false;
        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.NotRunning);

        Host.Running = true;
        Host.Picture = null;
        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.NoPicture);
        Assert.Equal(0, Host.Captures); // no picture rectangle: nothing to capture

        Host.Picture = new PictureInfo(40, 80);
        Host.Frame = null; // minimized, gone or the capture failed
        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.NoPicture);
        Assert.Equal(1, Host.Captures);
    }

    [Fact]
    public async Task TheQueryIsCheckedBeforeTheStateAndTheLicenseBeforeBoth()
    {
        Host.Running = false;

        await AssertProblem(await GetAsync("/v1/screenshot?format=gif"), ApiErrorCodes.InvalidRequest);
        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.NotRunning);
    }

    [Fact]
    public async Task ItWorksWhileTheProbeHasSuspendedTheBridge()
    {
        Host.ProbeSuspended = true;

        Assert.Equal(200, (int)(await GetAsync("/v1/screenshot")).StatusCode);
    }

    [Fact]
    public async Task ItWorksWhileAnOperationRunsAndIsNeverBusy()
    {
        var running = PostAsync("/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 5000 }""");
        await WaitUntil(() => Touch.Downs.Any(), "the finger going down");
        Assert.True(Controller.IsOperating);

        var response = await GetAsync("/v1/screenshot");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.True(Controller.IsOperating); // still the operation's gate
        Assert.Equal(200, (int)(await GetAsync("/v1/screenshot?format=jpeg")).StatusCode);

        await PostAsync("/v1/cancel");
        await AssertProblem(await running, ApiErrorCodes.Cancelled);
    }

    [Fact]
    public async Task ItWorksWhileAStreamSessionIsOpen()
    {
        await using var session = (await Controller.TryOpenSessionAsync()).Value!;

        Assert.Equal(200, (int)(await GetAsync("/v1/screenshot")).StatusCode);
        AssertError(await Controller.TryOpenSessionAsync(), ApiErrorCodes.Busy); // the gate is the session's
    }

    [Fact]
    public async Task ItSendsNoInputAndTakesNoLease()
    {
        await GetAsync("/v1/screenshot");

        Assert.Empty(Touch.Frames);
        Assert.Empty(Host.Leases);
    }

    // ---- The rate limit ----------------------------------------------------------------------------------------

    [Fact]
    public async Task TenPerSecondAreAllowedAndTheEleventhIsRateLimitedWithRetryAfter()
    {
        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(200, (int)(await GetAsync("/v1/screenshot")).StatusCode);
        }

        var refused = await GetAsync("/v1/screenshot");

        await AssertProblem(refused, ApiErrorCodes.RateLimited);
        Assert.Equal(429, (int)refused.StatusCode);
        Assert.Equal("1", refused.Headers.GetValues("Retry-After").Single());
        Assert.Equal(10, Host.Captures);

        Time.Advance(TimeSpan.FromMilliseconds(999));
        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.RateLimited);

        Time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(200, (int)(await GetAsync("/v1/screenshot")).StatusCode);
    }

    [Fact]
    public async Task TheWindowRollsAndARefusedRequestDoesNotExtendIt()
    {
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(200, (int)(await GetAsync("/v1/screenshot")).StatusCode);
        }

        Time.Advance(TimeSpan.FromMilliseconds(600));
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(200, (int)(await GetAsync("/v1/screenshot")).StatusCode);
        }

        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.RateLimited); // 10 in the last 600 ms

        // 400 ms later the first five are older than a second, the second five are not.
        Time.Advance(TimeSpan.FromMilliseconds(400));
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(200, (int)(await GetAsync("/v1/screenshot")).StatusCode);
        }

        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.RateLimited);
    }

    [Fact]
    public async Task OnlyRequestsThatPassedTheQueryCheckAreCounted()
    {
        for (var i = 0; i < 30; i++)
        {
            await AssertProblem(await GetAsync("/v1/screenshot?format=gif"), ApiErrorCodes.InvalidRequest);
        }

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(200, (int)(await GetAsync("/v1/screenshot")).StatusCode);
        }
    }

    [Fact]
    public async Task ARequestThatPassedTheQueryCountsEvenWhenTheStateRefusesIt()
    {
        Host.Running = false;
        for (var i = 0; i < 10; i++)
        {
            await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.NotRunning);
        }

        Host.Running = true;
        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.RateLimited); // the limit comes before not_running
    }

    [Fact]
    public async Task TheLimitIsOverAllClients()
    {
        using var other = new HttpClient(new HttpClientHandler { UseProxy = false });
        for (var i = 0; i < 10; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, Url("/v1/screenshot"));
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {TestToken}");
            using var response = await (i % 2 == 0 ? other.SendAsync(request) : GetAsync("/v1/screenshot"));
            Assert.Equal(200, (int)response.StatusCode);
        }

        await AssertProblem(await GetAsync("/v1/screenshot"), ApiErrorCodes.RateLimited);
    }

    [Fact]
    public async Task TheLogHasOneLinePerKindNotOnePerScreenshot()
    {
        for (var i = 0; i < 25; i++)
        {
            await GetAsync("/v1/screenshot");
        }

        Assert.Single(Log, l => l.StartsWith("[api] screenshot ok", StringComparison.Ordinal));
        Assert.Single(Log, l => l.StartsWith("[api] screenshot rate_limited", StringComparison.Ordinal));
        Assert.DoesNotContain(Log, l => l.Contains(TestToken, StringComparison.Ordinal));
    }

    // ---- The image and the headers -----------------------------------------------------------------------------

    [Fact]
    public async Task ThePngIsTheCapturedPictureOpaqueAndTheHeadersSayWhatItIs()
    {
        Host.Frame = TestPictures.Quadrants(60, 100, landscape: false, rotation: Rotation.R180);

        var response = await GetAsync("/v1/screenshot");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("portrait", response.Headers.GetValues("X-IceScreenTouch-Orientation").Single());
        Assert.Equal("180", response.Headers.GetValues("X-IceScreenTouch-Rotation").Single());
        Assert.Equal("60x100", response.Headers.GetValues("X-IceScreenTouch-Source-Size").Single());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(response.Content.Headers.ContentLength, (await response.Content.ReadAsByteArrayAsync()).Length);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], bytes[..4]);
        Assert.Equal(2, bytes[25]); // IHDR colour type 2: RGB, no alpha channel (the receiver's alpha byte was 0)
        using var image = Decode(bytes);
        Assert.Equal((60, 100), (image.Width, image.Height));
        AssertQuadrants(image, tolerance: 0);
        Assert.All(Enumerable.Range(0, 100), y => Assert.Equal(255, image.GetPixel(0, y).Alpha));
    }

    [Fact]
    public async Task ALandscapePictureSaysLandscapeAndItsRotation()
    {
        Host.Frame = TestPictures.Quadrants(100, 60, landscape: true, rotation: Rotation.R270);

        var response = await GetAsync("/v1/screenshot");

        Assert.Equal("landscape", response.Headers.GetValues("X-IceScreenTouch-Orientation").Single());
        Assert.Equal("270", response.Headers.GetValues("X-IceScreenTouch-Rotation").Single());
        Assert.Equal("100x60", response.Headers.GetValues("X-IceScreenTouch-Source-Size").Single());
    }

    [Theory]
    [InlineData(200, 100, 50, 50, 25)]
    [InlineData(100, 300, 150, 50, 150)]
    [InlineData(101, 303, 100, 33, 100)]
    [InlineData(100, 100, 40, 40, 40)]
    [InlineData(200, 100, 200, 200, 100)] // as large as the limit: unchanged
    [InlineData(200, 100, 500, 200, 100)] // never enlarged
    [InlineData(10, 400, 16, 1, 16)] // a side never reaches 0
    public async Task MaxSizeShrinksTheLongestSideKeepingTheAspectAndTheSourceSizeStaysTheOriginal(int w, int h, int max, int expectedW, int expectedH)
    {
        Host.Frame = TestPictures.Quadrants(w, h);

        var response = await GetAsync($"/v1/screenshot?maxSize={max}");

        Assert.Equal($"{w}x{h}", response.Headers.GetValues("X-IceScreenTouch-Source-Size").Single());
        using var image = Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal((expectedW, expectedH), (image.Width, image.Height));
        if (expectedW >= 8 && expectedH >= 8)
        {
            AssertQuadrants(image, tolerance: 12);
        }
    }

    [Fact]
    public async Task AJpegIsOpaqueAndKeepsTheColours()
    {
        Host.Frame = TestPictures.Quadrants(80, 160);

        var response = await GetAsync("/v1/screenshot?format=jpeg&quality=95&maxSize=100");

        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("80x160", response.Headers.GetValues("X-IceScreenTouch-Source-Size").Single());
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal([0xFF, 0xD8], bytes[..2]);
        using var image = Decode(bytes);
        Assert.Equal((50, 100), (image.Width, image.Height));
        AssertQuadrants(image, tolerance: 40);
    }

    [Fact]
    public async Task ALowerJpegQualityMakesASmallerFile()
    {
        Host.Frame = Noise(200, 300);

        var high = await (await GetAsync("/v1/screenshot?format=jpeg&quality=95")).Content.ReadAsByteArrayAsync();
        var low = await (await GetAsync("/v1/screenshot?format=jpeg&quality=10")).Content.ReadAsByteArrayAsync();
        var defaulted = await (await GetAsync("/v1/screenshot?format=jpeg")).Content.ReadAsByteArrayAsync();
        var ninety = await (await GetAsync("/v1/screenshot?format=jpeg&quality=90")).Content.ReadAsByteArrayAsync();

        Assert.True(low.Length < high.Length);
        Assert.Equal(ninety, defaulted); // the default quality is 90
    }

    private static ApiPictureFrame Noise(int width, int height)
    {
        var random = new Random(7);
        var bytes = new byte[width * height * 4];
        random.NextBytes(bytes);
        return new ApiPictureFrame(bytes, width, height, width * 4, false, Rotation.R0);
    }

    // ---- Failing and going away ----------------------------------------------------------------------------------

    [Fact]
    public async Task AClientThatGoesAwayCancelsTheCapture()
    {
        var seen = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        Host.CaptureOverride = async ct =>
        {
            seen.TrySetResult(ct);
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        };
        using var cts = new CancellationTokenSource();
        var request = SendAsync(HttpMethod.Get, "/v1/screenshot", cancellationToken: cts.Token);
        var token = await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await Eventually(() => token.IsCancellationRequested, "the capture's token");
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task AFailingCaptureIsAnInternalErrorWithAGenericDetail()
    {
        Host.CaptureOverride = _ => throw new InvalidOperationException("SECRET-DETAIL");

        var response = await GetAsync("/v1/screenshot");

        var problem = await AssertProblem(response, ApiErrorCodes.InternalError);
        Assert.DoesNotContain("SECRET", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains(Log.Concat(ServerLog), l => l.Contains("screenshot failed", StringComparison.Ordinal));
        Assert.DoesNotContain(Log.Concat(ServerLog), l => l.Contains("SECRET", StringComparison.Ordinal));
    }
}
