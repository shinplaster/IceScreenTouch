// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>Controller errors as problem+json, unexpected exceptions as a generic 500, client abort.</summary>
public class ServerErrorTests : ServerTestBase
{
    private const string Tap = """{ "x": 0.5, "y": 0.5, "holdMs": 70 }""";

    [Fact]
    public async Task PurchaseRequiredIs403()
    {
        Allowed = false;

        await AssertProblem(await GetAsync("/v1/status"), ApiErrorCodes.PurchaseRequired);
        await AssertProblem(await PostAsync("/v1/touch/tap", Tap), ApiErrorCodes.PurchaseRequired);
        await AssertProblem(await GetAsync("/v1/rotation"), ApiErrorCodes.PurchaseRequired);
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task NotRunningNotConnectedAndNoPictureAre409()
    {
        Host.Running = false;
        await AssertProblem(await PostAsync("/v1/touch/tap", Tap), ApiErrorCodes.NotRunning);
        Host.Running = true;

        Host.TouchSink.IsSubscribed = false;
        await AssertProblem(await PostAsync("/v1/touch/tap", Tap), ApiErrorCodes.NotConnected);
        Host.TouchSink.IsSubscribed = true;

        Host.Picture = null;
        await AssertProblem(await PostAsync("/v1/touch/tap", Tap), ApiErrorCodes.NoPicture);
    }

    [Fact]
    public async Task UserActiveIs409()
    {
        Host.UserJustActed(TimeSpan.FromMilliseconds(100));

        var problem = await AssertProblem(await PostAsync("/v1/touch/tap", Tap), ApiErrorCodes.UserActive);

        Assert.Equal("Conflict", problem.GetProperty("title").GetString());
        Assert.Empty(Touch.Frames);
    }

    [Fact]
    public async Task ASecondOperationWhileOneRunsIsBusy()
    {
        var running = PostAsync("/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 5000 }""");
        await WaitUntil(() => Touch.Downs.Any(), "the finger going down");

        await AssertProblem(await PostAsync("/v1/touch/tap", Tap), ApiErrorCodes.Busy);
        await AssertProblem(await PostAsync("/v1/home"), ApiErrorCodes.Busy);
        using (var status = await ReadJson(await GetAsync("/v1/status"))) // status is not an operation
        {
            Assert.True(status.RootElement.GetProperty("operating").GetBoolean());
        }

        Assert.True(Controller.CancelCurrent());
        await AssertProblem(await running, ApiErrorCodes.Cancelled);
    }

    [Fact]
    public async Task UserInputStopsAnOperationWithUserInterrupted()
    {
        var running = PostAsync("/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 5000 }""");
        await WaitUntil(() => Touch.Downs.Any(), "the finger going down");

        Host.Lease!.SimulateUserInput();

        await AssertProblem(await running, ApiErrorCodes.UserInterrupted);
        Assert.True(Touch.Frames[^1].IsLift || Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task APictureChangedIs409()
    {
        // A landscape screenshot's size against the portrait picture.
        var response = await PostAsync("/v1/touch/tap", """{ "x": 10, "y": 10, "space": "pixel", "ref": { "width": 2000, "height": 1000 } }""");

        await AssertProblem(response, ApiErrorCodes.PictureChanged);
    }

    [Fact]
    public async Task NoKeyboardAndNoHomeButtonAre409()
    {
        Host.KeyboardSink.HasKeyboard = false;
        Host.HomeSink.HasHomeButton = false;

        await AssertProblem(await PostAsync("/v1/keys/press", """{ "key": "a" }"""), ApiErrorCodes.NoKeyboard);
        await AssertProblem(await PostAsync("/v1/home"), ApiErrorCodes.NoHomeButton);
    }

    [Fact]
    public async Task AnUnexpectedExceptionIsAGeneric500WithoutTheMessage()
    {
        var throwing = new ThrowingHost(Host);
        using var controller = new ApiController(throwing, () => true, Time, Log.Add);
        await using var server = new ApiServer(controller, TestToken, port: 0, log: null, time: Time);
        Assert.True((await server.StartAsync()).IsSuccess);

        var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{server.Port}/v1/touch/tap")
        {
            Content = Json(Tap),
            Headers = { { "Authorization", $"Bearer {TestToken}" } },
        });
        var status = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{server.Port}/v1/status")
        {
            Headers = { { "Authorization", $"Bearer {TestToken}" } },
        });

        foreach (var r in new[] { response, status })
        {
            var text = await r.Content.ReadAsStringAsync();
            var problem = AssertProblemFrom(r, text);
            Assert.Equal("Internal Server Error", problem.GetProperty("title").GetString());
            Assert.DoesNotContain("SECRET-LINK-DETAIL", text, StringComparison.Ordinal);
            Assert.DoesNotContain("InvalidOperationException", text, StringComparison.Ordinal);
            Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Exception", text, StringComparison.Ordinal);
        }

        Assert.False(controller.IsOperating);
        Assert.DoesNotContain(Log, line => line.Contains("SECRET-LINK-DETAIL", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AClientThatGoesAwayCancelsTheOperationAndLifts()
    {
        using var cts = new CancellationTokenSource();
        var running = SendAsync(HttpMethod.Post, "/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 5000 }""", cancellationToken: cts.Token);
        await WaitUntil(() => Touch.Downs.Any(), "the finger going down");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);

        await WaitUntil(() => !Controller.IsOperating, "the operation having ended");
        Assert.True(Touch.Frames[^1].IsLift || Touch.Frames[^1].IsEmpty);
        Assert.False(Controller.IsOperating);
        // And the server still works.
        Assert.Equal(200, (int)(await PostDriven("/v1/touch/tap", Tap)).StatusCode);
    }

    private static System.Text.Json.JsonElement AssertProblemFrom(HttpResponseMessage response, string text)
    {
        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();
        Assert.Equal(ApiErrorCodes.InternalError, root.GetProperty("code").GetString());
        Assert.Equal(500, root.GetProperty("status").GetInt32());
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        return root;
    }

    /// <summary>A host whose touch link fails and whose status cannot be read, as a broken BLE write would.</summary>
    private sealed class ThrowingHost(FakeHost inner) : IApiHost
    {
        public ITouchSink Touch { get; } = new ThrowingTouch();

        public IKeyboardSink Keyboard => inner.Keyboard;

        public IHomeButtonSink HomeButton => inner.HomeButton;

        public ApiHostSnapshot GetSnapshot() => inner.GetSnapshot();

        public ApiStatus GetStatus() => throw new InvalidOperationException("SECRET-LINK-DETAIL");

        public ValueTask<IInputLease> AcquireInputAsync(CancellationToken cancellationToken) => inner.AcquireInputAsync(cancellationToken);

        public void SetRotation(Rotation? landscape, Rotation? portrait) => inner.SetRotation(landscape, portrait);

        public bool ChooseTarget(string? deviceId) => inner.ChooseTarget(deviceId);
    }

    private sealed class ThrowingTouch : ITouchSink
    {
        public bool IsSubscribed => true;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("SECRET-LINK-DETAIL");
    }
}
