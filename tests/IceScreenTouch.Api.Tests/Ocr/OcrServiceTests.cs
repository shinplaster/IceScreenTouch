// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api.Ocr;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Api.Tests.Ocr;

public class OcrServiceTests
{
    private readonly FakeTimeProvider _time = new();

    private static OcrRecognition OneWord(string text, OcrRect box) =>
        OcrBuild.Recognition(OcrBuild.EngineLine(text, box, new OcrWord(text, box, null)));

    [Fact]
    public async Task TheDefaultEngineAndLanguageAreUsedWhenNoneIsNamed()
    {
        var engine = new FakeOcrEngine();
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, null, default);
        Assert.Equal(("fake", "ja"), (target.Info.Id, target.Language));
        Assert.Equal("en-US", (await service.ResolveAsync("fake", "EN-us", default)).Language);
    }

    [Fact]
    public async Task AMissingLanguageListsTheAvailableOnes()
    {
        var service = new OcrService([new FakeOcrEngine()], _time);
        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(async () => await service.ResolveAsync(null, "fr", default));
        Assert.Equal(("language", "fake"), (ex.Details.Reason, ex.Details.EngineId));
        Assert.Equal(["en-US", "ja"], ex.Details.Available);
        Assert.Equal(["fake"], ex.Details.Engines);
    }

    [Fact]
    public async Task NoLanguageAtAllIsAMissingLanguageWithAnEmptyList()
    {
        var service = new OcrService([new FakeOcrEngine(languages: [], defaultLanguage: null)], _time);
        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(async () => await service.ResolveAsync(null, null, default));
        Assert.Equal("language", ex.Details.Reason);
        Assert.Empty(ex.Details.Available);
    }

    [Fact]
    public async Task AnUnknownEngineOrNoEngineIsAMissingEngine()
    {
        var service = new OcrService([new FakeOcrEngine("windows")], _time);
        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(async () => await service.ResolveAsync("paddle", null, default));
        Assert.Equal(("engine", "paddle"), (ex.Details.Reason, ex.Details.EngineId));
        Assert.Equal(["windows"], ex.Details.Engines);

        var none = new OcrService([], _time);
        var empty = await Assert.ThrowsAsync<OcrUnavailableException>(async () => await none.ResolveAsync(null, null, default));
        Assert.Equal(("engine", null), (empty.Details.Reason, empty.Details.EngineId));
        Assert.Empty(empty.Details.Engines);
    }

    [Fact]
    public void EnginesWithAMalformedOrRepeatedIdAreLeftOut()
    {
        var service = new OcrService([new FakeOcrEngine("Bad Id"), new FakeOcrEngine("a"), new FakeOcrEngine("a"), new FakeOcrEngine("b")], _time);
        Assert.Equal(["a", "b"], service.Engines.Select(e => e.Id));
    }

    [Fact]
    public async Task AReadingMapsBoxesBackAndOrdersLines()
    {
        // The engine enlarges 2 x; its boxes come back halved.
        var engine = new FakeOcrEngine(capabilities: FakeOcrEngine.Capabilities(hints: new OcrImageHints(200, 3, null, 10_000, 1)));
        engine.Returns(OcrBuild.Recognition(
            OcrBuild.EngineLine("b", new OcrRect(0, 0, 0, 0), new OcrWord("b", new OcrRect(100, 100, 40, 20), null)),
            OcrBuild.EngineLine("a", new OcrRect(0, 0, 0, 0), new OcrWord("a", new OcrRect(20, 20, 40, 20), null))));
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, "ja", default);
        using var turn = (await service.EnterTurnAsync(default))!;
        var reading = await service.RecognizeAsync(turn, target, OcrBuild.Frame(100, 200), null, default);

        Assert.Equal(["a", "b"], reading.Lines.Select(l => l.Text));
        Assert.Equal(new OcrRect(10, 10, 20, 10), reading.Lines[0].Box);
        var seen = Assert.Single(engine.Requests);
        Assert.Equal((200, 400, "ja"), (seen.Width, seen.Height, seen.Language));
        Assert.True(seen.Packed);
        Assert.True(seen.Opaque);
    }

    [Fact]
    public async Task ARegionReadsOnlyLinesWhoseCentreIsInside()
    {
        var engine = new FakeOcrEngine();

        // The region (40, 40, 20, 20) is cut with an 8 px margin, so the engine's pixel (0, 0) is the picture's (32, 32).
        engine.Returns(OcrBuild.Recognition(
            OcrBuild.EngineLine("in", default, new OcrWord("in", new OcrRect(10, 10, 10, 10), null)),
            OcrBuild.EngineLine("edge", default, new OcrWord("edge", new OcrRect(0, 0, 4, 4), null))));
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, null, default);
        using var turn = (await service.EnterTurnAsync(default))!;
        var reading = await service.RecognizeAsync(turn, target, OcrBuild.Frame(100, 100), new OcrRect(40, 40, 20, 20), default);

        var line = Assert.Single(reading.Lines);
        Assert.Equal(("in", new OcrRect(42, 42, 10, 10)), (line.Text, line.Box));
        Assert.Equal((37, 37), (engine.Requests[0].Width, engine.Requests[0].Height));
    }

    [Fact]
    public async Task OneRecognitionAtATimeAndTwoMayWait()
    {
        var service = new OcrService([new FakeOcrEngine()], _time);
        var first = await service.EnterTurnAsync(default);
        var second = service.EnterTurnAsync(default).AsTask();
        var third = service.EnterTurnAsync(default).AsTask();
        Assert.NotNull(first);
        Assert.False(second.IsCompleted);
        Assert.Null(await service.EnterTurnAsync(default)); // a fourth is refused

        first!.Dispose();
        using (var s = await second)
        {
            Assert.NotNull(s);
            Assert.False(third.IsCompleted);
        }

        (await third)!.Dispose();
        using var again = await service.EnterTurnAsync(default);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task ACancelledWaitGivesItsPlaceBack()
    {
        var service = new OcrService([new FakeOcrEngine()], _time);
        using var first = await service.EnterTurnAsync(default);
        using var cts = new CancellationTokenSource();
        var waiting = service.EnterTurnAsync(cts.Token).AsTask();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var second = service.EnterTurnAsync(default).AsTask();
        var third = service.EnterTurnAsync(default).AsTask();
        Assert.False(second.IsCompleted || third.IsCompleted);
        Assert.Null(await service.EnterTurnAsync(default));
    }

    [Fact]
    public async Task TheTurnIsHeldUntilTheEngineCallReallyEnds()
    {
        var engine = new FakeOcrEngine();
        var release = new TaskCompletionSource<OcrRecognition>();
        engine.Waits(release.Task, ignoreCancellation: true);
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, null, default);

        using var cts = new CancellationTokenSource();
        var turn = (await service.EnterTurnAsync(default))!;
        var reading = service.RecognizeAsync(turn, target, OcrBuild.Frame(10, 10), null, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        turn.Dispose(); // the request is over, the engine is not

        var next = service.EnterTurnAsync(default).AsTask();
        await Task.Delay(50);
        Assert.False(next.IsCompleted);

        release.SetResult(OcrBuild.Recognition());
        (await next)!.Dispose();
        Assert.Equal(1, engine.MostAtOnce);
    }

    [Fact]
    public async Task AnEngineThatDoesNotReturnIsSetAsideUntilItDoes()
    {
        var engine = new FakeOcrEngine();
        var release = new TaskCompletionSource<OcrRecognition>();
        engine.Waits(release.Task, ignoreCancellation: true);
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, null, default);

        using var cts = new CancellationTokenSource();
        var turn = (await service.EnterTurnAsync(default))!;
        var reading = service.RecognizeAsync(turn, target, OcrBuild.Frame(10, 10), null, cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        turn.Dispose();
        await WaitUntil(() => engine.Calls == 1);

        var next = service.EnterTurnAsync(default).AsTask();
        _time.Advance(OcrService.StuckAfter);
        using (var t = await next.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.NotNull(t);
        }

        // Stopped: no new call, OCR is unavailable (engine), and the status says so.
        var ex = await Assert.ThrowsAsync<OcrUnavailableException>(async () => await service.ResolveAsync(null, null, default));
        Assert.Equal("engine", ex.Details.Reason);
        Assert.Empty(ex.Details.Engines);
        var status = await service.GetStatusAsync(default);
        Assert.False(status.Available);
        Assert.False(status.Engines[0].Available);

        release.SetResult(OcrBuild.Recognition());
        await WaitUntil(() => service.AvailableEngineIds.Count == 1);
        Assert.Equal("fake", (await service.ResolveAsync(null, null, default)).Info.Id);
    }

    [Fact]
    public async Task EngineFailuresReachTheCallerAndGiveTheTurnBack()
    {
        var engine = new FakeOcrEngine();
        engine.Fails(new OcrEngineException(OcrFailure.Busy));
        engine.Fails(new InvalidOperationException("boom"));
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, null, default);
        foreach (var expected in new[] { typeof(OcrEngineException), typeof(InvalidOperationException) })
        {
            using var turn = (await service.EnterTurnAsync(default))!;
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => service.RecognizeAsync(turn, target, OcrBuild.Frame(10, 10), null, default));
            Assert.IsType(expected, ex);
        }

        await WaitUntil(async () =>
        {
            using var t = await service.EnterTurnAsync(default);
            return t is not null;
        });
    }

    [Fact]
    public async Task AnEngineThatBlocksBeforeReturningItsTaskDoesNotBlockTheCaller()
    {
        var gate = new ManualResetEventSlim();
        var engine = new BlockingEngine(gate);
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, null, default);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var turn = (await service.EnterTurnAsync(default))!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RecognizeAsync(turn, target, OcrBuild.Frame(10, 10), null, cts.Token));
        gate.Set();
    }

    [Fact]
    public async Task TheStatusShowsTheDefaultEngineAndItsLanguages()
    {
        var service = new OcrService([new FakeOcrEngine("windows"), new FakeOcrEngine("other", languages: ["fr"])], _time);
        var status = await service.GetStatusAsync(default);
        Assert.True(status.Available);
        Assert.Equal("windows", status.Engine!.Id);
        Assert.Equal(["en-US", "ja"], status.Languages);
        Assert.Equal("ja", status.DefaultLanguage);
        Assert.Equal(["windows", "other"], status.Engines.Select(e => e.Info.Id));

        var none = await new OcrService([new FakeOcrEngine(languages: [], defaultLanguage: null)], _time).GetStatusAsync(default);
        Assert.False(none.Available);
        Assert.False((await new OcrService([], _time).GetStatusAsync(default)).Available);
    }

    [Fact]
    public async Task MalformedBoxesFromTheEngineNeverReachTheReading()
    {
        var engine = new FakeOcrEngine();
        engine.Returns(OcrBuild.Recognition(OcrBuild.EngineLine("x", default, new OcrWord("x", new OcrRect(double.NaN, 0, 1, 1), 2.0), new OcrWord("y", new OcrRect(1, 1, 3, 3), double.NaN))));
        var service = new OcrService([engine], _time);
        var target = await service.ResolveAsync(null, null, default);
        using var turn = (await service.EnterTurnAsync(default))!;
        var reading = await service.RecognizeAsync(turn, target, OcrBuild.Frame(10, 10), null, default);
        var line = Assert.Single(reading.Lines);
        Assert.Equal("y", line.Text);
        Assert.Null(line.Confidence);
    }

    private static async Task WaitUntil(Func<bool> condition) => await WaitUntil(() => Task.FromResult(condition()));

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(5);
        }
    }

    private sealed class BlockingEngine(ManualResetEventSlim gate) : IOcrEngine
    {
        public OcrEngineInfo Info { get; } = new("blocking", "Blocking", "1", OcrEngineKind.BuiltIn, FakeOcrEngine.Capabilities());

        public ValueTask<OcrLanguageSet> GetLanguagesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(new OcrLanguageSet(["ja"], "ja"));

        public ValueTask<string?> ResolveLanguageAsync(string? tag, CancellationToken cancellationToken) => ValueTask.FromResult<string?>("ja");

        public Task<OcrRecognition> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
        {
            gate.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            return Task.FromResult(new OcrRecognition("ja", []));
        }
    }
}
