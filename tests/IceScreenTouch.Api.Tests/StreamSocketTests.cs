// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

/// <summary>A scripted WebSocket: the test queues what the client "sends" and chooses how the server's sends and close behave.</summary>
internal sealed class FakeWebSocket : WebSocket
{
    private readonly Channel<(byte[] Data, WebSocketMessageType Type, bool End)> _incoming = Channel.CreateUnbounded<(byte[], WebSocketMessageType, bool)>();
    private readonly CancellationTokenSource _abort = new();
    private volatile WebSocketState _state = WebSocketState.Open;

    /// <summary>Sends never finish (until cancelled, which aborts the connection like the real socket).</summary>
    public volatile bool BlockSends;

    public SyncList<string> Sent { get; } = [];

    public volatile bool Aborted;

    public WebSocketCloseStatus? ClosedWith { get; private set; }

    public override WebSocketCloseStatus? CloseStatus => ClosedWith;

    public override string? CloseStatusDescription => null;

    public override WebSocketState State => _state;

    public override string? SubProtocol => null;

    public void Enqueue(string text, bool end = true) => _incoming.Writer.TryWrite((Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, end));

    public void EnqueueBytes(byte[] data, bool end) => _incoming.Writer.TryWrite((data, WebSocketMessageType.Text, end));

    public override async Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _abort.Token);
        try
        {
            var item = await _incoming.Reader.ReadAsync(linked.Token);
            item.Data.CopyTo(buffer.AsSpan());
            return new WebSocketReceiveResult(item.Data.Length, item.Type, item.End);
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely);
        }
    }

    public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
    {
        if (BlockSends)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Abort();
                throw;
            }
        }

        Sent.Add(Encoding.UTF8.GetString(buffer));
    }

    public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
    {
        ClosedWith = closeStatus;
        _state = WebSocketState.CloseSent; // and the client never answers
        return Task.CompletedTask;
    }

    public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
        CloseOutputAsync(closeStatus, statusDescription, cancellationToken);

    public override void Abort()
    {
        Aborted = true;
        _state = WebSocketState.Aborted;
        _abort.Cancel();
    }

    public override void Dispose()
    {
    }
}

/// <summary>The stream against a scripted socket: what a real client cannot be made to do (never read, never answer a close, spin with empty fragments).</summary>
public class StreamSocketTests : ApiTestBase
{
    private sealed class Registry : IStreamRegistry
    {
        public int CurrentGeneration => 0;

        public int? Authenticate(string? token) => token == TestTokens.Main ? 0 : null;
    }

    private (FakeWebSocket Socket, Task Run) Start(TimeSpan? sendTimeout = null, TimeSpan? closeGrace = null)
    {
        var socket = new FakeWebSocket();
        var stream = new ApiStream(socket, new Registry(), Controller, Time, Log.Add, new RejectionLog(Log.Add, Time), CancellationToken.None, sendTimeout, closeGrace);
        return (socket, Task.Run(stream.RunAsync));
    }

    private static string Auth => $$"""{ "op": "auth", "token": "{{TestTokens.Main}}" }""";

    private static async Task Finished(Task run) => await run.WaitAsync(TimeSpan.FromSeconds(15));

    private async Task<(FakeWebSocket Socket, Task Run)> OpenedAsync(TimeSpan? sendTimeout = null, TimeSpan? closeGrace = null)
    {
        var (socket, run) = Start(sendTimeout, closeGrace);
        socket.Enqueue(Auth);
        await Eventually(() => socket.Sent.Any(s => s.Contains("\"ready\"", StringComparison.Ordinal)), "ready");
        return (socket, run);
    }

    [Fact]
    public async Task AClientThatNeverReadsEndsTheStreamAfterTheSendTimeoutAndReleasesTheFinger()
    {
        var (socket, run) = await OpenedAsync(sendTimeout: TimeSpan.FromMilliseconds(100));
        socket.Enqueue("""{ "op": "down", "seq": 1, "contacts": [{ "id": 0, "x": 0.5, "y": 0.5 }] }""");
        await Eventually(() => socket.Sent.Any(s => s.Contains("\"ack\"", StringComparison.Ordinal)), "the ack");

        socket.BlockSends = true;
        socket.Enqueue("""{ "op": "ping" }""");

        await Finished(run);
        Assert.True(socket.Aborted);
        Assert.True(Touch.Frames[^1].IsEmpty);
        Assert.True(Touch.Frames[^2].IsLift);
        Assert.True(Host.Lease!.Disposed);
        Assert.True(Host.Journal.IndexOf("touch:empty") < Host.Journal.IndexOf("lease:disposed"));
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task AClientThatNeverAnswersTheCloseIsDroppedAfterTheGrace()
    {
        var (socket, run) = await OpenedAsync(closeGrace: TimeSpan.FromMilliseconds(100));
        socket.Enqueue("""{ "op": "down", "seq": 1, "contacts": [{ "id": 0, "x": 0.5, "y": 0.5 }] }""");
        await Eventually(() => socket.Sent.Any(s => s.Contains("\"ack\"", StringComparison.Ordinal)), "the ack");

        Host.Lease!.SimulateUserInput();

        await Finished(run);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, socket.ClosedWith);
        Assert.True(socket.Aborted); // the connection was dropped, the close frame was not answered
        Assert.Contains(socket.Sent, s => s.Contains("\"interrupted\"", StringComparison.Ordinal));
        Assert.True(Touch.Frames[^1].IsEmpty);
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task ManyEmptyContinuationFragmentsAreCountedAndEndTheStream()
    {
        var (socket, run) = await OpenedAsync();

        for (var i = 0; i < ApiStream.MaxMessagesPerSecond + 50; i++)
        {
            socket.EnqueueBytes([], end: false);
        }

        await Finished(run);
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.ClosedWith);
        Assert.Contains(socket.Sent, s => s.Contains("rate_limited", StringComparison.Ordinal));
        Assert.False(Controller.IsOperating);
    }

    [Fact]
    public async Task AStreamStuckInADeviceWriteIsEndedByCancellingTheSession()
    {
        var (socket, run) = await OpenedAsync(closeGrace: TimeSpan.FromMilliseconds(100));
        Touch.HangNextSend = true;
        socket.Enqueue("""{ "op": "down", "seq": 1, "contacts": [{ "id": 0, "x": 0.5, "y": 0.5 }] }""");
        await Eventually(() => !Touch.HangNextSend, "the write to hang");
        await Task.Delay(50);
        Assert.False(run.IsCompleted);

        Controller.CancelCurrent(); // what a server stop does when a stream does not end

        await Finished(run);
        await Eventually(() => !Controller.IsOperating, "the gate to open");
        Assert.Contains(socket.Sent, s => s.Contains("\"interrupted\"", StringComparison.Ordinal));
        Assert.True(Host.Lease!.Disposed);
    }

    [Fact]
    public async Task ADeviceWriteThatNeverReturnsIsGivenUpAfterTheSendTimeout()
    {
        var (socket, run) = await OpenedAsync(sendTimeout: TimeSpan.FromMilliseconds(150));
        Touch.HangNextSend = true;

        socket.Enqueue("""{ "op": "down", "seq": 1, "contacts": [{ "id": 0, "x": 0.5, "y": 0.5 }] }""");

        await Eventually(() => socket.Sent.Any(s => s.Contains("internal_error", StringComparison.Ordinal)), "the error");
        await Eventually(() => !Controller.IsOperating, "the gate to open");
        Assert.Equal(WebSocketCloseStatus.InternalServerError, socket.ClosedWith);
        Assert.True(Host.Lease!.Disposed);
        socket.Abort();
        await Finished(run);
    }
}
