// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json;
using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>Every route: the happy path reaches the controller and answers the documented JSON.</summary>
public class ServerRouteTests : ServerTestBase
{
    [Fact]
    public void TheRoutesAreTheDocumentedOnes()
    {
        string[] expected =
        [
            "GET /v1/status", "GET /v1/rotation", "PUT /v1/target", "PUT /v1/rotation",
            "POST /v1/touch/tap", "POST /v1/touch/long-press", "POST /v1/touch/swipe", "POST /v1/touch/path", "POST /v1/touch/pinch",
            "POST /v1/keys/press", "POST /v1/keys/type", "POST /v1/home", "POST /v1/cancel", "GET /v1/screenshot", "GET /v1/stream", "GET /v1/openapi.json",
        ];

        Assert.Equal(expected.Order(), Server.Routes.Order());
    }

    [Fact]
    public async Task StatusIsTheStatusObject()
    {
        var response = await GetAsync("/v1/status");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = await ReadJson(response);
        var root = doc.RootElement;
        Assert.Equal("0.0.0-test", root.GetProperty("version").GetString());
        Assert.Equal(1, root.GetProperty("apiVersion").GetInt32());
        Assert.True(root.GetProperty("running").GetBoolean());
        Assert.Equal("connected", root.GetProperty("bleState").GetString());
        var device = root.GetProperty("devices")[0];
        Assert.Equal("iphone-1", device.GetProperty("id").GetString());
        Assert.True(device.GetProperty("subscribed").GetBoolean());
        Assert.Equal("iphone-1", root.GetProperty("targetDeviceId").GetString());
        Assert.Equal("TestReceiver", root.GetProperty("receiver").GetString());
        Assert.Equal(390, root.GetProperty("picture").GetProperty("width").GetInt32());
        Assert.Equal("portrait", root.GetProperty("picture").GetProperty("orientation").GetString());
        Assert.Equal(90, root.GetProperty("rotation").GetProperty("landscape").GetInt32());
        Assert.False(root.GetProperty("operating").GetBoolean());
    }

    [Fact]
    public async Task EveryResponseIsNotCachedAndNotSniffed()
    {
        foreach (var response in new[] { await GetAsync("/v1/status"), await GetAsync("/v1/nothing"), await SendAsync(HttpMethod.Get, "/v1/status", token: null) })
        {
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.False(response.Headers.Contains("Server"));
        }
    }

    [Fact]
    public async Task TargetChoosesTheDevice()
    {
        var response = await SendAsync(Put, "/v1/target", """{ "deviceId": "iphone-1" }""");
        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());

        Assert.Equal(200, (int)(await SendAsync(Put, "/v1/target", """{ "deviceId": null }""")).StatusCode);
        await AssertProblem(await SendAsync(Put, "/v1/target", "{}"), ApiErrorCodes.InvalidRequest); // the member must be present

