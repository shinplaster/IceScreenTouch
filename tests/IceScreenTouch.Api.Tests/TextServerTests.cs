// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json.Nodes;
using IceScreenTouch.Api.Ocr;

namespace IceScreenTouch.Api.Tests;

/// <summary>The text endpoints over HTTP: the answers have the documented shapes, and the error extensions appear only where documented.</summary>
public class TextServerTests : ServerTestBase
{
    private static readonly OcrRecognition Labels = new("ja",
    [
        new OcrLine("Wi-Fi", new OcrRect(10, 20, 20, 5), [new OcrWord("Wi-Fi", new OcrRect(10, 20, 20, 5), null)], null),
        new OcrLine("OK", new OcrRect(10, 40, 10, 5), [new OcrWord("OK", new OcrRect(10, 40, 10, 5), null)], null),
        new OcrLine("OK", new OcrRect(10, 60, 10, 5), [new OcrWord("OK", new OcrRect(10, 60, 10, 5), null)], null),
    ]);

    [Fact]
    public async Task TheTextAnswersMatchTheirSchemas()
    {
        var spec = SpecFiles.LoadYaml();
        Host.OcrEngine.Fallback = Labels;

        var read = await PostAsync("/v1/text/read", """{ "words": true }""");
        Assert.Equal(200, (int)read.StatusCode);
        var readJson = JsonNode.Parse(await read.Content.ReadAsStringAsync())!;
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "TextReadResponse", readJson));
        Assert.False(readJson["lines"]![0]!.AsObject().ContainsKey("confidence")); // the engine gives none: left out, not null

        var find = await PostAsync("/v1/text/find", """{ "text": "wi-fi" }""");
        Assert.Equal(200, (int)find.StatusCode);
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "TextFindResponse", JsonNode.Parse(await find.Content.ReadAsStringAsync())));

        var tap = await PostDriven("/v1/touch/tap-text", """{ "text": "Wi-Fi" }""");
        Assert.Equal(200, (int)tap.StatusCode);
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "TapTextResponse", JsonNode.Parse(await tap.Content.ReadAsStringAsync())));
    }

    [Fact]
    public async Task OcrErrorsCarryTheirExtensionMembers()
    {
        var spec = SpecFiles.LoadYaml();
        var unavailable = await PostAsync("/v1/text/read", """{ "lang": "fr" }""");
        await AssertProblem(unavailable, ApiErrorCodes.OcrUnavailable);
        var problem = JsonNode.Parse(await unavailable.Content.ReadAsStringAsync())!;
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "Problem", problem));
        Assert.Equal("language", problem["reason"]!.GetValue<string>());
        Assert.Equal(["en-US", "ja"], problem["available"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(problem.AsObject().ContainsKey("matches"));

        Host.OcrEngine.Fallback = Labels;
        Time.Wait(1000);
        var ambiguous = await PostDriven("/v1/touch/tap-text", """{ "text": "OK" }""");
        await AssertProblem(ambiguous, ApiErrorCodes.AmbiguousText);
        var candidates = JsonNode.Parse(await ambiguous.Content.ReadAsStringAsync())!;
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "Problem", candidates));
        Assert.Equal(2, candidates["matchCount"]!.GetValue<int>());
        Assert.Equal(2, candidates["matches"]!.AsArray().Count);
        Assert.False(candidates.AsObject().ContainsKey("reason"));
    }

    [Fact]
    public async Task OtherErrorsKeepTheirShape()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1/touch/tap", """{ "x": 5, "y": 5 }""");
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal(["code", "detail", "status", "title", "type"], problem.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnUnknownMemberIsInvalidButAnUnknownEngineIsUnavailable()
    {
        await AssertProblem(await PostAsync("/v1/text/read", """{ "language": "ja" }"""), ApiErrorCodes.InvalidRequest);
        await AssertProblem(await PostAsync("/v1/text/read", """{ "engine": "paddle" }"""), ApiErrorCodes.OcrUnavailable);
        await AssertProblem(await PostAsync("/v1/text/read", """{ "engine": "Paddle OCR" }"""), ApiErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task TheRateLimitAnswerHasRetryAfter()
    {
        for (var i = 0; i < ApiController.TextReadsPerSecond; i++)
        {
            Assert.Equal(200, (int)(await PostAsync("/v1/text/read", "{}")).StatusCode);
        }

        var refused = await PostAsync("/v1/text/read", "{}");
        await AssertProblem(refused, ApiErrorCodes.RateLimited);
        Assert.Equal("1", refused.Headers.RetryAfter!.ToString());
    }

    [Fact]
    public async Task TheStatusHasTheOcrPart()
    {
        var status = JsonNode.Parse(await (await GetAsync("/v1/status")).Content.ReadAsStringAsync())!;
        var ocr = status["ocr"]!;
        Assert.True(ocr["available"]!.GetValue<bool>());
        Assert.Equal("fake", ocr["engine"]!["id"]!.GetValue<string>());
        Assert.Equal("ja", ocr["defaultLanguage"]!.GetValue<string>());
    }
}

/// <summary>A host without OCR: the status says so and every text request is <c>ocr_unavailable</c> (reason <c>engine</c>, no engine).</summary>
public class NoOcrHostTests
{
    [Fact]
    public async Task WithoutAnEngineOcrIsUnavailable()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var host = new HostWithoutOcr(new FakeHost(time));
        using var controller = new ApiController(host, () => true, time);
        var ocr = (await controller.GetStatusAsync()).Value!.Ocr!;
        Assert.False(ocr.Available);
        Assert.Null(ocr.Engine);
        Assert.Empty(ocr.Engines);
        Assert.Empty(ocr.Languages);

        var read = await controller.ReadTextAsync(new TextReadRequest());
        Assert.Equal(ApiErrorCodes.OcrUnavailable, read.Error!.Code);
        Assert.Equal("engine", read.Error.Extras!.Reason);
        Assert.Null(read.Error.Extras.Engine);
        Assert.Empty(read.Error.Extras.Engines!);

        var spec = SpecFiles.LoadYaml();
        var status = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize((await controller.GetStatusAsync()).Value!, ApiJson.Default.ApiStatus))!;
        Assert.Empty(SchemaCheck.ValidateNamed(spec, "Status", status));
    }

    // Every member of the fake host, but the default (empty) OCR engine list of IApiHost.
    private sealed class HostWithoutOcr(FakeHost inner) : IApiHost
    {
        public IceScreenTouch.Core.ITouchSink Touch => inner.Touch;

        public IceScreenTouch.Core.IKeyboardSink Keyboard => inner.Keyboard;

        public IceScreenTouch.Core.IHomeButtonSink HomeButton => inner.HomeButton;

        public ApiHostSnapshot GetSnapshot() => inner.GetSnapshot();

        public ApiStatus GetStatus() => inner.GetStatus();

        public ValueTask<IInputLease> AcquireInputAsync(CancellationToken cancellationToken) => inner.AcquireInputAsync(cancellationToken);

        public void SetRotation(IceScreenTouch.Core.Rotation? landscape, IceScreenTouch.Core.Rotation? portrait) => inner.SetRotation(landscape, portrait);

        public bool ChooseTarget(string? deviceId) => inner.ChooseTarget(deviceId);

        public ValueTask<ApiPictureFrame?> CapturePictureAsync(CancellationToken cancellationToken) => inner.CapturePictureAsync(cancellationToken);
    }
}
