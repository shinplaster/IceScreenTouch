// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using IceScreenTouch.Api.Ocr;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Api.Tests.Ocr;

/// <summary>Turn misuse, engines that hang their questions, and engine output too large to read whole.</summary>
public class OcrBoundsTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public async Task ATurnRunsOneRecognitionAndOnlyWhileHeld()
    {
        var engine = new FakeOcrEngine();
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, null, default);

        var turn = (await service.EnterTurnAsync(default))!;
        _ = await service.RecognizeAsync(turn, target, OcrBuild.Frame(10, 10), null, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(turn, target, OcrBuild.Frame(10, 10), null, default));
        turn.Dispose();

        var released = (await service.EnterTurnAsync(default))!;
        released.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecognizeAsync(released, target, OcrBuild.Frame(10, 10), null, default));
        Assert.Equal(1, engine.Calls); // the misused turns never reached the engine
    }

    [Fact]
    public async Task AFailureBeforeTheEngineCallGivesTheTurnBack()
    {
        var service = new OcrService([new FakeOcrEngine()], _time);
        var target = await service.ResolveAsync(null, null, default);
        using (var turn = (await service.EnterTurnAsync(default))!)
        {
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.RecognizeAsync(turn, target, OcrBuild.Frame(10, 10), new OcrRect(double.NaN, 0, 1, 1), default));
        }

        using var next = await service.EnterTurnAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(next);
    }

    [Fact]
    public async Task AnEngineThatDoesNotAnswerItsLanguageQuestionsIsUnavailable()
    {
        var hang = new TaskCompletionSource();
        var engine = new FakeOcrEngine { QueriesWaitFor = hang.Task };
        var service = new OcrService([engine], _time);

        var resolving = service.ResolveAsync(null, "ja", default).AsTask();
        var status = service.GetStatusAsync(default).AsTask();
        await Task.Delay(20);
        _time.Advance(OcrService.QueryTimeout);

        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(() => resolving.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("engine", ex.Details.Reason);
        var s = await status.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(s.Available);
        Assert.Empty(s.Languages);
        hang.SetResult();
    }

    [Fact]
    public async Task LanguageListsAreCleaned()
    {
        var engine = new FakeOcrEngine(languages: ["ja", "JA", "bad tag", .. Enumerable.Range(0, 100).Select(i => $"x-{i}")]);
        var status = await new OcrService([engine], _time).GetStatusAsync(default);
        Assert.Equal(OcrService.MaxLanguages, status.Languages.Count);
        Assert.Equal("ja", status.Languages[0]);
        Assert.DoesNotContain("JA", status.Languages);
        Assert.DoesNotContain("bad tag", status.Languages);
    }

    [Fact]
    public void HugeEngineOutputIsHandledInBoundedWork()
    {
        var bad = new OcrWord("x", new OcrRect(double.NaN, 0, 1, 1), null);
        var line = new OcrLine("x", new OcrRect(0, 0, 1, 1), [.. Enumerable.Repeat(bad, 2_000_000)], null);
        var watch = Stopwatch.StartNew();
        var result = OcrOutputGuard.Sanitize(new OcrRecognition("ja", [line, line]), 10, 10);
        Assert.Empty(result.Lines);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
    }

    [Fact]
    public void TheTotalTextIsLimited()
    {
        var text = new string('a', OcrOutputGuard.MaxTextLength);
        var lines = Enumerable.Range(0, 400).Select(i => new OcrLine(text, new OcrRect(0, i, 10, 1), [new OcrWord(text, new OcrRect(0, i, 10, 1), null)], null)).ToList();
        var result = OcrOutputGuard.Sanitize(new OcrRecognition("ja", lines), 100, 1000);
        Assert.Equal(OcrOutputGuard.MaxTotalTextLength / OcrOutputGuard.MaxTextLength, result.Lines.Count);
    }

    [Fact]
    public void MatchesAreCapped()
    {
        var words = Enumerable.Range(0, 3000).Select(i => new TextWord("a", new OcrRect(i, 0, 1, 1), null)).ToList();
        var line = TextLayout.CreateLine("", new OcrRect(0, 0, 3000, 1), words, null);
        Assert.Equal(TextMatcher.MaxMatches, TextMatcher.Find([line], "a", TextMatchMode.Contains, default).Count);
    }

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(-1, 0, 5, 5)]
    [InlineData(5, 5, 100, 5)]
    [InlineData(0, 0, double.PositiveInfinity, 1)]
    public void ARegionOutsideThePictureIsRefused(double x, double y, double w, double h) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrImagePreparer.Prepare(OcrBuild.Frame(10, 10), new OcrRect(x, y, w, h), FakeOcrEngine.Capabilities()));
}
