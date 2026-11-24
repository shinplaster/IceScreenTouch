// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net.WebSockets;
using System.Numerics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>How a stream ends: what is logged, what the client is told first, and the close frame.</summary>
/// <param name="LogReason">The words of <c>[api] stream closed (...)</c>: never user data.</param>
/// <param name="Status">The WebSocket close status.</param>
/// <param name="Description">The close description (short, no user data).</param>
/// <param name="Event">Sent before the close frame, or null.</param>
/// <param name="SendClose">False when the connection is gone (nothing to send).</param>
internal sealed record StreamEnd(string LogReason, WebSocketCloseStatus Status, string Description, StreamEvent? Event = null, bool SendClose = true)
{
    /// <summary>"Try again later" (RFC 6455 does not list it; the IANA registry has it as 1013).</summary>
    public const WebSocketCloseStatus TryAgainLater = (WebSocketCloseStatus)1013;

    /// <summary>The connection broke or was aborted.</summary>
    public static StreamEnd Disconnected { get; } = new("disconnected", WebSocketCloseStatus.NormalClosure, string.Empty, null, false);

    /// <summary>The client sent a close frame.</summary>
    public static StreamEnd PeerClosed { get; } = new("client closed", WebSocketCloseStatus.NormalClosure, "bye");

    /// <summary>An unexpected failure (a failed BLE write, a bug).</summary>
    public static StreamEnd Internal { get; } = new(
        "internal error",
        WebSocketCloseStatus.InternalServerError,
        "internal error",
        StreamEvent.Error(null, ApiError.Create(ApiErrorCodes.InternalError, "the stream stopped because of an internal error; every finger and key was released")));

    /// <summary>A message larger than <see cref="ApiStream.MaxMessageBytes"/>.</summary>
    public static StreamEnd TooBig { get; } = new("message too large", WebSocketCloseStatus.MessageTooBig, "message too large");

    /// <summary>A binary message.</summary>
    public static StreamEnd Binary { get; } = new("binary message", WebSocketCloseStatus.InvalidMessageType, "text messages only");

    /// <summary>More than <see cref="ApiStream.MaxMessagesPerSecond"/> messages in a second.</summary>
    public static StreamEnd RateLimited { get; } = new(
        "rate limited",
        WebSocketCloseStatus.PolicyViolation,
        "too many messages",
        StreamEvent.Error(null, ApiError.Create(ApiErrorCodes.RateLimited, "too many messages per second")));

    /// <summary>The server is stopping.</summary>
    public static StreamEnd ServerStopping { get; } = new("server stopping", WebSocketCloseStatus.EndpointUnavailable, "the API is shutting down", StreamEvent.Interrupted("cancelled"));

    /// <summary>The token the stream used was replaced.</summary>
    public static StreamEnd TokenChanged { get; } = new(
        "token changed",
        WebSocketCloseStatus.PolicyViolation,
        "the token changed",
        StreamEvent.Error(null, ApiError.Create(ApiErrorCodes.Unauthorized, "the token was regenerated; every finger and key was released")));

    /// <summary>The first message was not a valid <c>auth</c>, or came too late.</summary>
    public static StreamEnd Unauthorized(string logReason, string detail) => new(
        logReason,
        WebSocketCloseStatus.PolicyViolation,
        "unauthorized",
        StreamEvent.Error(null, ApiError.Create(ApiErrorCodes.Unauthorized, detail)));

    /// <summary>The session could not be opened (1008 for the license, 1013 "try again later" for the state, 1011 otherwise).</summary>
    public static StreamEnd Refused(ApiError error) => new(
        $"refused: {error.Code}",
        error.Code switch
        {
            ApiErrorCodes.Unauthorized or ApiErrorCodes.ForbiddenOrigin or ApiErrorCodes.PurchaseRequired => WebSocketCloseStatus.PolicyViolation,
            ApiErrorCodes.Busy or ApiErrorCodes.UserActive or ApiErrorCodes.NotConnected or ApiErrorCodes.NotRunning => TryAgainLater,
            ApiErrorCodes.Cancelled => WebSocketCloseStatus.EndpointUnavailable,
            _ => WebSocketCloseStatus.InternalServerError,
        },
        error.Code,
        StreamEvent.Error(null, error));

