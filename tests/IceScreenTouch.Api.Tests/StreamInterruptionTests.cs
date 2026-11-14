// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Text;
using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

/// <summary>
/// Nothing that puts a finger or key down reaches the device once the user has taken over, even though the user's token reaches
/// the session's own token only after a thread-pool hop. The tests fire the user's input from inside a device send (on the
/// sending thread, so the moment is exact) and look at every report after it.
/// </summary>
public class StreamInterruptionTests : StreamTestBase
{
    private static byte[] PaddedPing(int bytes)
    {
        const string ping = """{ "op": "ping" }""";
        return Encoding.UTF8.GetBytes(ping + new string(' ', bytes - ping.Length));
    }

    [Fact]
    public async Task AStreamSendsNoDownAfterTheUserTookOverEvenWithMessagesAlreadyQueued()
    {
        var client = await OpenAsync();
        Touch.OnSend = n =>
        {
            if (n == 3)
            {
                Host.Lease!.SimulateUserInput();
            }
        };
        try
        {
            for (var i = 1; i <= 100; i++)
            {
                await DownAsync(client, i, i % 2 == 0 ? 0.3 : 0.6, 0.5); // a down is never held back or coalesced
            }
        }
        catch (WebSocketException)
        {
            // The server already ended the stream: expected, the rest of the burst has nowhere to go.
        }

        await client.WaitForInterruptedAsync("user");
        await client.WaitEndedAsync();
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        var frames = Touch.Frames;
        Assert.Equal(3, frames.Count(f => f.IsDown)); // the third one was in flight when the user acted
        Assert.All(frames.Skip(3), f => Assert.False(f.IsDown));
        Assert.True(frames[^1].IsEmpty);
        Assert.True(Host.Journal.IndexOf("touch:lift") < Host.Journal.IndexOf("lease:disposed"));
    }

    [Fact]
    public async Task AStreamSendsNoKeyDownAfterTheUserTookOver()
    {
        var client = await OpenAsync();
        Keys.OnSend = n =>
        {
            if (n == 3)
            {
                Host.Lease!.SimulateUserInput();
            }
        };

        try
        {
            for (var i = 1; i <= 100; i += 2)
            {
                await KeyAsync(client, i, "a", down: true);
                await KeyAsync(client, i + 1, "a", down: false);
            }
        }
        catch (WebSocketException)
        {
            // The server already ended the stream: expected.
        }

        await client.WaitForInterruptedAsync("user");
        await client.WaitEndedAsync();
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        Assert.All(Keys.Reports.Skip(3), r => Assert.True(r.IsRelease));
        Assert.True(Keys.Reports[^1].IsRelease);
    }

    [Fact]
    public async Task AKeyMessageDoesNotSendItsKeyReportAfterItsModifierReportWhenTheUserActedInBetween()
    {
        var client = await OpenAsync();
        Keys.OnSend = n =>
        {
            if (n == 1)
            {
                Host.Lease!.SimulateUserInput(); // the user acts as the modifier report goes out
            }
        };

        await KeyAsync(client, 1, "a", down: true, modifiers: "\"cmd\"");

        await client.WaitForInterruptedAsync("user");
        await client.WaitEndedAsync();
        Assert.Equal(2, Keys.Reports.Count); // the modifier, then the release: never the key itself
        Assert.Empty(Keys.Reports[0].Keys);
        Assert.True(Keys.Reports[1].IsRelease);
    }

    [Fact]
    public async Task AFlushAndTheDeadManDoNothingOnceTheUserTookOver()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        await MoveAsync(client, 2, 0.5, 0.6); // held back for the flush
        await BarrierAsync(client);
        Assert.Single(Touch.Frames);

        Host.Lease!.SimulateUserInput();
        Advance(16); // the flush is due now

