// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using IceScreenTouch.Api.Ocr;
using IceScreenTouch.Api.Tests.Ocr;

namespace IceScreenTouch.Api.Tests;

/// <summary>A trial quota for the tests: unlimited until <see cref="Limited"/> is set; counts what it was asked to take.</summary>
internal sealed class FakeQuota : IApiQuota
{
    private readonly Lock _lock = new();

    public bool Limited { get; set; }

    public int OperationLimit { get; set; } = 300;

    public int CaptureLimit { get; set; } = 300;

    public int StreamSeconds { get; set; } = 300;

    public int Operations { get; set; }

    public int Captures { get; set; }

    public ApiTrialInfo? Current
    {
        get
        {
            lock (_lock)
            {
                return Limited ? new ApiTrialInfo(Math.Max(0, OperationLimit - Operations), OperationLimit, Math.Max(0, CaptureLimit - Captures), CaptureLimit, StreamSeconds) : null;
            }
        }
    }

    public bool TryTake(ApiQuotaCounter counter, out int limit)
    {
        lock (_lock)
        {
            limit = counter == ApiQuotaCounter.Operations ? OperationLimit : CaptureLimit;
            if (!Limited)
            {
                return true;
            }

            if (counter == ApiQuotaCounter.Operations)
            {
                if (Operations >= limit)
                {
                    return false;
                }

                Operations++;
            }
            else
            {
                if (Captures >= limit)
                {
                    return false;
                }

                Captures++;
            }

            return true;
        }
    }
}

/// <summary>The trial quota on the controller: what counts, when, and the answer once a counter is used up (docs/API-DESIGN.md, chapter 5).</summary>
public class TrialQuotaTests : ApiTestBase
{
    private static readonly TapRequest Tap = new() { X = 0.5, Y = 0.5 };

    private static readonly OcrRecognition SettingsScreen = new("ja", [new OcrLine("設定", new OcrRect(10, 20, 20, 10), [new OcrWord("設定", new OcrRect(10, 20, 20, 10), null)], null)]);

    public TrialQuotaTests()
    {
        Quota.Limited = true;
    }

    private static void AssertTrialLimit<T>(ApiResult<T> result, string counter, int limit) => AssertTrialLimit(result.WithoutValue(), counter, limit);

    private static void AssertTrialLimit(ApiResult result, string counter, int limit)
    {
        AssertError(result, ApiErrorCodes.PurchaseRequired);
        Assert.Equal(403, result.Error!.Status);
        Assert.Equal(new ApiErrorExtras { Reason = "trial_limit", Limit = limit, Counter = counter }, result.Error.Extras);
    }