    /// <summary>The trial's time for one connection is up (1008, with <c>purchase_required</c> and the reason <c>trial_limit</c> first).</summary>
    public static StreamEnd TrialTimeUp(TimeSpan limit)
    {
        var seconds = (int)Math.Round(limit.TotalSeconds);
        var error = ApiError.Create(ApiErrorCodes.PurchaseRequired, $"the trial allows a stream connection to stay open for {seconds} seconds; every finger and key was released; buy IceScreen Touch for unlimited use")
            with { Extras = new ApiErrorExtras { Reason = ApiErrorReasons.TrialLimit, Limit = seconds, Counter = "stream_seconds" } };
        return new("trial time limit", WebSocketCloseStatus.PolicyViolation, ApiErrorReasons.TrialLimit, StreamEvent.Error(null, error));
    }

    /// <summary>The user, <c>POST /v1/cancel</c> or the end of the mirroring session stopped the stream.</summary>
    public static StreamEnd Interrupted(string reason) => new(reason, WebSocketCloseStatus.NormalClosure, "interrupted", StreamEvent.Interrupted(reason));
}

/// <summary>What the stream needs from the server that owns it.</summary>
internal interface IStreamRegistry
{
    /// <summary>The generation of the token now in force (it grows with every <see cref="ApiServer.SetTokenAsync"/>).</summary>
    int CurrentGeneration { get; }

    /// <summary>Checks a token; returns the generation it belongs to, or null when it is wrong. Constant time.</summary>
    int? Authenticate(string? token);
}

/// <summary>
/// One WebSocket connection of <c>GET /v1/stream</c> (docs/API-DESIGN.md, chapter 8). The first message authenticates (within
/// <see cref="AuthTimeout"/>), then the stream opens an <see cref="ApiSession"/> (so every other operation is <c>busy</c>) and sends
/// fingers and keys to the device as the client says, until the client closes, the user takes over, the operation is cancelled,
/// the mirroring session ends, the token changes or the server stops. Whatever the reason, every finger is lifted and every key
/// released FIRST, then the session is disposed (the bridge gets the input back), then the client is told and the socket closed.
/// </summary>
/// <remarks>
/// Concurrency: the reader loop, the three timers and the interruption callbacks never run the logic themselves; each takes the
/// one gate and then runs, so the state below is touched by one thread at a time and the sends to the socket and the device are
/// in order. A send to the client times out (<see cref="SendTimeout"/>, real time) so a client that never reads cannot hold the
/// gate and keep a finger down. Logging: <c>[api] stream opened</c>, <c>[api] stream closed (reason)</c> and refusals; never a token,
/// position or key.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The gate is a SemaphoreSlim whose wait handle is never used (nothing to release), and the timers and callbacks may still run after the stream ended")]
internal sealed class ApiStream
{
    /// <summary>The largest message (4 KB); a larger one closes the stream with 1009.</summary>
    public const int MaxMessageBytes = 4096;

    /// <summary>More frames (messages and fragments of messages) than this in one second close the stream with 1008 (a runaway client; real use is 60 to 125 a second).</summary>
    public const int MaxMessagesPerSecond = 1000;

    /// <summary>The first message must be <c>auth</c> within this time of the upgrade.</summary>
    public static readonly TimeSpan AuthTimeout = TimeSpan.FromSeconds(2);

    /// <summary>While a finger or key is down, a stream silent for this long releases everything.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(1);

    /// <summary>At most one move report per interval (the same as the gestures of the REST operations).</summary>
    public static readonly TimeSpan MoveInterval = TouchTiming.Default.MoveInterval;

    /// <summary>The longest a send to the client may take (real time).</summary>
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    /// <summary>How long a close waits for the client's close frame before the connection is dropped (real time).</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private static readonly string[] FieldNames = ["token", "seq", "space", "ref", "contacts", "key", "modifiers", "down"];

    private readonly WebSocket _socket;
    private readonly IStreamRegistry _registry;
    private readonly ApiController _controller;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly RejectionLog _rejections;
    private readonly CancellationToken _aborted;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource _endedSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ---- State: touched only while the gate is held ----
    private Phase _phase = Phase.Authenticating;
    private ApiSession? _session;
    private ITimer? _authTimer;
    private ITimer? _flushTimer;
    private ITimer? _idleTimer;
    private ITimer? _limitTimer;
    private CancellationTokenRegistration _interruptRegistration;
    private long _lastActivity;
    private long? _lastTouchSend;
    private bool _flushArmed;
    private bool _idleArmed;
    private int? _pendingSeq;
    private TouchContact[] _wanted = [];
    private string? _space;
    private PixelRef? _ref;
    private readonly List<HidKey> _held = [];
    private byte _reportedModifiers;
    private byte[] _reportedKeys = [];

    private volatile int _generation = -1;

    private readonly TimeSpan _sendTimeout;
    private readonly TimeSpan _closeGrace;
    private CancellationTokenSource? _deviceCts;