        await client.WaitForInterruptedAsync("user");
        await client.WaitEndedAsync();
        Assert.Single(Touch.Frames, f => f.IsDown); // the held move was never sent
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task ARestSwipeSendsNoDownAfterTheUserTookOver()
    {
        Touch.OnSend = n =>
        {
            if (n == 3)
            {
                Host.Lease!.SimulateUserInput();
            }
        };

        var response = await PostDriven("/v1/touch/swipe", """{ "from": { "x": 0.5, "y": 0.8 }, "to": { "x": 0.5, "y": 0.2 }, "durationMs": 2000 }""");

        await AssertProblem(response, ApiErrorCodes.UserInterrupted);
        var frames = Touch.Frames;
        Assert.Equal(3, frames.Count(f => f.IsDown));
        Assert.All(frames.Skip(3), f => Assert.False(f.IsDown));
        Assert.True(frames[^1].IsEmpty);
    }

    [Fact]
    public async Task ARestTypeSendsNoKeyDownAfterTheUserTookOver()
    {
        Keys.OnSend = n =>
        {
            if (n == 3)
            {
                Host.Lease!.SimulateUserInput();
            }
        };

        var response = await PostDriven("/v1/keys/type", """{ "text": "abcdefghij" }""");

        await AssertProblem(response, ApiErrorCodes.UserInterrupted);
        Assert.All(Keys.Reports.Skip(3), r => Assert.True(r.IsRelease));
        Assert.True(Keys.Reports[^1].IsRelease);
    }

    // ---- Upgrade slots, fragments, the token race --------------------------------------------------------------------

    [Fact]
    public async Task AFifthConcurrentUpgradeIsRefusedAsBusyNotAsAnInternalError()
    {
        var clients = new List<StreamClient>();
        for (var i = 0; i < ApiStreamEndpoint.MaxStreams; i++)
        {
            clients.Add(await ConnectAsync());
        }

        var error = await Assert.ThrowsAsync<WebSocketException>(async () =>
        {
            using var socket = new ClientWebSocket();
            socket.Options.Proxy = null;
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}/v1/stream"), CancellationToken.None);
        });

        Assert.Contains("409", error.Message, StringComparison.Ordinal);
        Assert.Contains(ServerLog, l => l.StartsWith("[api] stream rejected: busy", StringComparison.Ordinal));
        Assert.DoesNotContain(ServerLog, l => l.Contains("internal_error", StringComparison.Ordinal));
        Assert.Equal(ApiStreamEndpoint.MaxStreams, Server.OpenStreams);

        // A slot that frees up is usable again.
        clients[0].Abort();
        await WaitUntil(() => Server.OpenStreams < ApiStreamEndpoint.MaxStreams, "a slot to free up");
        var again = await OpenAsync();
        Assert.False(again.Ended);
    }

    [Fact]
    public async Task OnlyStreamsWithAnOlderTokenThanTheNewestGenerationAreClosed()
    {
        await Server.SetTokenAsync(TestTokens.Two); // generation 1
        var client = await OpenAsync(TestTokens.Two);

        await Server.Streams.CloseStaleAsync(1); // a late close for generation 1 (the race of two regenerations)
        await BarrierAsync(client);
        Assert.False(client.Ended);

        await Server.Streams.CloseStaleAsync(2);

        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
    }

    [Fact]
    public async Task ATwoRegenerationsInARowLeaveNoStreamOpenOnAnOldToken()
    {
        var client = await OpenAsync();

        var first = Server.SetTokenAsync(TestTokens.One);
        var second = Server.SetTokenAsync(TestTokens.Two);
        await Task.WhenAll(first, second);
        var current = Server.Token;

        await client.WaitEndedAsync();
        var fresh = await OpenAsync(current);
        Assert.False(fresh.Ended);
    }

    [Fact]
    public async Task AMessageOfFourKilobytesInSeveralFramesIsAccepted()
    {
        var client = await OpenAsync();
        var ping = PaddedPing(4096);

        await client.SendFragmentsAsync(ping[..1000], ping[1000..3000], ping[3000..]);

        await client.WaitForEventAsync("pong");
        Assert.False(client.Ended);
    }

    [Fact]
    public async Task AMessageOverFourKilobytesInSeveralFramesClosesWith1009()
    {
        var client = await OpenAsync();
        var ping = PaddedPing(4097);

        await client.SendFragmentsAsync(ping[..1000], ping[1000..3000], ping[3000..]);

        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.MessageTooBig, client.CloseStatus);
        Assert.Equal(0, client.Count("pong"));
    }
}
