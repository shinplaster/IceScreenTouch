// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;
using IceScreenTouch.Api.Tests.Ocr;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>The text endpoints on the controller (docs/API-DESIGN.md 16.5 and 16.7), with the fake engine on the fake clock.</summary>
public class TextOperationTests : ApiTestBase
{
    // A 400 x 800 portrait picture; the fake engine does not scale, so its boxes are pixels of the picture.
    private const int W = 400;
    private const int H = 800;

    public TextOperationTests()
    {
        Host.Frame = TestPictures.Quadrants(W, H);
        Host.Picture = new PictureInfo(W, H);
    }

    private FakeOcrEngine Engine => Host.OcrEngine;

    /// <summary>A recognition of lines; each line is <c>(text, x, y, width, height)</c>, its words split at spaces with equal widths.</summary>
    private static OcrRecognition Screen(params (string Text, double X, double Y, double Width, double Height)[] lines) =>
        new("ja", [.. lines.Select(l =>
        {
            var parts = l.Text.Split(' ');
            var w = l.Width / parts.Length;
            var words = parts.Select((p, i) => new OcrWord(p, new OcrRect(l.X + (i * w), l.Y, w, l.Height), null)).ToList();
            return new OcrLine(l.Text, new OcrRect(l.X, l.Y, l.Width, l.Height), words, null);
        })]);

    private static readonly OcrRecognition Settings = Screen(("設定", 100, 200, 80, 40), ("Wi-Fi", 100, 400, 100, 40));

    // ---- read ----

    [Fact]
    public async Task ReadReturnsLinesInReadingOrderInScreenSpace()
    {
        Engine.Returns(Screen(("Wi-Fi", 100, 400, 100, 40), ("設定", 100, 200, 80, 40)));
        var result = await Controller.ReadTextAsync(new TextReadRequest { Lang = "ja" });

        Assert.True(result.IsSuccess, result.Error?.Detail);
        var r = result.Value!;
        Assert.Equal(("ja", "fake", "screen"), (r.Language, r.Engine.Id, r.Space));
        Assert.Equal(new TextPicture(W, H, "portrait", 0), r.Picture);
        Assert.Equal(["設定", "Wi-Fi"], r.Lines.Select(l => l.Text));
        Assert.Equal(new TextBox(0.25, 0.25, 0.2, 0.05), r.Lines[0].Box);
        Assert.Equal(new TextPoint(0.35, 0.275), r.Lines[0].Center);
        Assert.Null(r.Lines[0].Words);
        Assert.Null(r.Lines[0].Confidence);
        Assert.Equal("ja", Engine.Requests[0].Language);
    }

    [Fact]
    public async Task ReadAddsWordsWhenAsked()
    {
        Engine.Returns(Screen(("Open Settings", 0, 0, 200, 40)));
        var line = (await Controller.ReadTextAsync(new TextReadRequest { Words = true })).Value!.Lines[0];
        Assert.Equal(["Open", "Settings"], line.Words!.Select(w => w.Text));
    }

    [Fact]
    public async Task PixelSpaceUsesTheRefSize()
    {
        Engine.Returns(Settings);
        var r = (await Controller.ReadTextAsync(new TextReadRequest { Space = "pixel", Ref = new PixelRef { Width = 200, Height = 400 } })).Value!;
        Assert.Equal("pixel", r.Space);
        Assert.Equal(new TextBox(50, 100, 40, 20), r.Lines[0].Box);
    }

    [Fact]
    public async Task ARefOfTheOtherOrientationIsPictureChanged()
    {
        var result = await Controller.ReadTextAsync(new TextReadRequest { Space = "pixel", Ref = new PixelRef { Width = 800, Height = 400 } });
        AssertError(result, ApiErrorCodes.PictureChanged);
        Assert.Equal(0, Engine.Calls);
    }