    public ApiStream(
        WebSocket socket,
        IStreamRegistry registry,
        ApiController controller,
        TimeProvider time,
        Action<string>? log,
        RejectionLog rejections,
        CancellationToken aborted,
        TimeSpan? sendTimeout = null,
        TimeSpan? closeGrace = null)
    {
        _sendTimeout = sendTimeout ?? SendTimeout;
        _closeGrace = closeGrace ?? CloseGrace;
        _socket = socket;
        _registry = registry;
        _controller = controller;
        _time = time;
        _log = log;
        _rejections = rejections;
        _aborted = aborted;

        // Counted from the upgrade: the timer exists as soon as the stream is known to the server.
        _authTimer = time.CreateTimer(static s => ((ApiStream)s!).Fire(static self => self.AuthTimedOutAsync()), this, AuthTimeout, Timeout.InfiniteTimeSpan);
    }

    private enum Phase
    {
        Authenticating,
        Open,
        Ended,
    }

    [Flags]
    private enum Field
    {
        None = 0,
        Token = 1,
        Seq = 2,
        Space = 4,
        Ref = 8,
        Contacts = 16,
        Key = 32,
        Modifiers = 64,
        Down = 128,
    }

    /// <summary>The generation of the token this stream authenticated with; -1 until it has.</summary>
    public int Generation => _generation;

    /// <summary>Ends the stream (as the server asks): everything is released first. Returns when it has ended.</summary>
    public Task CloseAsync(StreamEnd end) => EndAsync(end);