        Assert.Equal(["iphone-1", null], Host.TargetChoices);
        await AssertProblem(await SendAsync(Put, "/v1/target", """{ "deviceId": "unknown" }"""), ApiErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task TapTouchesTheDevice()
    {
        var response = await PostDriven("/v1/touch/tap", """{ "x": 0.5, "y": 0.25, "holdMs": 70 }""");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        Assert.Single(Touch.Downs);
        Assert.Equal(Expected(0.5, 0.25, Rotation.R0), Touch.Downs.Single().First);
        Assert.True(Touch.Frames[^1].IsLift || Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task TapCanRepeat()
    {
        var response = await PostDriven("/v1/touch/tap", """{ "x": 0.5, "y": 0.5, "holdMs": 70, "count": 2, "intervalMs": 30 }""");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(2, Touch.Downs.Count());
    }

    [Fact]
    public async Task LongPressSwipePathAndPinchTouchTheDevice()
    {
        Assert.Equal(200, (int)(await PostDriven("/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 100 }""")).StatusCode);
        Assert.Equal(200, (int)(await PostDriven("/v1/touch/swipe", """{ "from": { "x": 0.5, "y": 0.8 }, "to": { "x": 0.5, "y": 0.2 }, "durationMs": 100 }""")).StatusCode);
        Assert.Equal(200, (int)(await PostDriven("/v1/touch/path", """{ "points": [ { "x": 0.2, "y": 0.2, "t": 0 }, { "x": 0.8, "y": 0.8, "t": 100 } ] }""")).StatusCode);
        Assert.Equal(200, (int)(await PostDriven("/v1/touch/pinch", """{ "center": { "x": 0.5, "y": 0.5 }, "startRadius": 0.1, "endRadius": 0.3, "durationMs": 100 }""")).StatusCode);

        Assert.True(Touch.Downs.Count() >= 4);
        Assert.Equal(4, Host.Leases.Count);
        Assert.All(Host.Leases, lease => Assert.True(lease.Disposed));
    }

    [Fact]
    public async Task KeysPressAndType()
    {
        var press = await PostDriven("/v1/keys/press", """{ "key": "Enter", "modifiers": ["cmd"], "holdMs": 30 }""");
        Assert.Equal(200, (int)press.StatusCode);
        Assert.Equal("{}", await press.Content.ReadAsStringAsync());
        Assert.NotEmpty(Keys.Reports);

        var type = await PostDriven("/v1/keys/type", """{ "text": "abあ", "intervalMs": 5 }""");
        Assert.Equal(200, (int)type.StatusCode);
        using var doc = await ReadJson(type);
        Assert.Equal("あ", doc.RootElement.GetProperty("skipped").GetString());
    }

    [Fact]
    public async Task HomePressesTheHomeButton()
    {
        var response = await PostAsync("/v1/home");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("{}", await response.Content.ReadAsStringAsync());
        Assert.Equal(1, Host.HomeSink.Presses);
    }

    [Fact]
    public async Task RotationCanBeReadAndSet()
    {
        using (var doc = await ReadJson(await GetAsync("/v1/rotation")))
        {
            Assert.Equal(90, doc.RootElement.GetProperty("landscape").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("portrait").GetInt32());
        }

        var put = await SendAsync(Put, "/v1/rotation", """{ "landscape": 270 }""");
        Assert.Equal(200, (int)put.StatusCode);
        using (var doc = await ReadJson(put))
        {
            Assert.Equal(270, doc.RootElement.GetProperty("landscape").GetInt32());
            Assert.Equal(0, doc.RootElement.GetProperty("portrait").GetInt32());
        }

        Assert.Equal([(Rotation.R270, (Rotation?)null)], Host.RotationSets.Select(s => (s.Landscape!.Value, s.Portrait)));
        await AssertProblem(await SendAsync(Put, "/v1/rotation", """{ "landscape": 45 }"""), ApiErrorCodes.InvalidRequest);
        await AssertProblem(await SendAsync(Put, "/v1/rotation", "{}"), ApiErrorCodes.InvalidRequest);
    }

    [Fact]
    public async Task CancelWithNothingRunningSaysSo()
    {
        var response = await PostAsync("/v1/cancel");

        Assert.Equal(200, (int)response.StatusCode);
        using var doc = await ReadJson(response);
        Assert.False(doc.RootElement.GetProperty("cancelled").GetBoolean());
    }

    [Fact]
    public async Task CancelEndsARunningOperationAndLifts()
    {
        var running = PostAsync("/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 5000 }""");
        await WaitUntil(() => Touch.Downs.Any(), "the finger going down");

        var cancel = await PostAsync("/v1/cancel");

        using (var doc = await ReadJson(cancel))
        {
            Assert.True(doc.RootElement.GetProperty("cancelled").GetBoolean());
        }

        await AssertProblem(await running, ApiErrorCodes.Cancelled);
        Assert.True(Touch.Frames[^1].IsLift || Touch.Frames[^1].IsEmpty);
        Assert.True(Host.Lease!.Disposed);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task OpenApiNeedsNoToken()
    {
        var response = await SendAsync(HttpMethod.Get, "/v1/openapi.json", token: null);

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = await ReadJson(response);
        Assert.Equal("3.1.0", doc.RootElement.GetProperty("openapi").GetString());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("paths").ValueKind);
        Assert.Equal(200, (int)(await GetAsync("/v1/openapi.json")).StatusCode);
    }
}