    [Fact]
    public async Task WithoutTheTrialNothingIsCountedAndTheStatusHasNoTrial()
    {
        Quota.Limited = false;
        Quota.OperationLimit = 0;

        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);
        Assert.Null((await Controller.GetStatusAsync()).Value!.Trial);
        Assert.Equal(0, Quota.Operations);
    }

    [Fact]
    public async Task TheStatusSaysWhatIsLeftAndIsNeverCounted()
    {
        Quota.Operations = 10;
        Quota.Captures = 299;

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(new ApiTrialInfo(290, 300, 1, 300, 300), (await Controller.GetStatusAsync()).Value!.Trial);
        }

        Assert.True(Controller.GetRotation().IsSuccess);
        Assert.True((await Controller.SetRotationAsync(new RotationRequest { Landscape = 270 })).IsSuccess);
        Assert.True((await Controller.SetTargetAsync(new TargetRequest { DeviceId = null })).IsSuccess);
        Assert.False(Controller.CancelCurrent());
        Assert.Equal((10, 299), (Quota.Operations, Quota.Captures));
    }

    [Fact]
    public async Task AnOperationCountsWhenItRunsAndARefusedOneDoesNot()
    {
        AssertError(await Controller.TapAsync(new TapRequest { X = 2, Y = 0.5 }), ApiErrorCodes.InvalidRequest);
        Host.Running = false;
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.NotRunning);
        Host.Running = true;
        Host.UserJustActed(Ms(100));
        AssertError(await Controller.TapAsync(Tap), ApiErrorCodes.UserActive);
        Host.LastUserInput = null;
        Assert.Equal(0, Quota.Operations);

        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);
        Assert.True((await Time.Drive(Controller.SwipeAsync(new SwipeRequest { From = new ApiPoint { X = 0.5, Y = 0.8 }, To = new ApiPoint { X = 0.5, Y = 0.2 } }))).IsSuccess);
        Assert.True((await Time.Drive(Controller.PressKeyAsync(new KeyPressRequest { Key = "a" }))).IsSuccess);
        Assert.True((await Time.Drive(Controller.TypeTextAsync(new TypeTextRequest { Text = "ab" }))).IsSuccess);
        Assert.True((await Time.Drive(Controller.HomeAsync())).IsSuccess);

        Assert.Equal(5, Quota.Operations); // one per request, whatever it sends
        Assert.Equal(0, Quota.Captures);
    }

    [Fact]
    public async Task UsedUpOperationsAre403WithTheReasonAndNothingIsSent()
    {
        Quota.OperationLimit = 1;
        Assert.True((await Time.Drive(Controller.TapAsync(Tap))).IsSuccess);
        var frames = Touch.Frames.Count;

        AssertTrialLimit(await Controller.TapAsync(Tap), "operations", 1);
        AssertTrialLimit(await Controller.HomeAsync(), "operations", 1);
        AssertTrialLimit(await Controller.PressKeyAsync(new KeyPressRequest { Key = "a" }), "operations", 1);
        AssertTrialLimit(await Controller.TapTextAsync(new TapTextRequest { Text = "設定" }), "operations", 1);
        AssertTrialLimit(await Controller.TryOpenSessionAsync(), "operations", 1);
        AssertError(await Controller.TapAsync(null), ApiErrorCodes.InvalidRequest); // a malformed request is still told so

        Assert.Equal(frames, Touch.Frames.Count);
        Assert.Single(Host.Leases); // only the tap that ran took the input
        Assert.True((await Controller.ScreenshotAsync([])).IsSuccess); // the other counter is separate
        Assert.Contains("operations", (await Controller.TapAsync(Tap)).Error!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AScreenshotCountsOnlyWhenThereIsAPicture()
    {
        AssertError(await Controller.ScreenshotAsync([new("format", "gif")]), ApiErrorCodes.InvalidRequest);
        Host.Frame = null;
        AssertError(await Controller.ScreenshotAsync([]), ApiErrorCodes.NoPicture);
        Host.Running = false;
        AssertError(await Controller.ScreenshotAsync([]), ApiErrorCodes.NotRunning);
        Assert.Equal(0, Quota.Captures);

        Host.Running = true;
        Host.Frame = TestPictures.Quadrants(40, 80);
        Quota.CaptureLimit = 1;
        Assert.True((await Controller.ScreenshotAsync([])).IsSuccess);
        Assert.Equal(1, Quota.Captures);

        Time.Advance(TimeSpan.FromSeconds(1)); // (not the rate limit)
        AssertTrialLimit(await Controller.ScreenshotAsync([]), "captures", 1);
        Assert.Contains("screenshots and text readings", (await Controller.ScreenshotAsync([])).Error!.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATextReadingCountsOnceAndAWaitingFindCountsOnceForAllItsReadings()
    {
        Host.Frame = TestPictures.Quadrants(400, 800);
        Host.Picture = new PictureInfo(400, 800);
        Host.OcrEngine.Fallback = SettingsScreen;

        Assert.True((await Controller.ReadTextAsync(new TextReadRequest { Lang = "ja" })).IsSuccess);
        Assert.Equal(1, Quota.Captures);

        var find = (await Time.Drive(Controller.FindTextAsync(new TextFindRequest { Text = "Done", Lang = "ja", TimeoutMs = 1200, PollMs = 500 }))).Value!;
        Assert.Equal(4, find.Attempts);
        Assert.Equal(2, Quota.Captures);

        AssertError(await Controller.ReadTextAsync(new TextReadRequest { Lang = "xx-YY" }), ApiErrorCodes.OcrUnavailable); // not read: not counted
        Assert.Equal(2, Quota.Captures);
        Assert.Equal(0, Quota.Operations);

        Quota.CaptureLimit = 2;
        AssertTrialLimit(await Controller.ReadTextAsync(new TextReadRequest { Lang = "ja" }), "captures", 2);
        AssertTrialLimit(await Controller.FindTextAsync(new TextFindRequest { Text = "設定", Lang = "ja" }), "captures", 2);
    }

    [Fact]
    public async Task TapTextIsOneOperationAndNoCapture()
    {
        Host.Frame = TestPictures.Quadrants(400, 800);
        Host.Picture = new PictureInfo(400, 800);
        Host.OcrEngine.Fallback = SettingsScreen;

        var result = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "設定", Lang = "ja", SettleMs = 0 }));

        Assert.True(result.IsSuccess, result.Error?.Detail);
        Assert.Equal((1, 0), (Quota.Operations, Quota.Captures));
    }

    [Fact]
    public async Task ASessionCountsOneOperationAndCarriesTheTrialTime()
    {
        Quota.StreamSeconds = 120;

        await using (var session = (await Controller.TryOpenSessionAsync()).Value!)
        {
            Assert.Equal(TimeSpan.FromSeconds(120), session.TimeLimit);
        }

        Assert.Equal(1, Quota.Operations);

        Quota.Limited = false;
        await using var unlimited = (await Controller.TryOpenSessionAsync()).Value!;
        Assert.Null(unlimited.TimeLimit);
    }
}

