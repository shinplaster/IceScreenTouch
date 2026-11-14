// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>
/// Thrown by <see cref="IApiHost.AcquireInputAsync"/> when the input cannot be given right now (the user is in the middle
/// of a gesture, or the bridge is suspended or busy). The controller turns it into the matching error
/// (<c>user_active</c>, <c>busy</c>) instead of failing the request.
/// </summary>
public sealed class InputRefusedException : Exception
{
    /// <summary>Creates the exception for an <see cref="ApiErrorCodes"/> code.</summary>
    public InputRefusedException(string code, string detail)
        : base(detail)
    {
        Code = code;
    }

    /// <summary>The error code: <see cref="ApiErrorCodes.UserActive"/> or <see cref="ApiErrorCodes.Busy"/>.</summary>
    public string Code { get; }
}

/// <summary>The lease of the API on top of an <see cref="ApiInputLease"/> of the core.</summary>
internal sealed class ArbiterInputLease(ApiInputLease inner) : IInputLease
{
    public CancellationToken UserInput => inner.UserInput;

    public bool SessionEnded => inner.SessionEnded;

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>
/// The input part of an <see cref="IApiHost"/> on top of the core's <see cref="ApiInputArbiter"/>: the host creates
/// one arbiter per session, gives it to the bridge and to the keyboard input, and delegates
/// <see cref="IApiHost.AcquireInputAsync"/> and the user-input fields of the snapshot to this class.
/// </summary>
/// <param name="arbiter">The session's arbiter (created with the same <see cref="TimeProvider"/> as the controller).</param>
public sealed class ArbiterInput(ApiInputArbiter arbiter)
{
    /// <summary>For <see cref="ApiHostSnapshot.LastUserInput"/>.</summary>
    public DateTimeOffset? LastUserInput => arbiter.LastUserInput;

    /// <summary>For <see cref="ApiHostSnapshot.UserInputActive"/>.</summary>
    public bool UserInputActive => arbiter.UserInputActive;

    /// <summary>For <see cref="IApiHost.AcquireInputAsync"/>.</summary>
    /// <exception cref="InputRefusedException">The user is active (<c>user_active</c>) or the bridge is suspended or busy (<c>busy</c>), or the session has ended (<c>not_running</c>).</exception>
    public async ValueTask<IInputLease> AcquireAsync(CancellationToken cancellationToken)
    {
        var result = await arbiter.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        return result switch
        {
            { Lease: { } lease } => new ArbiterInputLease(lease),
            { Refusal: ApiInputRefusal.UserActive } => throw new InputRefusedException(ApiErrorCodes.UserActive, "the user is using the mirror"),
            { Refusal: ApiInputRefusal.Closed } => throw new InputRefusedException(ApiErrorCodes.NotRunning, "the mirroring session is not running"),
            { Refusal: ApiInputRefusal.Suspended } => throw new InputRefusedException(ApiErrorCodes.Busy, "a measurement is running"),
            _ => throw new InputRefusedException(ApiErrorCodes.Busy, "another operation or session is running"),
        };
    }
}
