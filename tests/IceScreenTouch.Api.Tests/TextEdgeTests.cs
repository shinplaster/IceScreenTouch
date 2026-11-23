// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;
using IceScreenTouch.Api.Tests.Ocr;

namespace IceScreenTouch.Api.Tests;

/// <summary>Edges of tap-text and of the readings: refused confirmations, wait slots, deadlines and odd characters.</summary>
public class TextEdgeTests : ApiTestBase
{
    private static readonly OcrRecognition WiFi = new("ja",
        [new OcrLine("Wi-Fi", new OcrRect(10, 20, 20, 5), [new OcrWord("Wi-Fi", new OcrRect(10, 20, 20, 5), null)], null)]);

    private static readonly OcrRecognition Twice = new("ja",
    [
        new OcrLine("OK", new OcrRect(10, 20, 10, 5), [new OcrWord("OK", new OcrRect(10, 20, 10, 5), null)], null),
        new OcrLine("OK", new OcrRect(10, 60, 10, 5), [new OcrWord("OK", new OcrRect(10, 60, 10, 5), null)], null),
    ]);

    public TextEdgeTests() => Controller.Ocr.CallInline = true;

    private FakeOcrEngine Engine => Host.OcrEngine;

    [Fact]
    public async Task AConfirmationTheRateWindowRefusesIsRateLimitedNotNotFound()
    {
        Engine.Fallback = WiFi;
        for (var i = 0; i < ApiController.TextReadsPerSecond - 1; i++)
        {
            Assert.True((await Controller.ReadTextAsync(new TextReadRequest())).IsSuccess);
        }

        // The first reading takes the last place of the window; its confirmation, with no time left, is refused.
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi", SettleMs = 100 })), ApiErrorCodes.RateLimited);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task AWaitingTapTextHoldsOneOfTheTwoWaitSlots()
    {
        Engine.Fallback = new OcrRecognition("ja", []);
        var tap = Controller.TapTextAsync(new TapTextRequest { Text = "Done", TimeoutMs = 10_000 }).AsTask();
        var find = Controller.FindTextAsync(new TextFindRequest { Text = "Done", TimeoutMs = 10_000 }).AsTask();
        AssertError(await Controller.FindTextAsync(new TextFindRequest { Text = "Done", TimeoutMs = 1000 }), ApiErrorCodes.RateLimited);

        Assert.True(Controller.CancelCurrent()); // ends the tap-text, whose slot is given back
        await Eventually(() => tap.IsCompleted, "the cancelled tap-text");
        AssertError(await tap, ApiErrorCodes.Cancelled);
        var again = Controller.FindTextAsync(new TextFindRequest { Text = "Done", TimeoutMs = 1000 }).AsTask();
        Time.Wait(10_000);
        Assert.True((await again).IsSuccess);
        Assert.True((await find).IsSuccess);
    }

    [Fact]
    public async Task StillAmbiguousAtTheDeadlineIsAmbiguous()
    {
        Engine.Fallback = Twice;
        var result = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "OK", TimeoutMs = 1200 }));
        AssertError(result, ApiErrorCodes.AmbiguousText);
        Assert.Equal(2, result.Error!.Extras!.MatchCount);
        Assert.True(Engine.Calls >= 3); // it kept reading until the deadline
    }

    [Fact]
    public async Task AConfirmationThatFindsAnotherMatchAtTheDeadlineEndsTheSearch()
    {
        Engine.Returns(WiFi);
        Engine.Returns(new OcrRecognition("ja", [new OcrLine("Wi-Fi", new OcrRect(10, 60, 20, 5), [new OcrWord("Wi-Fi", new OcrRect(10, 60, 20, 5), null)], null)]));
        Engine.Fallback = WiFi;
        var result = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" }));
        AssertError(result, ApiErrorCodes.TextNotFound);
        Assert.Equal(2, Engine.Calls);
        Assert.Equal(1, result.Error!.Extras!.MatchCount);
    }

    [Fact]
    public async Task TheHardDeadlineStillListsTheLastCandidates()
    {
        Engine.Returns(WiFi);
        Engine.Waits(new TaskCompletionSource<OcrRecognition>().Task, ignoreCancellation: true); // the confirmation never comes back
        var result = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" }));
        AssertError(result, ApiErrorCodes.TextNotFound);
        Assert.Equal(1, result.Error!.Extras!.MatchCount);
        Assert.Single(result.Error.Extras.Matches!);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task NotFoundWithoutAnyReadingStillHasEmptyCandidates()
    {
        Engine.Waits(new TaskCompletionSource<OcrRecognition>().Task, ignoreCancellation: true);
        var result = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" }));
        AssertError(result, ApiErrorCodes.TextNotFound);
        Assert.Equal(0, result.Error!.Extras!.MatchCount);
        Assert.Empty(result.Error.Extras.Matches!);
    }

    [Fact]
    public async Task AReadingThatOnlyWaitedForItsTurnIsRateLimitedNotAnInternalError()
    {
        Engine.Waits(new TaskCompletionSource<OcrRecognition>().Task, ignoreCancellation: true);
        var first = Controller.ReadTextAsync(new TextReadRequest()).AsTask();
        var second = Controller.ReadTextAsync(new TextReadRequest()).AsTask();
        Time.Wait(5000);
        AssertError(await first, ApiErrorCodes.InternalError); // the engine did not answer
        AssertError(await second, ApiErrorCodes.RateLimited); // it never got the turn
    }

    [Fact]
    public async Task AnOffsetThatLandsOnTheEdgeIsOnTheScreen()
    {
        // Centre x = 20 / 400 = 0.05; an offset of 0.95 lands on 1 (maybe a rounding error above it).
        Host.Frame = TestPictures.Quadrants(400, 800);
        Host.Picture = new PictureInfo(400, 800);
        Engine.Fallback = WiFi;
        var result = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi", Offset = new ApiPoint { X = 0.95, Y = 0 }, SettleMs = 0 }));
        Assert.True(result.IsSuccess, result.Error?.Detail);
        Assert.Equal(1, result.Value!.Tapped.At.X);
    }

    [Fact]
    public async Task ANoncharacterInTheSearchedTextIsNoInternalError()
    {
        Engine.Fallback = WiFi;
        var result = await Controller.FindTextAsync(new TextFindRequest { Text = "a￾b" });
        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Matches);
    }
}
