// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using Microsoft.AspNetCore.Http;

namespace IceScreenTouch.Api;

/// <summary>
/// The WebSocket endpoint <c>GET /v1/stream</c> of the <see cref="ApiServer"/>: accepts the upgrade (after the server's Origin and
/// Host checks; the token comes in the first message) and keeps the open streams so the server can close them when the token
/// changes or it stops.
/// </summary>
internal sealed class ApiStreamEndpoint(
    ApiController controller,
    TimeProvider time,
    Action<string>? log,
    RejectionLog rejections,
    Func<string?, int?> authenticate,
    Func<int> currentGeneration) : IStreamRegistry
{
    /// <summary>At most this many upgrades at once (open streams plus those being accepted); Kestrel's own limit is higher (see <see cref="ApiServer"/>).</summary>
    public const int MaxStreams = 4;

    private readonly Lock _lock = new();
    private readonly HashSet<ApiStream> _streams = [];
    private int _slots;

    /// <summary>The open streams (including those still waiting for their <c>auth</c>).</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _streams.Count;
            }
        }
    }

    /// <inheritdoc />
    public int CurrentGeneration => currentGeneration();

    /// <inheritdoc />
    public int? Authenticate(string? token) => authenticate(token);

    /// <summary>Handles one upgrade request until the stream ends.</summary>
    public async Task HandleAsync(HttpContext context)
    {
        // The count is our own and lower than Kestrel's limit on upgraded connections, so a surplus client is refused with an
        // ordinary answer BEFORE the handshake (never a 500 from the server's limit). Only one stream can own the session at a
        // time; the others are waiting for their auth, being refused or closing.
        bool reserved;
        lock (_lock)
        {
            reserved = _slots < MaxStreams;
            if (reserved)
            {
                _slots++;
            }
        }

        if (!reserved)
        {
            rejections.Record("stream rejected: busy (too many streams)");
            await ApiResponses.WriteProblemAsync(context, ApiError.Create(ApiErrorCodes.Busy, $"at most {MaxStreams} streams can be open at once")).ConfigureAwait(false);
            return;
        }

        ApiStream? stream = null;
        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            stream = new ApiStream(socket, this, controller, time, log, rejections, context.RequestAborted);
            lock (_lock)
            {
                _streams.Add(stream);
            }

            await stream.RunAsync().ConfigureAwait(false);
        }
        finally
        {
            // The stream and its slot go together, so a client that sees the slot free can connect again at once.
            lock (_lock)
            {
                if (stream is not null)
                {
                    _streams.Remove(stream);
                }

                _slots--;
            }
        }
    }

    /// <summary>Ends every open stream (everything released first). Returns when they have ended, or after <paramref name="timeout"/>.</summary>
    public Task CloseAllAsync(StreamEnd end, TimeSpan timeout) => CloseWhereAsync(_ => true, end, timeout);

    /// <summary>Ends the streams that authenticated with an older token than <paramref name="generation"/> (a stream that already uses a newer one stays).</summary>
    public Task CloseStaleAsync(int generation) =>
        CloseWhereAsync(s => s.Generation >= 0 && s.Generation < generation, StreamEnd.TokenChanged, TimeSpan.FromSeconds(5));

    private async Task CloseWhereAsync(Func<ApiStream, bool> filter, StreamEnd end, TimeSpan timeout)
    {
        Interlocked.MemoryBarrier();
        ApiStream[] targets;
        lock (_lock)
        {
            targets = [.. _streams.Where(filter)];
        }

        if (targets.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(targets.Select(s => s.CloseAsync(end))).WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // A stream stuck behind a hung device write is dropped with the connection.
        }
    }
}