    [Fact]
    public async Task ARegionIsCutOutAndOnlyItsLinesAreReturned()
    {
        // The region (0.25, 0.25)..(0.75, 0.30) = pixels (100, 200, 200, 40), cut with an 8 px margin: (92, 192, 217, 57).
        Engine.Returns(OcrBuild.Recognition(
            OcrBuild.EngineLine("in", default, new OcrWord("in", new OcrRect(20, 10, 40, 30), null)),
            OcrBuild.EngineLine("margin", default, new OcrWord("margin", new OcrRect(0, 0, 6, 6), null))));
        var r = (await Controller.ReadTextAsync(new TextReadRequest { Region = new TextRegion { X = 0.25, Y = 0.25, Width = 0.5, Height = 0.05 } })).Value!;
        Assert.Equal(["in"], r.Lines.Select(l => l.Text));
        Assert.Equal((217, 57), (Engine.Requests[0].Width, Engine.Requests[0].Height));
    }

    [Theory]
    [InlineData("ja_JP", null, null)]
    [InlineData(null, "Windows", null)]
    [InlineData(null, null, "device")]
    public async Task MalformedLanguageEngineOrSpaceIsInvalid(string? lang, string? engine, string? space)
    {
        AssertError(await Controller.ReadTextAsync(new TextReadRequest { Lang = lang, Engine = engine, Space = space }), ApiErrorCodes.InvalidRequest);
        Assert.Equal(0, Host.Captures);
    }

    [Theory]
    [InlineData(-0.1, 0, 0.5, 0.5)]
    [InlineData(0.6, 0, 0.5, 0.5)]
    [InlineData(0, 0, 0, 0.5)]
    [InlineData(double.NaN, 0, 0.5, 0.5)]
    public async Task ARegionOutsideTheScreenIsInvalid(double x, double y, double w, double h) =>
        AssertError(await Controller.ReadTextAsync(new TextReadRequest { Region = new TextRegion { X = x, Y = y, Width = w, Height = h } }), ApiErrorCodes.InvalidRequest);

    [Fact]
    public async Task AMissingBodyIsInvalid() =>
        AssertError(await Controller.ReadTextAsync(null), ApiErrorCodes.InvalidRequest);

    [Fact]
    public async Task AMissingLanguageIsOcrUnavailableWithTheAvailableOnes()
    {
        var result = await Controller.ReadTextAsync(new TextReadRequest { Lang = "fr" });
        AssertError(result, ApiErrorCodes.OcrUnavailable);
        var extras = result.Error!.Extras!;
        Assert.Equal(("language", "fake"), (extras.Reason, extras.Engine));
        Assert.Equal(["en-US", "ja"], extras.Available);
        Assert.Equal(["fake"], extras.Engines);
        Assert.DoesNotContain("fr", result.Error.Detail, StringComparison.Ordinal);
        Assert.Equal(0, Host.Captures);
    }

    [Fact]
    public async Task AnUnknownEngineIsOcrUnavailable()
    {
        var result = await Controller.ReadTextAsync(new TextReadRequest { Engine = "paddle" });
        AssertError(result, ApiErrorCodes.OcrUnavailable);
        Assert.Equal(("engine", "paddle"), (result.Error!.Extras!.Reason, result.Error.Extras.Engine));
        Assert.Equal(["fake"], result.Error.Extras.Engines);
    }

    [Fact]
    public async Task TheLicenseComesFirst()
    {
        Allowed = false;
        AssertError(await Controller.ReadTextAsync(new TextReadRequest { Lang = "bad tag" }), ApiErrorCodes.PurchaseRequired);
        AssertError(await Controller.FindTextAsync(null), ApiErrorCodes.PurchaseRequired);
        AssertError(await Controller.TapTextAsync(null), ApiErrorCodes.PurchaseRequired);
    }