    /// <summary>Runs the stream until the socket is closed or dropped.</summary>
    public async Task RunAsync()
    {
        var reader = ReadLoopAsync();
        try
        {
            var first = await Task.WhenAny(reader, _endedSignal.Task).ConfigureAwait(false);
            if (first == reader)
            {
                await EndAsync(await reader.ConfigureAwait(false)).ConfigureAwait(false);
                await DrainAsync().ConfigureAwait(false);
                return;
            }

            // Somebody else ended it (the close frame is out): give the client a moment to answer, then drop the connection.
            try
            {
                await reader.WaitAsync(_closeGrace).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _socket.Abort();
                await reader.ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _rejections.Record($"stream failed: internal_error ({ex.GetType().Name})");
            await EndAsync(StreamEnd.Internal).ConfigureAwait(false);
            _socket.Abort();
        }
    }

    // The reader stopped before the client answered our close frame (too large a message, a binary one): read until it does.
    private async Task DrainAsync()
    {
        if (_socket.State != WebSocketState.CloseSent)
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(_closeGrace);
            var scratch = new byte[256];
            while (_socket.State == WebSocketState.CloseSent)
            {
                await _socket.ReceiveAsync(scratch, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            _socket.Abort();
        }
    }

    // ---- Reading ----------------------------------------------------------------------------------------------

    private async Task<StreamEnd> ReadLoopAsync()
    {
        var buffer = new byte[MaxMessageBytes + 1];
        var windowStart = _time.GetTimestamp();
        var inWindow = 0;
        while (true)
        {
            var total = 0;
            while (true)
            {
                ValueWebSocketReceiveResult result;
                try
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(total), _aborted).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
                {
                    return StreamEnd.Disconnected;
                }

                // Every frame counts, empty continuation fragments included, so they cannot be used to spin the loop.
                if (_time.GetElapsedTime(windowStart) >= TimeSpan.FromSeconds(1))
                {
                    windowStart = _time.GetTimestamp();
                    inWindow = 0;
                }

                if (++inWindow > MaxMessagesPerSecond)
                {
                    return StreamEnd.RateLimited;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return StreamEnd.PeerClosed;
                }

                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    return StreamEnd.Binary;
                }

                total += result.Count;
                if (total > MaxMessageBytes)
                {
                    return StreamEnd.TooBig;
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            await HandleMessageAsync(buffer.AsMemory(0, total)).ConfigureAwait(false);
        }
    }

    private async Task HandleMessageAsync(ReadOnlyMemory<byte> data)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            switch (_phase)
            {
                case Phase.Authenticating:
                    await GuardedAsync(() => AuthenticateAsync(data)).ConfigureAwait(false);
                    break;
                case Phase.Open:
                    await GuardedAsync(() => DispatchAsync(data)).ConfigureAwait(false);
                    break;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Runs work with the gate held; a failure ends the stream (everything released first), never the server.</summary>
    private async Task GuardedAsync(Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch (StreamInterruptedException)
        {
            await EndInterruptedLockedAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_aborted.IsCancellationRequested)
        {
            // The client went away: the reader sees it and ends the stream.
        }
        catch (OperationCanceledException) when (_session?.IsInterrupted == true)
        {
            // A device send was cancelled because the session was cancelled or stopped.
            await EndInterruptedLockedAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _rejections.Record($"stream failed: internal_error ({ex.GetType().Name})");
            await EndLockedAsync(StreamEnd.Internal).ConfigureAwait(false);
        }
    }

    /// <summary>Thrown (synchronously, before anything is sent) when the user took over or the session was cancelled.</summary>
    private sealed class StreamInterruptedException : Exception
    {
    }

    // The user's input is cancelled from the mouse-hook thread and reaches the session's own token only after a thread-pool hop;
    // until then this check, made before every message and every send that puts something down, is what stops the input.
    private void ThrowIfInterrupted()
    {
        if (_session is { IsInterrupted: true })
        {
            throw new StreamInterruptedException();
        }
    }

    private Task EndInterruptedLockedAsync()
    {
        if (_aborted.IsCancellationRequested || _session is not { } session)
        {
            return EndLockedAsync(StreamEnd.Disconnected);
        }

        var reason = session.CurrentInterruption?.Code switch
        {
            ApiErrorCodes.UserInterrupted => "user",
            ApiErrorCodes.NotRunning => "session_ended",
            _ => "cancelled",
        };
        return EndLockedAsync(StreamEnd.Interrupted(reason));
    }

    /// <summary>
    /// A send that puts a finger or key down: refused once the user has taken over, bounded by <see cref="SendTimeout"/> (so a
    /// hung write cannot hold the gate for ever) and cancelled by the connection going away or by the session being cancelled
    /// or stopped. The user's own token is not linked in: cancelling it on the hook thread must not run the continuation of a
    /// BLE write there; the check above covers it.
    /// </summary>
    private async Task DeviceSendAsync(Func<CancellationToken, ValueTask> send)
    {
        ThrowIfInterrupted();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(_deviceCts!.Token);
        bounded.CancelAfter(_sendTimeout);
        await send(bounded.Token).ConfigureAwait(false);
    }

    /// <summary>A lift or release: it always goes out; if the device does not answer within the grace of <see cref="ReleaseSend"/> it is given up.</summary>
    private async Task ReleaseSendAsync(Func<CancellationToken, ValueTask> send)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(_aborted);
        bounded.CancelAfter(ReleaseSend.Grace);
        await ReleaseSend.RunAsync(send, bounded.Token).ConfigureAwait(false);
    }

    // ---- Timers and interruption: queue to the pool, then take the gate -----------------------------------------

    private void Fire(Func<ApiStream, Task> work) =>
        _ = Task.Run(async () =>
        {
            try
            {
                await work(this).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _rejections.Record($"stream failed: internal_error ({ex.GetType().Name})");
            }
        });

    private async Task AuthTimedOutAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_phase == Phase.Authenticating)
            {
                _rejections.Record("stream rejected: authentication timed out");
                await EndLockedAsync(StreamEnd.Unauthorized("authentication timed out", "the first message must be auth, within 2 seconds")).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task FlushAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _flushArmed = false;
            if (_phase == Phase.Open && _pendingSeq is { } seq)
            {
                await GuardedAsync(async () =>
                {
                    ThrowIfInterrupted();
                    var wait = MoveInterval - ElapsedSinceTouch();
                    if (wait > TimeSpan.Zero)
                    {
                        ArmFlush(wait); // the timer fired a little early
                        return;
                    }

                    _pendingSeq = null;
                    await SendTouchAsync().ConfigureAwait(false);
                    await SendAsync(StreamEvent.Ack(seq)).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task IdleCheckAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _idleArmed = false;
            if (_phase != Phase.Open || !IsHeld)
            {
                return;
            }

            await GuardedAsync(async () =>
            {
                ThrowIfInterrupted();
                var wait = IdleTimeout - _time.GetElapsedTime(_lastActivity);
                if (wait > TimeSpan.Zero)
                {
                    ArmIdle(wait);
                    return;
                }

                // The dead-man: the client went quiet with a finger or key down. Release everything and tell it; the session stays open.
                await ReleaseAllAsync().ConfigureAwait(false);
                await SendAsync(StreamEvent.Interrupted("idle")).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndIfOpenAsync(StreamEnd end)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_phase == Phase.Open)
            {
                await EndLockedAsync(end).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task InterruptedAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_phase != Phase.Open)
            {
                return;
            }

            await EndInterruptedLockedAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Ending: release, dispose the session, tell the client, close ----------------------------------------------

    private async Task EndAsync(StreamEnd end)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EndLockedAsync(end).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EndLockedAsync(StreamEnd end)
    {
        if (_phase == Phase.Ended)
        {
            return;
        }

        var opened = _phase == Phase.Open;
        _phase = Phase.Ended;
        _authTimer?.Dispose();
        _flushTimer?.Dispose();
        _idleTimer?.Dispose();
        _limitTimer?.Dispose();
        _interruptRegistration.Dispose();
        _deviceCts?.Dispose();

        // 1. Lift every finger and release every key, before the input goes back to the bridge.
        try
        {
            await ReleaseAllAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _rejections.Record($"stream release failed ({ex.GetType().Name})");
        }

        // 2. Open the gate again: a client that sees the close can start the next operation at once.
        if (_session is { } session)
        {
            _session = null;
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _rejections.Record($"stream session release failed ({ex.GetType().Name})");
            }
        }

        // 3. Tell the client, then close.
        if (end.SendClose)
        {
            if (end.Event is { } ev)
            {
                await TrySendAsync(ev).ConfigureAwait(false);
            }

            await TryCloseAsync(end).ConfigureAwait(false);
        }

        if (opened)
        {
            _log?.Invoke($"[api] stream closed ({end.LogReason})");
        }

        _endedSignal.TrySetResult();
    }

    private async Task TrySendAsync(StreamEvent ev)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(ev, ApiJson.Default.StreamEvent);
        try
        {
            using var timeout = new CancellationTokenSource(_sendTimeout);
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The client is gone or does not read: the reader ends the stream.
        }
    }

    private async Task TryCloseAsync(StreamEnd end)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(_sendTimeout);
            await _socket.CloseOutputAsync(end.Status, end.Description, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            // As above.
        }
    }

    // ---- The first message -------------------------------------------------------------------------------------

    private async Task AuthenticateAsync(ReadOnlyMemory<byte> data)
    {
        var message = Parse(data);
        if (message is not { Op: "auth", Token: { } token } || Fields(message) != Field.Token)
        {
            _rejections.Record("stream rejected: first message is not auth");
            await EndLockedAsync(StreamEnd.Unauthorized("first message is not auth", "the first message must be { \"op\": \"auth\", \"token\": \"...\" }")).ConfigureAwait(false);
            return;
        }

        if (_registry.Authenticate(token) is not { } generation)
        {
            _rejections.Record($"stream rejected: {ApiErrorCodes.Unauthorized}");
            await EndLockedAsync(StreamEnd.Unauthorized("unauthorized", "the token is not valid")).ConfigureAwait(false);
            return;
        }

        _authTimer?.Dispose();
        _generation = generation;
        Interlocked.MemoryBarrier();
        if (_registry.CurrentGeneration != generation)
        {
            // The token was replaced between the check and now (SetTokenAsync closes streams it can see).
            await EndLockedAsync(StreamEnd.TokenChanged).ConfigureAwait(false);
            return;
        }

        var opened = await _controller.TryOpenSessionAsync(_aborted).ConfigureAwait(false);
        if (opened.Error is { } error)
        {
            _rejections.Record($"stream refused: {error.Code}");
            await EndLockedAsync(StreamEnd.Refused(error)).ConfigureAwait(false);
            return;
        }

        _session = opened.Value!;
        _deviceCts = CancellationTokenSource.CreateLinkedTokenSource(_aborted, _session.Interrupted);
        _phase = Phase.Open;
        _lastActivity = _time.GetTimestamp();
        _flushTimer = _time.CreateTimer(static s => ((ApiStream)s!).Fire(static self => self.FlushAsync()), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _idleTimer = _time.CreateTimer(static s => ((ApiStream)s!).Fire(static self => self.IdleCheckAsync()), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_session.TimeLimit is { } limit)
        {
            // The trial: counted from the open; the end releases everything first, like every other end.
            var timeUp = StreamEnd.TrialTimeUp(limit);
            _limitTimer = _time.CreateTimer(_ => Fire(self => self.EndIfOpenAsync(timeUp)), null, limit, Timeout.InfiniteTimeSpan);
        }

        _log?.Invoke("[api] stream opened");
        await SendAsync(StreamEvent.Ready()).ConfigureAwait(false);

        // After the ready: an interruption that already happened is then reported after it.
        _interruptRegistration = _session.Interrupted.UnsafeRegister(static s => ((ApiStream)s!).Fire(static self => self.InterruptedAsync()), this);
    }

    // ---- The messages --------------------------------------------------------------------------------------------

    private async Task DispatchAsync(ReadOnlyMemory<byte> data)
    {
        ThrowIfInterrupted();
        _lastActivity = _time.GetTimestamp();
        var message = Parse(data);
        if (message is null)
        {
            await SendErrorAsync(null, ApiError.InvalidRequest("the message is not a valid JSON object of this API")).ConfigureAwait(false);
            return;
        }

        var (allowed, needsSeq) = message.Op switch
        {
            "ping" => (Field.None, false),
            "down" => (Field.Seq | Field.Space | Field.Ref | Field.Contacts, true),
            "move" => (Field.Seq | Field.Contacts, true),
            "up" => (Field.Seq, true),
            "key" => (Field.Seq | Field.Key | Field.Modifiers | Field.Down, true),
            _ => (Field.None, false),
        };
        if (message.Op is not ("ping" or "down" or "move" or "up" or "key"))
        {
            var detail = message.Op == "auth" ? "the stream is already authenticated" : "op must be down, move, up, key or ping";
            await SendErrorAsync(message.Seq, ApiError.InvalidRequest(detail)).ConfigureAwait(false);
            return;
        }

        var extra = Fields(message) & ~allowed;
        if (extra != Field.None)
        {
            await SendErrorAsync(message.Seq, ApiError.InvalidRequest($"{FieldNames[BitOperations.TrailingZeroCount((int)extra)]} is not valid for {message.Op}")).ConfigureAwait(false);
            return;
        }

        if (needsSeq && message.Seq is not >= 0)
        {
            await SendErrorAsync(null, ApiError.InvalidRequest("seq is required (a number, 0 or more)")).ConfigureAwait(false);
            return;
        }

        switch (message.Op)
        {
            case "ping":
                await SendAsync(StreamEvent.Pong()).ConfigureAwait(false);
                break;
            case "down":
                await DownAsync(message, message.Seq!.Value).ConfigureAwait(false);
                break;
            case "move":
                await MoveAsync(message, message.Seq!.Value).ConfigureAwait(false);
                break;
            case "up":
                await UpAsync(message.Seq!.Value).ConfigureAwait(false);
                break;
            default:
                await KeyAsync(message, message.Seq!.Value).ConfigureAwait(false);
                break;
        }
    }

    // down: puts the listed fingers down (or moves a finger that is already down; the others stay), sets the space for the moves.
    private async Task DownAsync(StreamClientMessage message, int seq)
    {
        if (!_session!.Snapshot().Subscribed)
        {
            await SendErrorAsync(seq, ApiError.Create(ApiErrorCodes.NotConnected, "the target device is not connected")).ConfigureAwait(false);
            return;
        }

        var converted = ToContacts(message.Space, message.Ref, message.Contacts, requireDown: false, out var error);
        if (error is not null)
        {
            await SendErrorAsync(seq, error).ConfigureAwait(false);
            return;
        }

        _space = message.Space;
        _ref = message.Ref;
        _wanted = Merge(_wanted, converted);
        if (_pendingSeq is { } coalesced)
        {
            _pendingSeq = null;
            await SendAsync(StreamEvent.Ack(coalesced)).ConfigureAwait(false);
        }

        await SendTouchAsync().ConfigureAwait(false);
        ArmIdleIfHeld();
        await SendAsync(StreamEvent.Ack(seq)).ConfigureAwait(false);
    }

    // move: the listed fingers (all must be down) go to their new positions; reports are at most one per MoveInterval.
    private async Task MoveAsync(StreamClientMessage message, int seq)
    {
        if (_wanted.Length == 0)
        {
            await SendErrorAsync(seq, ApiError.InvalidRequest("no finger is down: send down first")).ConfigureAwait(false);
            return;
        }

        var converted = ToContacts(_space, _ref, message.Contacts, requireDown: true, out var error);
        if (error is not null)
        {
            await SendErrorAsync(seq, error).ConfigureAwait(false);
            return;
        }

        _wanted = Merge(_wanted, converted);

        // A move still waiting is replaced by this one: it is answered as applied (coalesced), after the state is final.
        var coalesced = _pendingSeq;
        _pendingSeq = null;
        var wait = MoveInterval - ElapsedSinceTouch();
        if (wait <= TimeSpan.Zero)
        {
            if (coalesced is { } old)
            {
                await SendAsync(StreamEvent.Ack(old)).ConfigureAwait(false);
            }

            await SendTouchAsync().ConfigureAwait(false);
            await SendAsync(StreamEvent.Ack(seq)).ConfigureAwait(false);
        }
        else
        {
            _pendingSeq = seq;
            ArmFlush(wait);
            if (coalesced is { } old)
            {
                await SendAsync(StreamEvent.Ack(old)).ConfigureAwait(false);
            }
        }
    }

    // up: every finger is lifted at its last position (the latest move, even one not sent yet), then the empty report.
    private async Task UpAsync(int seq)
    {
        if (_pendingSeq is { } coalesced)
        {
            _pendingSeq = null;
            await SendAsync(StreamEvent.Ack(coalesced)).ConfigureAwait(false);
        }

        await LiftAsync().ConfigureAwait(false);
        await SendAsync(StreamEvent.Ack(seq)).ConfigureAwait(false);
    }

    private async Task KeyAsync(StreamClientMessage message, int seq)
    {
        if (message.Key is null || message.Down is not { } down)
        {
            await SendErrorAsync(seq, ApiError.InvalidRequest("key and down are required")).ConfigureAwait(false);
            return;
        }

        if (!ApiKeys.TryParse(message.Key, message.Modifiers, out var key, out var parseError))
        {
            await SendErrorAsync(seq, ApiError.InvalidRequest(parseError)).ConfigureAwait(false);
            return;
        }

        var snapshot = _session!.Snapshot();
        if (down && !snapshot.HasKeyboard)
        {
            await SendErrorAsync(seq, ApiError.Create(ApiErrorCodes.NoKeyboard, "the published HID map has no keyboard")).ConfigureAwait(false);
            return;
        }

        if (down && !snapshot.Subscribed)
        {
            await SendErrorAsync(seq, ApiError.Create(ApiErrorCodes.NotConnected, "the target device is not connected")).ConfigureAwait(false);
            return;
        }

        var index = _held.FindIndex(k => k.Usage == key.Usage);
        if (down)
        {
            if (index < 0 && _held.Count >= HidReports.KeyboardKeySlots)
            {
                await SendErrorAsync(seq, ApiError.InvalidRequest($"at most {HidReports.KeyboardKeySlots} keys can be down at once")).ConfigureAwait(false);
                return;
            }

            if (index >= 0)
            {
                _held[index] = key;
            }
            else
            {
                _held.Add(key);
            }

            // A modifier that is new goes down one report before the key, like a person typing (and like POST /v1/keys/press).
            var modifiers = HeldModifiers();
            if ((modifiers & ~_reportedModifiers) != 0)
            {
                await SendKeysAsync(modifiers, HeldUsages(excluding: key.Usage)).ConfigureAwait(false);
            }

            await SendKeysAsync(modifiers, HeldUsages()).ConfigureAwait(false);
        }
        else if (index >= 0)
        {
            _held.RemoveAt(index);

            // The key comes up first; the modifiers that no key needs any more come up after it.
            var modifiers = HeldModifiers();
            if ((_reportedModifiers & ~modifiers) != 0)
            {
                await SendKeysAsync(_reportedModifiers, HeldUsages()).ConfigureAwait(false);
            }

            await SendKeysAsync(modifiers, HeldUsages()).ConfigureAwait(false);
        }

        ArmIdleIfHeld();
        await SendAsync(StreamEvent.Ack(seq)).ConfigureAwait(false);
    }

    // ---- Touch -----------------------------------------------------------------------------------------------------

    private bool IsHeld => _wanted.Length > 0 || _held.Count > 0;

    private TimeSpan ElapsedSinceTouch() => _lastTouchSend is { } at ? _time.GetElapsedTime(at) : TimeSpan.MaxValue;

    /// <summary>The fingers of a message as digitizer contacts; the frame is resolved now, so a rotation or a new picture is honoured.</summary>
    private TouchContact[] ToContacts(string? space, PixelRef? reference, IReadOnlyList<StreamContact>? contacts, bool requireDown, out ApiError? error)
    {
        error = ValidateContacts(contacts);
        if (error is not null)
        {
            return [];
        }

        var frame = _session!.ResolveFrame(space, reference);
        if (frame.Error is not null)
        {
            error = frame.Error;
            return [];
        }

        var result = new TouchContact[contacts!.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var c = contacts[i];
            if (requireDown && !_wanted.Any(w => w.Id == c.Id))
            {
                error = ApiError.InvalidRequest($"contact {c.Id} is not down: send down for it first");
                return [];
            }

            var unit = frame.Value!.Spec.ToUnit(c.X, c.Y);
            if (unit.Error is not null)
            {
                error = unit.Error;
                return [];
            }

            var axis = frame.Value.ToAxis(unit.Value!);
            result[i] = new TouchContact((byte)c.Id, axis.X, axis.Y);
        }

        return result;
    }

    private static ApiError? ValidateContacts(IReadOnlyList<StreamContact>? contacts)
    {
        if (contacts is not { Count: >= 1 and <= 2 })
        {
            return ApiError.InvalidRequest("contacts must have 1 or 2 entries");
        }

        var seen = 0;
        foreach (var contact in contacts)
        {
            if (contact is null || contact.Id is not (0 or 1))
            {
                return ApiError.InvalidRequest("a contact id must be 0 or 1");
            }

            var bit = 1 << contact.Id;
            if ((seen & bit) != 0)
            {
                return ApiError.InvalidRequest("contact ids must be unique");
            }

            seen |= bit;
        }

        return null;
    }

    private static TouchContact[] Merge(TouchContact[] current, TouchContact[] incoming) =>
        [.. current.Where(c => incoming.All(i => i.Id != c.Id)).Concat(incoming).OrderBy(c => c.Id)];

    private async Task SendTouchAsync()
    {
        _lastTouchSend = _time.GetTimestamp();
        var contacts = _wanted;
        await DeviceSendAsync(ct => _session!.Host.Touch.SendAsync(contacts, ct)).ConfigureAwait(false);
    }

    // The lift of TouchPlayer: Tip = 0 at the LAST position (never at 0,0), then the empty report; both go out even when cancelled.
    private async Task LiftAsync()
    {
        var down = _wanted;
        if (down.Length == 0)
        {
            return;
        }

        _wanted = [];
        _pendingSeq = null;
        var sink = _session!.Host.Touch;
        var lifted = Array.ConvertAll(down, c => c.Lifted());
        _lastTouchSend = _time.GetTimestamp();
        await ReleaseSendAsync(ct => sink.SendAsync(lifted, ct)).ConfigureAwait(false);
        await ReleaseSendAsync(ct => sink.SendAsync(ReadOnlyMemory<TouchContact>.Empty, ct)).ConfigureAwait(false);
    }

    private void ArmFlush(TimeSpan due)
    {
        if (_flushArmed || _flushTimer is null)
        {
            return;
        }

        _flushArmed = true;
        _flushTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    // ---- Keys ------------------------------------------------------------------------------------------------------

    private byte HeldModifiers()
    {
        byte bits = 0;
        foreach (var key in _held)
        {
            bits |= key.Modifiers;
        }

        return bits;
    }

    private byte[] HeldUsages(byte? excluding = null) =>
        [.. _held.Where(k => k.Usage != excluding).Select(k => k.Usage)];

    private async Task SendKeysAsync(byte modifiers, byte[] usages)
    {
        if (modifiers == _reportedModifiers && usages.AsSpan().SequenceEqual(_reportedKeys))
        {
            return;
        }

        _reportedModifiers = modifiers;
        _reportedKeys = usages;
        await DeviceSendAsync(ct => _session!.Host.Keyboard.SendKeyboardAsync(modifiers, usages, ct)).ConfigureAwait(false);
    }

    // ---- Release -----------------------------------------------------------------------------------------------------

    /// <summary>Lifts every finger and releases every key (each only when it is down). Both are tried; the first failure is thrown after.</summary>
    private async Task ReleaseAllAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        Exception? failure = null;
        try
        {
            await LiftAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
        }

        _pendingSeq = null;
        if (_held.Count > 0 || _reportedModifiers != 0 || _reportedKeys.Length > 0)
        {
            _held.Clear();
            _reportedModifiers = 0;
            _reportedKeys = [];
            try
            {
                await ReleaseSendAsync(ct => session.Host.Keyboard.SendKeyboardAsync(0, ReadOnlyMemory<byte>.Empty, ct)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure ??= ex;
            }
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }
    }

    private void ArmIdleIfHeld()
    {
        if (IsHeld)
        {
            ArmIdle(IdleTimeout);
        }
    }

    private void ArmIdle(TimeSpan due)
    {
        if (_idleArmed || _idleTimer is null)
        {
            return;
        }

        _idleArmed = true;
        _idleTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static StreamClientMessage? Parse(ReadOnlyMemory<byte> data)
    {
        try
        {
            return JsonSerializer.Deserialize(data.Span, ApiJson.Default.StreamClientMessage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Field Fields(StreamClientMessage m)
    {
        var fields = Field.None;
        fields |= m.Token is null ? Field.None : Field.Token;
        fields |= m.Seq is null ? Field.None : Field.Seq;
        fields |= m.Space is null ? Field.None : Field.Space;
        fields |= m.Ref is null ? Field.None : Field.Ref;
        fields |= m.Contacts is null ? Field.None : Field.Contacts;
        fields |= m.Key is null ? Field.None : Field.Key;
        fields |= m.Modifiers is null ? Field.None : Field.Modifiers;
        fields |= m.Down is null ? Field.None : Field.Down;
        return fields;
    }

    private Task SendAsync(StreamEvent ev) => TrySendAsync(ev);

    private Task SendErrorAsync(int? seq, ApiError error) => TrySendAsync(StreamEvent.Error(seq, error));
}
