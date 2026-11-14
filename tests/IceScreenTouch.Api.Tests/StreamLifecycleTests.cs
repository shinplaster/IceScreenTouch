// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>How a stream ends or goes quiet: the dead-man, interruptions, disconnects, a new token and a stopping server.</summary>
public class StreamLifecycleTests : StreamTestBase
{
    /// <summary>Everything is lifted and released, and only then does the input go back.</summary>
    private void AssertReleasedBeforeTheInputWentBack()
    {
        var journal = Host.Journal;
        var disposed = journal.IndexOf("lease:disposed");
        Assert.True(disposed >= 0, "the lease was disposed");
        Assert.True(Touch.Frames.Count == 0 || Touch.Frames[^1].IsEmpty, "the last touch report is the empty one");
        Assert.True(Keys.Reports.Count == 0 || Keys.Reports[^1].IsRelease, "the last key report is the release");
        for (var i = 0; i < journal.Count; i++)
        {
            if (journal[i] is "touch:lift" or "touch:empty" or "key:release")
            {
                Assert.True(i < disposed, $"{journal[i]} came before the lease was disposed");
            }
        }

        Assert.DoesNotContain(journal.Skip(disposed + 1), e => e.StartsWith("touch:", StringComparison.Ordinal) || e.StartsWith("key:", StringComparison.Ordinal));
    }

    // ---- Dead-man -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASilentStreamWithAFingerDownReleasesAfterOneSecondAndStaysOpen()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);

        Advance(999);
        await Task.Delay(50);
        Assert.Single(Touch.Frames);
        Advance(1);

        var idle = await client.WaitForInterruptedAsync("idle");
        Assert.Equal(2, idle.EnumerateObject().Count());
        var frames = Touch.Frames;
        Assert.Equal(3, frames.Count);
        Assert.True(frames[1].IsLift);
        Assert.Equal(new AxisPoint(frames[0].Contacts[0].X, frames[0].Contacts[0].Y), frames[1].First);
        Assert.True(frames[2].IsEmpty);
        Assert.False(client.Ended);
        Assert.True(Controller.IsOperating); // the session stays open