    [Fact]
    public async Task ReadNeedsARunningSessionAndAPicture()
    {
        Host.Running = false;
        AssertError(await Controller.ReadTextAsync(new TextReadRequest()), ApiErrorCodes.NotRunning);
        Host.Running = true;
        Host.Picture = null;
        AssertError(await Controller.ReadTextAsync(new TextReadRequest()), ApiErrorCodes.NoPicture);
        Host.Picture = new PictureInfo(W, H);
        Host.Frame = null; // the capture failed
        AssertError(await Controller.ReadTextAsync(new TextReadRequest()), ApiErrorCodes.NoPicture);
        Assert.Equal(0, Engine.Calls);
    }

    [Fact]
    public async Task ReadIsNotAnOperation()
    {
        // A probe measurement and a running operation do not make reading busy.
        Host.ProbeSuspended = true;
        Assert.True((await Controller.ReadTextAsync(new TextReadRequest())).IsSuccess);
        Host.ProbeSuspended = false;
        var tap = Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5, HoldMs = 1000 }).AsTask();
        Assert.True(Controller.IsOperating);
        Assert.True((await Controller.ReadTextAsync(new TextReadRequest())).IsSuccess);
        Time.Wait(1000);
        Assert.True((await tap).IsSuccess);
    }

    [Fact]
    public async Task FiveReadingsASecondAndTheyShareTheScreenshotWindow()
    {
        for (var i = 0; i < ApiController.TextReadsPerSecond; i++)
        {
            Assert.True((await Controller.ReadTextAsync(new TextReadRequest())).IsSuccess);
        }

        var refused = await Controller.ReadTextAsync(new TextReadRequest());
        AssertError(refused, ApiErrorCodes.RateLimited);
        Assert.Equal(ApiController.TextReadsPerSecond, Engine.Calls);

        // The five readings also count as screenshots: five screenshots more fill that window too.
        for (var i = 0; i < ScreenshotRateLimiter.Limit - ApiController.TextReadsPerSecond; i++)
        {
            Assert.True((await Controller.ScreenshotAsync([])).IsSuccess);
        }

        AssertError(await Controller.ScreenshotAsync([]), ApiErrorCodes.RateLimited);
        Time.Wait(1000);
        Assert.True((await Controller.ReadTextAsync(new TextReadRequest())).IsSuccess);
    }

    [Fact]
    public async Task AReadingRefusedByTheScreenshotWindowDoesNotUseTheTextWindow()
    {
        for (var i = 0; i < ScreenshotRateLimiter.Limit; i++)
        {
            Assert.True((await Controller.ScreenshotAsync([])).IsSuccess);
        }

        AssertError(await Controller.ReadTextAsync(new TextReadRequest()), ApiErrorCodes.RateLimited);
        Time.Wait(1000);
        for (var i = 0; i < ApiController.TextReadsPerSecond; i++)
        {
            Assert.True((await Controller.ReadTextAsync(new TextReadRequest())).IsSuccess);
        }
    }

    [Fact]
    public async Task OneRecognitionAtATimeAndTwoMayWait()
    {
        var release = new TaskCompletionSource<OcrRecognition>();
        Engine.Waits(release.Task);
        var running = Controller.ReadTextAsync(new TextReadRequest()).AsTask();
        var waiting1 = Controller.ReadTextAsync(new TextReadRequest()).AsTask();
        var waiting2 = Controller.ReadTextAsync(new TextReadRequest()).AsTask();
        AssertError(await Controller.ReadTextAsync(new TextReadRequest()), ApiErrorCodes.RateLimited);
        Assert.Equal(1, Engine.Calls);

        release.SetResult(Settings);
        Assert.True((await running).IsSuccess);
        Assert.True((await waiting1).IsSuccess);
        Assert.True((await waiting2).IsSuccess);
        Assert.Equal(1, Engine.MostAtOnce);
    }

    [Fact]
    public async Task ABusyEngineIsRateLimitedAndAFailedOneThrows()
    {
        Engine.Fails(new OcrEngineException(OcrFailure.Busy));
        AssertError(await Controller.ReadTextAsync(new TextReadRequest()), ApiErrorCodes.RateLimited);
        Engine.Fails(new OcrEngineException(OcrFailure.Failed));
        await Assert.ThrowsAsync<OcrEngineException>(async () => await Controller.ReadTextAsync(new TextReadRequest()));
        Engine.Fails(new OcrEngineException(OcrFailure.LanguageUnavailable));
        var gone = await Controller.ReadTextAsync(new TextReadRequest());
        AssertError(gone, ApiErrorCodes.OcrUnavailable);
        Assert.Equal("language", gone.Error!.Extras!.Reason);
    }

    [Fact]
    public async Task ACaptureThatDoesNotComeBackIsNoPictureAfterFiveSeconds()
    {
        Host.CaptureOverride = async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, Time, ct);
            return null;
        };
        AssertError(await Time.Drive(Controller.ReadTextAsync(new TextReadRequest())), ApiErrorCodes.NoPicture);
    }

    [Fact]
    public async Task AnEngineThatDoesNotAnswerIsAnInternalErrorAfterFiveSeconds()
    {
        Engine.Waits(new TaskCompletionSource<OcrRecognition>().Task, ignoreCancellation: true);
        AssertError(await Time.Drive(Controller.ReadTextAsync(new TextReadRequest())), ApiErrorCodes.InternalError);
    }

    [Fact]
    public async Task TheLogHasTheKindTheResultAndTheEngineButNoText()
    {
        Engine.Returns(Settings);
        await Controller.ReadTextAsync(new TextReadRequest());
        Engine.Returns(Settings);
        await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi", SettleMs = 0 }));
        Assert.Contains("[api] text-read ok fake", Log);
        Assert.Contains(Log, l => l.StartsWith("[api] text-tap ok fake ", StringComparison.Ordinal));
        Assert.DoesNotContain(Log, l => l.Contains("Wi-Fi", StringComparison.Ordinal) || l.Contains("設定", StringComparison.Ordinal));
    }

    // ---- find ----

    [Fact]
    public async Task FindReturnsTheMatches()
    {
        Engine.Returns(Settings);
        var r = (await Controller.FindTextAsync(new TextFindRequest { Text = "設定" })).Value!;
        var match = Assert.Single(r.Matches);
        Assert.Equal(("設定", "設定", "word"), (match.Text, match.LineText, match.BoxLevel));
        Assert.Equal(1, r.Attempts);
    }

    [Fact]
    public async Task FindWithoutAMatchIsAnEmptyListNotAnError()
    {
        Engine.Returns(Settings);
        var r = await Controller.FindTextAsync(new TextFindRequest { Text = "Bluetooth" });
        Assert.True(r.IsSuccess);
        Assert.Empty(r.Value!.Matches);
    }

    [Fact]
    public async Task FindWaitsUntilTheTextAppears()
    {
        Engine.Returns(Screen(("Loading", 0, 0, 100, 40)));
        Engine.Returns(Screen(("Loading", 0, 0, 100, 40)));
        Engine.Returns(Screen(("Done", 0, 0, 100, 40)));
        var r = (await Time.Drive(Controller.FindTextAsync(new TextFindRequest { Text = "done", TimeoutMs = 5000 }))).Value!;
        Assert.Single(r.Matches);
        Assert.Equal(3, r.Attempts);
        Assert.Equal(1000, r.DurationMs); // two polls of 500 ms
    }

    [Fact]
    public async Task FindGivesUpAtTheTimeout()
    {
        Engine.Fallback = Screen(("Loading", 0, 0, 100, 40));
        var r = (await Time.Drive(Controller.FindTextAsync(new TextFindRequest { Text = "Done", TimeoutMs = 1200, PollMs = 500 }))).Value!;
        Assert.Empty(r.Matches);
        Assert.Equal(1200, r.DurationMs);
        Assert.Equal(4, r.Attempts); // 0, 500, 1000 and the last at 1200
    }

    [Fact]
    public async Task FindStopsAtOnceWhenTheSessionStops()
    {
        Engine.Fallback = Screen(("Loading", 0, 0, 100, 40));
        Engine.Returns(Screen(("Loading", 0, 0, 100, 40)), () => Host.Running = false);
        AssertError(await Time.Drive(Controller.FindTextAsync(new TextFindRequest { Text = "Done", TimeoutMs = 10_000 })), ApiErrorCodes.NotRunning);
    }

    [Fact]
    public async Task OnlyTwoWaitingSearchesAtOnce()
    {
        Engine.Fallback = Screen(("Loading", 0, 0, 100, 40));
        var a = Controller.FindTextAsync(new TextFindRequest { Text = "x", TimeoutMs = 3000 }).AsTask();
        var b = Controller.FindTextAsync(new TextFindRequest { Text = "x", TimeoutMs = 3000 }).AsTask();
        AssertError(await Controller.FindTextAsync(new TextFindRequest { Text = "x", TimeoutMs = 3000 }), ApiErrorCodes.RateLimited);
        Assert.True((await Controller.FindTextAsync(new TextFindRequest { Text = "x" })).IsSuccess); // without a wait it is not counted
        Time.Wait(3000);
        Assert.True((await a).IsSuccess);
        Assert.True((await b).IsSuccess);
        Assert.True((await Time.Drive(Controller.FindTextAsync(new TextFindRequest { Text = "x", TimeoutMs = 600 }))).IsSuccess);
    }

    [Fact]
    public async Task WhileWaitingARefusedReadingIsSkipped()
    {
        Engine.Fallback = Screen(("Loading", 0, 0, 100, 40));
        Engine.Returns(Screen(("Loading", 0, 0, 100, 40)));
        Engine.Fails(new OcrEngineException(OcrFailure.Busy));
        Engine.Returns(Screen(("Done", 0, 0, 100, 40)));
        var r = (await Time.Drive(Controller.FindTextAsync(new TextFindRequest { Text = "Done", TimeoutMs = 5000 }))).Value!;
        Assert.Single(r.Matches);
        Assert.Equal(2, r.Attempts);
    }

    [Fact]
    public async Task TheClientGoingAwayEndsTheWait()
    {
        Engine.Fallback = Screen(("Loading", 0, 0, 100, 40));
        using var cts = new CancellationTokenSource();
        var find = Controller.FindTextAsync(new TextFindRequest { Text = "Done", TimeoutMs = 10_000 }, cts.Token).AsTask();
        Time.Wait(700);
        await cts.CancelAsync();
        AssertError(await find, ApiErrorCodes.Cancelled);
    }

    [Theory]
    [InlineData("", null, null, null)]
    [InlineData("   ", null, null, null)]
    [InlineData("x", "regex", null, null)]
    [InlineData("x", null, 30_001, null)]
    [InlineData("x", null, -1, null)]
    [InlineData("x", null, null, 299)]
    [InlineData("x", null, null, 5001)]
    public async Task FindChecksItsCriteria(string text, string? match, int? timeout, int? poll) =>
        AssertError(await Controller.FindTextAsync(new TextFindRequest { Text = text, Match = match, TimeoutMs = timeout, PollMs = poll }), ApiErrorCodes.InvalidRequest);

    [Fact]
    public async Task TheSearchedTextIsAtMostTwoHundredCharacters()
    {
        Assert.True((await Controller.FindTextAsync(new TextFindRequest { Text = new string('a', 200) })).IsSuccess);
        AssertError(await Controller.FindTextAsync(new TextFindRequest { Text = new string('a', 201) }), ApiErrorCodes.InvalidRequest);
    }

    // ---- tap-text ----

    [Fact]
    public async Task TapTextConfirmsAndTapsTheCentre()
    {
        Engine.Fallback = Settings;
        var result = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" }));

        Assert.True(result.IsSuccess, result.Error?.Detail);
        var r = result.Value!;
        Assert.Equal(2, r.Attempts); // found, then confirmed after settleMs
        Assert.Equal(1, r.MatchCount);
        Assert.Equal(new TextPoint(0.375, 0.525), r.Tapped.At);
        var down = Touch.Frames[0];
        Assert.Equal(Expected(0.375, 0.525, Rotation.R0), down.First);
        Assert.Contains(Touch.Frames, f => f.IsLift);
        Assert.False(Touch.Frames[^1].IsDown);
        Assert.Single(Host.Leases);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task TheLeaseIsTakenOnlyToTap()
    {
        Engine.Returns(Settings, () => Assert.Empty(Host.Leases));
        Engine.Returns(Settings, () => Assert.Empty(Host.Leases));
        Assert.True((await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" }))).IsSuccess);
        Assert.Equal(["lease:acquired"], Host.Journal.Where(j => j.StartsWith("lease:acquired", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithoutSettleOneReadingIsEnough()
    {
        Engine.Fallback = Settings;
        var r = (await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi", SettleMs = 0 }))).Value!;
        Assert.Equal(1, r.Attempts);
    }

    [Fact]
    public async Task AMatchThatMovedIsConfirmedAgain()
    {
        Engine.Returns(Screen(("Wi-Fi", 100, 100, 100, 40))); // still scrolling
        Engine.Returns(Screen(("Wi-Fi", 100, 300, 100, 40)));
        Engine.Returns(Screen(("Wi-Fi", 100, 302, 100, 40))); // within 2 % of the short side (8 px)
        var r = (await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi", TimeoutMs = 2000 }))).Value!;
        Assert.Equal(3, r.Attempts);
        Assert.Equal(new TextPoint(0.375, 0.4025), r.Tapped.Center);
    }

    [Fact]
    public async Task WithoutTimeTheConfirmationMustMatch()
    {
        Engine.Returns(Screen(("Wi-Fi", 100, 100, 100, 40)));
        Engine.Returns(Screen(("Wi-Fi", 100, 300, 100, 40)));
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" })), ApiErrorCodes.TextNotFound);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task NotFoundIsAnErrorWithTheCandidates()
    {
        Engine.Fallback = Settings;
        var result = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Bluetooth" }));
        AssertError(result, ApiErrorCodes.TextNotFound);
        Assert.Equal(0, result.Error!.Extras!.MatchCount);
        Assert.Empty(result.Error.Extras.Matches!);
        Assert.DoesNotContain("Bluetooth", result.Error.Detail, StringComparison.Ordinal);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task MoreThanOneMatchIsAmbiguousUnlessFirstOrIndexIsChosen()
    {
        Engine.Fallback = Screen(("OK", 100, 100, 50, 40), ("OK", 100, 600, 50, 40));
        var ambiguous = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "OK" }));
        AssertError(ambiguous, ApiErrorCodes.AmbiguousText);
        Assert.Equal(2, ambiguous.Error!.Extras!.MatchCount);
        Assert.Equal(2, ambiguous.Error.Extras.Matches!.Count);

        var first = (await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "OK", Select = "first" }))).Value!;
        Assert.Equal(0.15, first.Tapped.Center.Y);
        Time.Wait(1000);
        var second = (await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "OK", Select = "index", Index = 1 }))).Value!;
        Assert.Equal(0.775, second.Tapped.Center.Y);
        Time.Wait(1000);
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "OK", Select = "index", Index = 2 })), ApiErrorCodes.TextNotFound);
    }

    [Fact]
    public async Task TheOffsetMovesTheTapAndMustStayOnTheScreen()
    {
        Engine.Fallback = Settings;
        var r = (await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi", Offset = new ApiPoint { X = 0.4, Y = 0 } }))).Value!;
        Assert.Equal(new TextPoint(0.775, 0.525), r.Tapped.At);
        Time.Wait(1000);
        var outside = await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi", Offset = new ApiPoint { X = 0.9, Y = 0 } }));
        AssertError(outside, ApiErrorCodes.TapOutsideScreen);
        Assert.Equal(1, outside.Error!.Extras!.MatchCount);
        Time.Wait(1000);
        var pixel = (await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi", Space = "pixel", Ref = new PixelRef { Width = 200, Height = 400 }, Offset = new ApiPoint { X = 20, Y = 0 } }))).Value!;
        Assert.Equal(new TextPoint(95, 210), pixel.Tapped.At);
    }

    [Fact]
    public async Task TapTextIsAnOperation()
    {
        var press = Controller.LongPressAsync(new LongPressRequest { X = 0.5, Y = 0.5, HoldMs = 1000 }).AsTask();
        AssertError(await Controller.TapTextAsync(new TapTextRequest { Text = "x" }), ApiErrorCodes.Busy);
        Time.Wait(1000);
        await press;

        Host.UserJustActed(Ms(10));
        AssertError(await Controller.TapTextAsync(new TapTextRequest { Text = "x" }), ApiErrorCodes.UserActive);
        Time.Wait(1000);
        Touch.IsSubscribed = false;
        AssertError(await Controller.TapTextAsync(new TapTextRequest { Text = "x" }), ApiErrorCodes.NotConnected);
        Assert.Equal(0, Engine.Calls);
    }

    [Fact]
    public async Task TheUserUsingTheMirrorWhileSearchingEndsItWithoutATap()
    {
        Engine.Returns(Settings, () => Host.UserJustActed(TimeSpan.Zero));
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" })), ApiErrorCodes.UserInterrupted);
        Assert.Empty(Touch.Frames);
        Assert.Empty(Host.Leases);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task AGestureInProgressWhenTheLeaseIsTakenIsUserInterrupted()
    {
        Engine.Fallback = Settings;
        Engine.Returns(Settings);
        Engine.Returns(Settings, () => Host.RefuseInput = new InputRefusedException(ApiErrorCodes.UserActive, "the user holds the mouse"));
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" })), ApiErrorCodes.UserInterrupted);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task ARotationWhileSearchingIsPictureChanged()
    {
        // The assumed rotation changed after the confirming picture (not by the user's input, which would be user_interrupted).
        Engine.Returns(Settings);
        Engine.Returns(Settings, () => Host.Portrait = Rotation.R180);
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" })), ApiErrorCodes.PictureChanged);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task TheScreenTurningLandscapeWhileSearchingIsPictureChanged()
    {
        Engine.Returns(Settings);
        Engine.Returns(Settings, () => Host.Picture = new PictureInfo(H, W));
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" })), ApiErrorCodes.PictureChanged);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task CtrlRWhileSearchingIsTheUsersInput()
    {
        Engine.Returns(Settings, () =>
        {
            Host.Portrait = Rotation.R180;
            Host.UserJustActed(TimeSpan.Zero);
        });
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" })), ApiErrorCodes.UserInterrupted);
    }

    [Fact]
    public async Task CancelEndsTheSearchAndOpensTheGate()
    {
        Engine.Fallback = Screen(("Loading", 0, 0, 100, 40));
        var tap = Controller.TapTextAsync(new TapTextRequest { Text = "Done", TimeoutMs = 10_000 }).AsTask();
        Time.Wait(600);
        Assert.True(Controller.CancelCurrent());
        await Eventually(() => tap.IsCompleted, "the cancelled search");
        AssertError(await tap, ApiErrorCodes.Cancelled);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task TheClientGoingAwayWhileSearchingOpensTheGate()
    {
        Engine.Fallback = Screen(("Loading", 0, 0, 100, 40));
        using var cts = new CancellationTokenSource();
        var tap = Controller.TapTextAsync(new TapTextRequest { Text = "Done", TimeoutMs = 10_000 }, cts.Token).AsTask();
        Time.Wait(600);
        await cts.CancelAsync();
        await Eventually(() => tap.IsCompleted, "the abandoned search");
        AssertError(await tap, ApiErrorCodes.Cancelled);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task ASessionThatStopsWhileSearchingEndsItAtOnce()
    {
        Engine.Fallback = Screen(("Loading", 0, 0, 100, 40));
        Engine.Returns(Screen(("Loading", 0, 0, 100, 40)), () => Touch.IsSubscribed = false);
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Done", TimeoutMs = 10_000 })), ApiErrorCodes.NotConnected);
    }

    [Fact]
    public async Task ACaptureThatDoesNotComeBackEndsTheSearchAsNoPicture()
    {
        Host.CaptureOverride = async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, Time, ct);
            return null;
        };
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "x" })), ApiErrorCodes.NoPicture);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task AnEngineThatDoesNotAnswerEndsTheSearchAsNotFound()
    {
        Engine.Waits(new TaskCompletionSource<OcrRecognition>().Task, ignoreCancellation: true);
        AssertError(await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "x" })), ApiErrorCodes.TextNotFound);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task AFailedConfirmationNeverTaps()
    {
        Engine.Returns(Settings);
        Engine.Fails(new OcrEngineException(OcrFailure.Failed));
        await Assert.ThrowsAsync<OcrEngineException>(async () => await Time.Drive(Controller.TapTextAsync(new TapTextRequest { Text = "Wi-Fi" })));
        Assert.Empty(Touch.Frames);
        Assert.False(Controller.IsOperating);
    }

    [Theory]
    [InlineData("random", null)]
    [InlineData("index", null)]
    [InlineData("index", -1)]
    [InlineData("first", 0)]
    public async Task TheSelectionIsChecked(string select, int? index) =>
        AssertError(await Controller.TapTextAsync(new TapTextRequest { Text = "x", Select = select, Index = index }), ApiErrorCodes.InvalidRequest);

    [Fact]
    public async Task TheWholeCallMustFitInSixtySeconds()
    {
        // 30 s of waiting + 2 s settle + 2 s for the readings + 10 taps of 2.6 s = 60 s: allowed; one millisecond more is not.
        var fits = new TapTextRequest { Text = "x", TimeoutMs = 30_000, SettleMs = 2000, Count = 10, HoldMs = 2580, IntervalMs = 25 };
        Assert.NotEqual(ApiErrorCodes.InvalidRequest, (await Controller.TapTextAsync(fits with { TimeoutMs = 0 })).Error?.Code);
        AssertError(await Controller.TapTextAsync(fits with { HoldMs = 2581 }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.TapTextAsync(new TapTextRequest { Text = "x", Offset = new ApiPoint { X = 1.1, Y = 0 } }), ApiErrorCodes.InvalidRequest);
        AssertError(await Controller.TapTextAsync(new TapTextRequest { Text = "x", SettleMs = 2001 }), ApiErrorCodes.InvalidRequest);
    }

    // ---- status ----

    [Fact]
    public async Task TheStatusShowsTheOcrEngineAndLanguages()
    {
        var ocr = (await Controller.GetStatusAsync()).Value!.Ocr!;
        Assert.True(ocr.Available);
        Assert.Equal(("fake", "builtin", true, false), (ocr.Engine!.Id, ocr.Engine.Kind, ocr.Engine.Capabilities.WordBoxes, ocr.Engine.Capabilities.Confidence));
        Assert.Equal(["en-US", "ja"], ocr.Languages);
        Assert.Equal("ja", ocr.DefaultLanguage);
        Assert.Equal(new OcrEngineSummary("fake", "Fake OCR", "builtin", true), Assert.Single(ocr.Engines));
    }
}
