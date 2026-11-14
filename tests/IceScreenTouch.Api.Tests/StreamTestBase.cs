// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>A WebSocket client of <c>/v1/stream</c> that collects every event the server sends.</summary>
internal sealed class StreamClient : IDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly SyncList<JsonElement> _events = [];
    private Task _pump = Task.CompletedTask;
    private volatile int _closeStatus = -1;
    private volatile string? _closeDescription;
    private volatile bool _ended;

    private StreamClient()
    {
    }

    public IReadOnlyList<JsonElement> Events => _events;

    /// <summary>The close status the server sent (null until it did).</summary>
    public WebSocketCloseStatus? CloseStatus => _closeStatus < 0 ? null : (WebSocketCloseStatus)_closeStatus;

    public string? CloseDescription => _closeDescription;

    public bool Ended => _ended;

    public static async Task<StreamClient> ConnectAsync(int port, Action<ClientWebSocketOptions>? tweak = null)
    {
        var client = new StreamClient();
        client._socket.Options.Proxy = null;
        tweak?.Invoke(client._socket.Options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await client._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/stream"), timeout.Token);
        client._pump = Task.Run(client.PumpAsync);
        return client;
    }

    public Task SendAsync(string json) => SendRawAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text);

    public async Task SendRawAsync(byte[] bytes, WebSocketMessageType type)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _socket.SendAsync(new ArraySegment<byte>(bytes), type, true, timeout.Token);
    }

    /// <summary>Sends one text message as several frames (every part but the last is a non-final fragment).</summary>
    public async Task SendFragmentsAsync(params byte[][] parts)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        for (var i = 0; i < parts.Length; i++)
        {
            await _socket.SendAsync(new ArraySegment<byte>(parts[i]), WebSocketMessageType.Text, i == parts.Length - 1, timeout.Token);
        }
    }

    /// <summary>Closes like a client (a close frame, then waits for the server's).</summary>
    public async Task CloseAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token);
        await _pump.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>Drops the connection without a close frame.</summary>
    public void Abort() => _socket.Abort();

    public async Task WaitEndedAsync()
    {
        await _pump.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>The first event (of all received so far, or arriving) that satisfies <paramref name="match"/>.</summary>
    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> match, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            foreach (var e in _events)
            {
                if (match(e))
                {
                    return e;
                }
            }

            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}; got: {string.Join(" ", _events.Select(e => e.GetRawText()))}");
            await Task.Delay(1);
        }
    }

    public Task<JsonElement> WaitForEventAsync(string ev) => WaitForAsync(e => IsEvent(e, ev), $"event {ev}");

    public Task<JsonElement> WaitForAckAsync(int seq) => WaitForAsync(e => IsEvent(e, "ack") && e.GetProperty("seq").GetInt32() == seq, $"ack {seq}");

    public Task<JsonElement> WaitForErrorAsync(string code) => WaitForAsync(e => IsEvent(e, "error") && e.GetProperty("code").GetString() == code, $"error {code}");

    public Task<JsonElement> WaitForInterruptedAsync(string reason) => WaitForAsync(e => IsEvent(e, "interrupted") && e.GetProperty("reason").GetString() == reason, $"interrupted {reason}");

    public int Count(string ev) => _events.Count(e => IsEvent(e, ev));

    public static bool IsEvent(JsonElement e, string ev) => e.TryGetProperty("ev", out var v) && v.GetString() == ev;

    public void Dispose()
    {
        _socket.Abort();
        _socket.Dispose();
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var total = 0;
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer, total, buffer.Length - total), CancellationToken.None);
                    total += result.Count;
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _closeDescription = result.CloseStatusDescription;
                    _closeStatus = (int)(result.CloseStatus ?? WebSocketCloseStatus.Empty);
                    try
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    }
                    catch (WebSocketException)
                    {
                        // The server already dropped the connection.
                    }

                    return;
                }

                _events.Add(JsonDocument.Parse(buffer.AsMemory(0, total)).RootElement.Clone());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // The connection was dropped (a test aborted it, or the server dropped it).
        }
        finally
        {
            _ended = true;
        }
    }
}

/// <summary>The server with a real WebSocket client in front of the fakes; the clock is the fake one.</summary>
public abstract class StreamTestBase : ServerTestBase
{
    private readonly List<StreamClient> _clients = [];

    // Portrait picture, rotation 0: the screen position is the digitizer position.
    internal static AxisPoint At(double x, double y) => Expected(x, y, Rotation.R0);

    internal async Task<StreamClient> ConnectAsync(Action<System.Net.WebSockets.ClientWebSocketOptions>? tweak = null)
    {
        var client = await StreamClient.ConnectAsync(Port, tweak);
        _clients.Add(client);
        await WaitUntil(() => Server.OpenStreams > 0, "the server to know the stream");
        return client;
    }

    internal async Task<StreamClient> OpenAsync(string token = TestToken)
    {
        var client = await ConnectAsync();
        await client.SendAsync($$"""{ "op": "auth", "token": "{{token}}" }""");
        await client.WaitForEventAsync("ready");
        return client;
    }

    /// <summary>Waits until the server has processed everything sent before (a ping is answered in order).</summary>
    internal static async Task BarrierAsync(StreamClient client)
    {
        var before = client.Count("pong");
        await client.SendAsync("""{ "op": "ping" }""");
        await WaitUntil(() => client.Count("pong") > before, "the pong");
    }

    internal static Task DownAsync(StreamClient client, int seq, double x, double y, int id = 0) =>
        client.SendAsync($$"""{ "op": "down", "seq": {{seq}}, "space": "screen", "contacts": [{ "id": {{id}}, "x": {{F(x)}}, "y": {{F(y)}} }] }""");

    internal static Task MoveAsync(StreamClient client, int seq, double x, double y, int id = 0) =>
        client.SendAsync($$"""{ "op": "move", "seq": {{seq}}, "contacts": [{ "id": {{id}}, "x": {{F(x)}}, "y": {{F(y)}} }] }""");

    internal static Task UpAsync(StreamClient client, int seq) => client.SendAsync($$"""{ "op": "up", "seq": {{seq}} }""");

    internal static Task KeyAsync(StreamClient client, int seq, string key, bool down, string modifiers = "") =>
        client.SendAsync($$"""{ "op": "key", "seq": {{seq}}, "key": "{{key}}", "modifiers": [{{modifiers}}], "down": {{(down ? "true" : "false")}} }""");

    internal static string F(double v) => v.ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);

    public override async Task DisposeAsync()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }

        await base.DisposeAsync();
    }

    protected void Advance(double milliseconds) => Time.AdvanceInline(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>The touch frames after <paramref name="skip"/>.</summary>
    internal List<Frame> FramesAfter(int skip) => [.. Touch.Frames.Skip(skip)];
}
