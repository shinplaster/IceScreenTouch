// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Text;
using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

/// <summary>The handshake, the first message, the refusals and the message limits of <c>GET /v1/stream</c>.</summary>
public class StreamConnectionTests : StreamTestBase
{
    private static readonly WebSocketCloseStatus TryAgainLater = (WebSocketCloseStatus)1013;

    private HttpRequestMessage UpgradeRequest(Action<HttpRequestMessage>? tweak = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Url("/v1/stream"));
        request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
        request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
        request.Headers.TryAddWithoutValidation("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
        tweak?.Invoke(request);
        return request;
    }

    private void AssertNothingReachedTheDevice()
    {
        Assert.Empty(Touch.Frames);
        Assert.Empty(Keys.Reports);
        Assert.Empty(Host.Journal);
        Assert.False(Controller.IsOperating);
    }

    // ---- Before the handshake ----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnOriginHeaderIsRefusedBeforeTheHandshake()
    {
        var response = await Client.SendAsync(UpgradeRequest(r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.example")));

        await AssertProblem(response, ApiErrorCodes.ForbiddenOrigin);
        Assert.Equal(0, Server.OpenStreams);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task AWebSocketClientWithAnOriginIsRefusedWithAnHttpError()
    {
        var error = await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            using var socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            socket.Options.SetRequestHeader("Origin", "http://evil.example");
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/v1/stream"), CancellationToken.None);
        });

        Assert.Contains("403", error.Message, StringComparison.Ordinal);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task AForeignHostIsRefusedBeforeTheHandshake()
    {
        var response = await Client.SendAsync(UpgradeRequest(r => r.Headers.Host = "evil.example"));

        await AssertProblem(response, ApiErrorCodes.ForbiddenOrigin);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task APlainGetIs400WithoutAToken()
    {
        var response = await SendAsync(HttpMethod.Get, "/v1/stream", token: null);

        await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task OnlyGetIsAllowedOnTheStreamRoute()
    {
        var response = await PostAsync("/v1/stream", "{}");

        await AssertProblem(response, ApiErrorCodes.MethodNotAllowed);
    }

    // ---- The first message -------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoAuthWithinTwoSecondsClosesWith1008()
    {
        var client = await ConnectAsync();

        Advance(1999);
        await Task.Delay(50);
        Assert.False(client.Ended);

        Advance(1);
        await client.WaitForErrorAsync(ApiErrorCodes.Unauthorized);
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
        await WaitUntil(() => Server.OpenStreams == 0, "the stream to be forgotten");
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task AWrongTokenClosesWith1008AndSendsNothingToTheDevice()
    {
        var client = await ConnectAsync();

        await client.SendAsync($$"""{ "op": "auth", "token": "{{TestTokens.One}}" }""");

        await client.WaitForErrorAsync(ApiErrorCodes.Unauthorized);
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
        Assert.Equal(0, client.Count("ready"));
        AssertNothingReachedTheDevice();
    }

    [Theory]
    [InlineData("""{ "op": "ping" }""")]
    [InlineData("""{ "op": "down", "seq": 1, "contacts": [{ "id": 0, "x": 0.5, "y": 0.5 }] }""")]
    [InlineData("not json")]
    [InlineData("""{ "op": "auth" }""")]
    [InlineData("""{ "op": "auth", "token": "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdA", "seq": 1 }""")]
    public async Task AFirstMessageThatIsNotAnAuthClosesWith1008(string first)
    {
        var client = await ConnectAsync();

        await client.SendAsync(first);

        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task ABinaryFirstMessageClosesWith1003()
    {
        var client = await ConnectAsync();

        await client.SendRawAsync([1, 2, 3], WebSocketMessageType.Binary);

        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, client.CloseStatus);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task ALateAuthStillWorksBeforeTheDeadlineAndTheDeadlineThenStopsMattering()
    {
        var client = await ConnectAsync();
        Advance(1900);

        await client.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");
        await client.WaitForEventAsync("ready");
        Advance(10_000);
        await Task.Delay(50);

        Assert.False(client.Ended);
        Assert.True(Controller.IsOperating);
    }

    // ---- Opening -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AValidAuthOpensTheSessionAndSendsReady()
    {
        var client = await OpenAsync();

        var ready = await client.WaitForEventAsync("ready");
        Assert.Equal(1, ready.GetProperty("api").GetInt32());
        Assert.Equal(2, ready.EnumerateObject().Count());
        Assert.True(Controller.IsOperating);
        Assert.Single(Host.Leases);
        Assert.Empty(Touch.Frames);
        Assert.Contains(ServerLog, l => l == "[api] stream opened");
    }

    [Fact]
    public async Task WhileTheStreamIsOpenTheOtherOperationsAreBusyButStatusWorks()
    {
        var client = await OpenAsync();

        await AssertProblem(await PostAsync("/v1/touch/tap", """{ "x": 0.5, "y": 0.5 }"""), ApiErrorCodes.Busy);
        await AssertProblem(await PostAsync("/v1/home"), ApiErrorCodes.Busy);
        var status = await GetAsync("/v1/status");
        Assert.Equal(200, (int)status.StatusCode);
        using var doc = await ReadJson(status);
        Assert.True(doc.RootElement.GetProperty("operating").GetBoolean());
        Assert.Empty(Touch.Frames);
        Assert.Contains(client.Events, e => StreamClient.IsEvent(e, "ready"));
    }

    [Fact]
    public async Task ASecondStreamIsRefusedAsBusyWith1013()
    {
        await OpenAsync();
        var second = await ConnectAsync();

        await second.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");

        await second.WaitForErrorAsync(ApiErrorCodes.Busy);
        await second.WaitEndedAsync();
        Assert.Equal(TryAgainLater, second.CloseStatus);
        Assert.Single(Host.Leases);
        Assert.True(Controller.IsOperating); // the first one still owns it
    }

    [Fact]
    public async Task APurchaseRequiredLicenseClosesWith1008()
    {
        Allowed = false;
        var client = await ConnectAsync();

        await client.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");

        await client.WaitForErrorAsync(ApiErrorCodes.PurchaseRequired);
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task ADeviceThatIsNotConnectedClosesWith1013()
    {
        Touch.IsSubscribed = false;
        var client = await ConnectAsync();

        await client.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");

        await client.WaitForErrorAsync(ApiErrorCodes.NotConnected);
        await client.WaitEndedAsync();
        Assert.Equal(TryAgainLater, client.CloseStatus);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task AUserWhoJustActedClosesWith1013()
    {
        Host.UserJustActed(Ms(100));
        var client = await ConnectAsync();

        await client.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");

        await client.WaitForErrorAsync(ApiErrorCodes.UserActive);
        await client.WaitEndedAsync();
        Assert.Equal(TryAgainLater, client.CloseStatus);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task AStoppedMirroringSessionClosesWith1013()
    {
        Host.Running = false;
        var client = await ConnectAsync();

        await client.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");

        await client.WaitForErrorAsync(ApiErrorCodes.NotRunning);
        await client.WaitEndedAsync();
        Assert.Equal(TryAgainLater, client.CloseStatus);
        AssertNothingReachedTheDevice();
    }

    [Fact]
    public async Task ThePictureIsNotNeededToOpen()
    {
        Host.Picture = null;

        var client = await OpenAsync();

        Assert.False(client.Ended);
        Assert.True(Controller.IsOperating);
    }

    // ---- Closing ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AClientClosingGivesTheInputBackAndOpensTheGate()
    {
        var client = await OpenAsync();

        await client.CloseAsync();

        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        Assert.True(Host.Lease!.Disposed);
        Assert.Contains(ServerLog, l => l == "[api] stream closed (client closed)");
        var tap = await PostDriven("/v1/touch/tap", """{ "x": 0.5, "y": 0.5 }""");
        Assert.Equal(200, (int)tap.StatusCode);
    }

    [Fact]
    public async Task TheLogHasNoTokenCoordinatesOrKeys()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.6543, 0.7321);
        await client.WaitForAckAsync(1);
        await KeyAsync(client, 2, "q", true);
        await client.WaitForAckAsync(2);
        await client.CloseAsync();
        await WaitUntil(() => ServerLog.Any(l => l.StartsWith("[api] stream closed", StringComparison.Ordinal)), "the closing line");

        var all = string.Join('\n', ServerLog.Concat(Log));
        Assert.DoesNotContain(TestToken, all, StringComparison.Ordinal);
        Assert.DoesNotContain("0.6543", all, StringComparison.Ordinal);
        Assert.DoesNotContain("0.7321", all, StringComparison.Ordinal);
        Assert.DoesNotContain("\"q\"", all, StringComparison.Ordinal);
    }

    // ---- Malformed and oversized messages ----------------------------------------------------------------------------

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("""{ "op": "ping", "extra": 1 }""")]
    [InlineData("""{ "op": 5 }""")]
    [InlineData("""{ "seq": 1 }""")]
    [InlineData("""{ "op": "down", "seq": 1, "contacts": [{ "id": 0, "x": "0.5", "y": 0.5 }] }""")]
    public async Task AMalformedMessageIsAnErrorAndTheSessionStays(string message)
    {
        var client = await OpenAsync();

        await client.SendAsync(message);

        await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        await BarrierAsync(client);
        Assert.False(client.Ended);
        Assert.True(Controller.IsOperating);
        Assert.Empty(Touch.Frames);
    }

    [Theory]
    [InlineData("""{ "op": "tap", "seq": 3 }""")]
    [InlineData("""{ "op": "auth", "token": "x" }""")]
    [InlineData("""{ "op": "ping", "seq": 3 }""")]
    [InlineData("""{ "op": "up", "seq": 3, "key": "a" }""")]
    [InlineData("""{ "op": "move", "seq": 3, "space": "screen", "contacts": [{ "id": 0, "x": 0.5, "y": 0.5 }] }""")]
    [InlineData("""{ "op": "key", "seq": 3, "contacts": [] }""")]
    public async Task AnUnknownOpOrAMemberTheOpDoesNotTakeIsAnError(string message)
    {
        var client = await OpenAsync();

        await client.SendAsync(message);

        var error = await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.False(string.IsNullOrEmpty(error.GetProperty("detail").GetString()));
        await BarrierAsync(client);
        Assert.False(client.Ended);
        Assert.Empty(Touch.Frames);
    }

    [Theory]
    [InlineData("""{ "op": "up" }""")]
    [InlineData("""{ "op": "up", "seq": -1 }""")]
    [InlineData("""{ "op": "move", "contacts": [{ "id": 0, "x": 0.5, "y": 0.5 }] }""")]
    [InlineData("""{ "op": "key", "key": "a", "down": true }""")]
    public async Task AMessageNeedsItsSeq(string message)
    {
        var client = await OpenAsync();

        await client.SendAsync(message);

        var error = await client.WaitForErrorAsync(ApiErrorCodes.InvalidRequest);
        Assert.False(error.TryGetProperty("seq", out _));
        Assert.Contains("seq", error.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Empty(Touch.Frames);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task AMessageOfExactlyFourKilobytesIsFine()
    {
        var client = await OpenAsync();
        var ping = """{ "op": "ping" }""";
        var padded = ping + new string(' ', 4096 - ping.Length);
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(padded));

        await client.SendAsync(padded);

        await client.WaitForEventAsync("pong");
        Assert.False(client.Ended);
    }

    [Fact]
    public async Task AMessageOverFourKilobytesClosesWith1009()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        var ping = """{ "op": "ping" }""";

        await client.SendAsync(ping + new string(' ', 4097 - ping.Length));

        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, client.CloseStatus);
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        Assert.True(Touch.Frames[^1].IsEmpty);
        Assert.True(Host.Lease!.Disposed);
    }

    [Fact]
    public async Task ABinaryMessageClosesWith1003AndReleases()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);

        await client.SendRawAsync([1, 2, 3], WebSocketMessageType.Binary);

        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, client.CloseStatus);
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task MoreThanAThousandMessagesInASecondCloseTheStream()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);

        for (var i = 0; i < 1001; i++)
        {
            await client.SendAsync("""{ "op": "ping" }""");
        }

        await client.WaitForErrorAsync(ApiErrorCodes.RateLimited);
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task ThousandMessagesPerSecondAreStillFineWhenTheSecondPasses()
    {
        var client = await OpenAsync();

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < 400; i++)
            {
                await client.SendAsync("""{ "op": "ping" }""");
            }

            await WaitUntil(() => client.Count("pong") >= 400 * (round + 1), "the pongs");
            Advance(1000);
        }

        Assert.False(client.Ended);
    }

    [Fact]
    public async Task ThePingIsAnsweredWithAPong()
    {
        var client = await OpenAsync();

        await client.SendAsync("""{ "op": "ping" }""");

        var pong = await client.WaitForEventAsync("pong");
        Assert.Single(pong.EnumerateObject());
    }
}