/// <summary>The trial quota over HTTP and the WebSocket: the problem body, the status member, the stream's time.</summary>
public class TrialQuotaServerTests : StreamTestBase
{
    public TrialQuotaServerTests()
    {
        Quota.Limited = true;
    }

    [Fact]
    public async Task TheProblemBodyHasTheReasonTheLimitAndTheCounterButNoResetTime()
    {
        Quota.Captures = Quota.CaptureLimit;

        using var response = await SendAsync(HttpMethod.Get, "/v1/screenshot");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(response.Headers.RetryAfter); // nothing to wait for
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;
        Assert.Equal("purchase_required", root.GetProperty("code").GetString());
        Assert.Equal("trial_limit", root.GetProperty("reason").GetString());
        Assert.Equal(300, root.GetProperty("limit").GetInt32());
        Assert.Equal("captures", root.GetProperty("counter").GetString());
        Assert.False(root.TryGetProperty("resetsAt", out _));
    }

    [Fact]
    public async Task TheStatusHasTheTrialMemberOnlyInTheTrial()
    {
        Quota.Operations = 4;
        using (var response = await SendAsync(HttpMethod.Get, "/v1/status"))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var trial = body.RootElement.GetProperty("trial");
            Assert.Equal(296, trial.GetProperty("operationsLeft").GetInt32());
            Assert.Equal(300, trial.GetProperty("operationsLimit").GetInt32());
            Assert.Equal(300, trial.GetProperty("capturesLeft").GetInt32());
            Assert.Equal(300, trial.GetProperty("capturesLimit").GetInt32());
            Assert.Equal(300, trial.GetProperty("streamLimitSeconds").GetInt32());
        }

        Quota.Limited = false;
        using (var response = await SendAsync(HttpMethod.Get, "/v1/status"))
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.False(body.RootElement.TryGetProperty("trial", out _));
        }
    }

    [Fact]
    public async Task UsedUpOperationsRefuseTheStreamWith1008AndTheReason()
    {
        Quota.Operations = Quota.OperationLimit;
        var client = await ConnectAsync();
        await client.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");

        var error = await client.WaitForErrorAsync("purchase_required");
        await client.WaitEndedAsync();

        Assert.Equal("trial_limit", error.GetProperty("reason").GetString());
        Assert.Equal("operations", error.GetProperty("counter").GetString());
        Assert.Equal(300, error.GetProperty("limit").GetInt32());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
        Assert.Empty(Host.Leases);
    }

    [Fact]
    public async Task AStreamEndsWhenTheTrialTimeIsUpAndEveryFingerIsLiftedFirst()
    {
        Quota.StreamSeconds = 300;
        var client = await OpenAsync();
        Assert.Equal(1, Quota.Operations);

        Advance(299_500);
        await DownAsync(client, 1, 0.5, 0.5); // half a second before the limit the stream still works
        await client.WaitForAckAsync(1);
        Assert.False(client.Ended);

        Advance(500); // (the 1 s idle watchdog would only fire later)
        var error = await client.WaitForErrorAsync("purchase_required");
        await client.WaitEndedAsync();

        Assert.Equal("trial_limit", error.GetProperty("reason").GetString());
        Assert.Equal("stream_seconds", error.GetProperty("counter").GetString());
        Assert.Equal(300, error.GetProperty("limit").GetInt32());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
        Assert.True(Touch.Frames[^2].IsLift); // the finger was lifted where it was, then the empty report
        Assert.True(Touch.Frames[^1].IsEmpty);
        Assert.Contains(ServerLog.Concat(Log), l => l.Contains("stream closed (trial time limit)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutTheTrialAStreamHasNoTimeLimit()
    {
        Quota.Limited = false;
        var client = await OpenAsync();

        Advance(3_600_000);
        await BarrierAsync(client);

        Assert.False(client.Ended);
        Assert.Equal(0, Quota.Operations);
    }
}
