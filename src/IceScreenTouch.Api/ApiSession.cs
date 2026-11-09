// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Api;

/// <summary>
/// The long-lived lease of a WebSocket session (opened by <see cref="ApiController.TryOpenSessionAsync"/>): while
/// it is open the controller refuses every other operation with <c>busy</c>, and the input lease is held, so the
/// user's own mouse and keys are held back. The owner sends the fingers and keys itself; when
/// <see cref="Interrupted"/> fires (the user took over, <see cref="ApiController.CancelCurrent"/>, the connection
/// went away) it must lift every finger and release every key, tell the client, and dispose the session. It must
/// also lift and release everything BEFORE disposing, because disposing gives the input back to the bridge.
/// </summary>
public sealed class ApiSession : IAsyncDisposable
{
    private readonly ApiController _controller;
    private readonly ApiController.Handle _handle;
    private readonly IInputLease _lease;
    private readonly CancellationTokenRegistration _connectionRegistration;
    private readonly CancellationTokenRegistration _userRegistration;
    private readonly long _started;
    private int _disposed;

    internal ApiSession(ApiController controller, ApiController.Handle handle, IInputLease lease, long started, CancellationToken connection)
    {
        _controller = controller;
        _handle = handle;
        _lease = lease;
        _started = started;

        // The token is the handle's own (cached, so reading it after the disposal is safe); the connection and the user's
        // input only queue its cancellation, so the owner's code never runs on the thread of the mouse hook or the socket.
        Interrupted = handle.Token;
        _connectionRegistration = handle.CancelOn(connection);
        _userRegistration = handle.CancelOn(lease.UserInput);
    }

    /// <summary>
    /// Cancelled (from a thread-pool thread) when the session must stop: the user took over, the operation was
    /// cancelled, or the connection ended. Safe to read after <see cref="DisposeAsync"/>.
    /// </summary>
    public CancellationToken Interrupted { get; }

    /// <summary>
    /// Why <see cref="Interrupted"/> fired: <c>user_interrupted</c> (the user wins) or <c>cancelled</c>; null while
    /// it has not fired.
    /// </summary>
    public ApiError? InterruptionError =>
        Interrupted.IsCancellationRequested ? ApiController.InterruptionError(_lease) : null;

    /// <summary>
    /// Ties a message's <c>space</c> and <c>ref</c> to the current picture and rotation. Failures:
    /// <c>invalid_request</c> (bad space or ref) and <c>no_picture</c> (screen and pixel need one).
    /// </summary>
    public ApiResult<CoordinateFrame> ResolveFrame(string? space, PixelRef? reference)
    {
        var spec = CoordinateSpec.Parse(space, reference);
        return spec.IsSuccess ? spec.Value!.Bind(_controller.Snapshot()) : ApiResult.Fail<CoordinateFrame>(spec.Error!);
    }

    /// <summary>Ends the session: gives the input back to the bridge and lets other operations in.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _connectionRegistration.Dispose();
        _userRegistration.Dispose();
        try
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // The gate opens even when giving the input back fails.
            _controller.Release(_handle);
            _controller.LogResult("session", "closed", _started);
        }
    }
}