        await DownAsync(client, 2, 0.4, 0.4); // and works again
        await client.WaitForAckAsync(2);
        Assert.Equal(4, Touch.Frames.Count);
    }

    [Fact]
    public async Task ASilentStreamWithAKeyDownReleasesTheKey()
    {
        var client = await OpenAsync();
        await KeyAsync(client, 1, "a", down: true, modifiers: "\"cmd\"");
        await client.WaitForAckAsync(1);

        Advance(1000);

        await client.WaitForInterruptedAsync("idle");
        Assert.True(Keys.Reports[^1].IsRelease);
        Assert.False(client.Ended);
    }

    [Fact]
    public async Task AFingerAndAKeyAreBothReleasedByTheDeadMan()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await KeyAsync(client, 2, "a", down: true);
        await client.WaitForAckAsync(2);

        Advance(1000);

        await client.WaitForInterruptedAsync("idle");
        Assert.True(Touch.Frames[^1].IsEmpty);
        Assert.True(Keys.Reports[^1].IsRelease);
        Assert.Equal(1, client.Count("interrupted"));
    }

    [Fact]
    public async Task APingKeepsTheFingerDown()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        Advance(600);
        await BarrierAsync(client); // the ping at 600 ms

        Advance(400); // 1000 ms after the down, 400 ms after the ping
        Advance(200);
        await Task.Delay(100);

        Assert.Equal(0, client.Count("interrupted"));
        Assert.Single(Touch.Frames);

        Advance(400); // 1000 ms after the ping
        await client.WaitForInterruptedAsync("idle");
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task AMoveKeepsTheFingerDownToo()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        Advance(900);
        await MoveAsync(client, 2, 0.5, 0.6);
        await client.WaitForAckAsync(2);

        Advance(200);
        await Task.Delay(100);

        Assert.Equal(0, client.Count("interrupted"));
    }

    [Fact]
    public async Task ASilentStreamWithNothingDownStaysQuiet()
    {
        var client = await OpenAsync();

        Advance(10_000);
        await Task.Delay(100);

        Assert.Equal(0, client.Count("interrupted"));
        Assert.False(client.Ended);
        Assert.Empty(Touch.Frames);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task AnUpBeforeTheDeadlineDisarmsTheDeadMan()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await UpAsync(client, 2);
        await client.WaitForAckAsync(2);

        Advance(5000);
        await Task.Delay(100);

        Assert.Equal(0, client.Count("interrupted"));
        Assert.Equal(3, Touch.Frames.Count);
    }

    // ---- Interruptions ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheUserTakingOverReleasesTellsTheClientAndClosesNormally()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await KeyAsync(client, 2, "a", down: true);
        await client.WaitForAckAsync(2);

        Host.Lease!.SimulateUserInput();

        await client.WaitForInterruptedAsync("user");
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        AssertReleasedBeforeTheInputWentBack();
        Assert.Contains(ServerLog, l => l == "[api] stream closed (user)");
    }

    [Fact]
    public async Task APostCancelEndsTheStreamAsCancelled()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);

        var cancel = await PostAsync("/v1/cancel");

        Assert.Equal(200, (int)cancel.StatusCode);
        using var doc = await ReadJson(cancel);
        Assert.True(doc.RootElement.GetProperty("cancelled").GetBoolean());
        await client.WaitForInterruptedAsync("cancelled");
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        AssertReleasedBeforeTheInputWentBack();
    }

    [Fact]
    public async Task TheEndOfTheMirroringSessionEndsTheStreamAsSessionEnded()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);

        Host.Lease!.SimulateSessionEnd();

        await client.WaitForInterruptedAsync("session_ended");
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        AssertReleasedBeforeTheInputWentBack();
    }

    [Fact]
    public async Task AnInterruptedStreamCanBeOpenedAgainAfterTheQuietWindow()
    {
        var client = await OpenAsync();
        Host.Lease!.SimulateUserInput();
        await client.WaitEndedAsync();
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");

        var again = await ConnectAsync();
        await again.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");

        await again.WaitForEventAsync("ready");
        Assert.Equal(2, Host.Leases.Count);
    }

    // ---- Disconnects --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ADroppedConnectionMidGestureReleasesEverything()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.SendAsync("""{ "op": "down", "seq": 2, "contacts": [{ "id": 1, "x": 0.6, "y": 0.6 }] }""");
        await KeyAsync(client, 3, "a", down: true);
        await client.WaitForAckAsync(3);

        client.Abort();

        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        AssertReleasedBeforeTheInputWentBack();
        Assert.True(Touch.Frames[^2].IsLift);
        Assert.Equal(2, Touch.Frames[^2].Contacts.Length);
        Assert.Contains(ServerLog, l => l.StartsWith("[api] stream closed", StringComparison.Ordinal));
        await WaitUntil(() => Server.OpenStreams == 0, "the stream to be forgotten");
    }

    [Fact]
    public async Task ACloseHandshakeMidGestureReleasesEverything()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);

        await client.CloseAsync();

        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        AssertReleasedBeforeTheInputWentBack();
        Assert.Equal(WebSocketCloseStatus.NormalClosure, client.CloseStatus);
    }

    [Fact]
    public async Task ADisconnectDuringTheAuthWaitOpensNoSession()
    {
        var client = await ConnectAsync();

        client.Abort();

        await WaitUntil(() => Server.OpenStreams == 0, "the stream to be forgotten");
        Assert.Empty(Host.Journal);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task AFailingLeaseReleaseStillOpensTheGate()
    {
        Host.LeaseDisposeThrows = true;
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);

        await client.CloseAsync();

        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        Assert.True(Touch.Frames[^1].IsEmpty);
    }

    [Fact]
    public async Task AFailingDeviceWriteEndsTheStreamWithAnInternalError()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await client.WaitForAckAsync(1);
        Host.TouchSink.FailNextSend = true;
        Advance(20);

        await MoveAsync(client, 2, 0.5, 0.6);

        await client.WaitForErrorAsync(ApiErrorCodes.InternalError);
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.InternalServerError, client.CloseStatus);
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        AssertReleasedBeforeTheInputWentBack();
    }

    // ---- The token and the server -------------------------------------------------------------------------------------

    [Fact]
    public async Task RegeneratingTheTokenClosesTheStreamAfterReleasing()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await KeyAsync(client, 2, "a", down: true);
        await client.WaitForAckAsync(2);

        await Server.SetTokenAsync(TestTokens.Two);

        await client.WaitForErrorAsync(ApiErrorCodes.Unauthorized);
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, client.CloseStatus);
        await WaitUntil(() => !Controller.IsOperating, "the gate to open");
        AssertReleasedBeforeTheInputWentBack();
        Assert.Contains(ServerLog, l => l == "[api] stream closed (token changed)");

        // The old token is no good any more, the new one is.
        var old = await ConnectAsync();
        await old.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");
        await old.WaitForErrorAsync(ApiErrorCodes.Unauthorized);
        var fresh = await ConnectAsync();
        await fresh.SendAsync($$"""{ "op": "auth", "token": "{{TestTokens.Two}}" }""");
        await fresh.WaitForEventAsync("ready");
    }

    [Fact]
    public async Task AStreamStillAuthenticatingWhenTheTokenChangesCannotUseTheOldToken()
    {
        var waiting = await ConnectAsync();

        await Server.SetTokenAsync(TestTokens.Two);

        // It has not authenticated yet: the old token no longer opens anything.
        await waiting.SendAsync($$"""{ "op": "auth", "token": "{{TestToken}}" }""");
        await waiting.WaitForErrorAsync(ApiErrorCodes.Unauthorized);
        await waiting.WaitEndedAsync();
        Assert.Empty(Host.Journal);
    }

    [Fact]
    public async Task StoppingTheServerReleasesAndClosesTheStreamWith1001()
    {
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        await KeyAsync(client, 2, "a", down: true);
        await client.WaitForAckAsync(2);

        await Server.StopAsync();

        await client.WaitForInterruptedAsync("cancelled");
        await client.WaitEndedAsync();
        Assert.Equal(WebSocketCloseStatus.EndpointUnavailable, client.CloseStatus);
        Assert.False(Controller.IsOperating);
        AssertReleasedBeforeTheInputWentBack();
        Assert.Contains(ServerLog, l => l == "[api] stream closed (server stopping)");
    }

    [Fact]
    public async Task ManyPingsAndTheDeadManShareTheGateWithoutDeadlock()
    {
        // The dead-man runs on the gate, so a send to a client that does not read must time out; this one reads and answers,
        // and the point here is only that the timers and the reader share the gate without deadlock under load.
        var client = await OpenAsync();
        await DownAsync(client, 1, 0.5, 0.5);
        for (var i = 0; i < 200; i++)
        {
            await client.SendAsync("""{ "op": "ping" }""");
        }

        await WaitUntil(() => client.Count("pong") == 200, "the pongs");
        Advance(1000);

        await client.WaitForInterruptedAsync("idle");
        Assert.True(Touch.Frames[^1].IsEmpty);
    }
}
