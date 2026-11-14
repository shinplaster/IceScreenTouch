// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Core;

/// <summary>Why <see cref="ApiInputArbiter.TryAcquireAsync"/> did not give the input.</summary>
public enum ApiInputRefusal
{
    /// <summary>Nothing: the input was given.</summary>
    None,

    /// <summary>The user is in the middle of a gesture (a finger down, wheel inertia, keys held, or input not yet handled).</summary>
    UserActive,

    /// <summary>The bridge is suspended by another sender (the probe).</summary>
    Suspended,

    /// <summary>Another lease is held or being taken.</summary>
    Busy,

    /// <summary>The bridge loop has ended (<see cref="ApiInputArbiter.Close"/>): nothing can be acquired any more.</summary>
    Closed,
}

/// <summary>The answer of <see cref="ApiInputArbiter.TryAcquireAsync"/>.</summary>
/// <param name="Refusal">Why the input was not given (<see cref="ApiInputRefusal.None"/> when it was).</param>
/// <param name="Lease">The lease, or null when refused.</param>
public readonly record struct ApiInputAcquisition(ApiInputRefusal Refusal, ApiInputLease? Lease)
{
    /// <summary>The input was given.</summary>
    public bool Acquired => Lease is not null;
}

/// <summary>
/// The right of the external API to send input while the user's own input is held back. Disposing it gives the
/// input back (the bridge and the keyboard relay then process what the user did meanwhile). Lift every finger and
/// release every key BEFORE disposing.
/// </summary>
public sealed class ApiInputLease : IAsyncDisposable
{
    private readonly ApiInputArbiter _owner;
    private readonly CancellationTokenSource _userInput = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _fired;
    private int _sessionEnded;

    internal ApiInputLease(ApiInputArbiter owner)
    {
        _owner = owner;
        UserInput = _userInput.Token; // read once: the source is never disposed (no timer, no wait handle), so the token stays usable
    }

    /// <summary>Cancelled (once) when the user's input reached the mirror while the lease was held, or the probe wants the input.</summary>
    public CancellationToken UserInput { get; }

    /// <summary>Completes when the lease is given back (or was never granted).</summary>
    internal Task Released => _released.Task;

    /// <summary>
    /// Fires <see cref="UserInput"/> at most once. The callbacks run on the thread pool, never on the caller (the
    /// bridge loop, the keyboard relay), so a slow callback cannot stall the user's input.
    /// </summary>
    internal void Fire()
    {
        if (Interlocked.Exchange(ref _fired, 1) == 0)
        {
            // CancelAsync marks the token cancelled at once and runs the callbacks on the thread pool.
            _ = _userInput.CancelAsync();
        }
    }

    internal void MarkReleased() => _released.TrySetResult();

    /// <summary>The arbiter was closed (the session ended) while the lease was held: <see cref="UserInput"/> fired for that reason, not because of the user.</summary>
    public bool SessionEnded => Volatile.Read(ref _sessionEnded) == 1;

    internal void MarkSessionEnded() => Volatile.Write(ref _sessionEnded, 1);

    /// <summary>Gives the input back. Idempotent.</summary>
    public ValueTask DisposeAsync()
    {
        _owner.Release(this);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Hands the input to the external API and back, and gives the user priority (docs/API-DESIGN.md, 9.2). While an
/// <see cref="ApiInputLease"/> is held, <see cref="MirrorBridge"/> and <see cref="KeyboardRelay"/> send nothing to the
/// device; the first user input that would have reached it fires <see cref="ApiInputLease.UserInput"/> and is HELD
/// (together with everything after it) until the lease is disposed, and then goes through as if it had just
/// happened. This is a state of its own, separate from <see cref="MirrorBridge.SuspendAsync"/> (which drops input).
/// <para>
/// One instance per session: pass it to <c>MirrorBridge</c>, to <c>KeyboardInput.Start</c> (the relay) and to the API
/// host (<c>ArbiterInput</c> in IceScreenTouch.Api). Thread-safe.
/// </para>
/// </summary>
public sealed class ApiInputArbiter
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private ApiPhase _phase;
    private ApiInputLease? _lease;
    private TaskCompletionSource<ApiInputRefusal>? _ack;
    private DateTimeOffset? _lastUserInput;
    private bool _closed;
    private bool _suspendWanted;
    private bool _touchActive;
    private bool _keysHeld;
    private int _keyBusy;
    private int _keyPending;

    /// <summary>Creates an arbiter. <paramref name="time"/> stamps <see cref="LastUserInput"/>: use the controller's provider.</summary>
    public ApiInputArbiter(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The phases of the hand-over.</summary>
    internal enum ApiPhase
    {
        /// <summary>The bridge and the relay send the user's input.</summary>
        Idle,

        /// <summary>A lease was asked for; the bridge has not yet said whether it is idle.</summary>
        Acquiring,

        /// <summary>The lease is held: nothing of the user's reaches the device.</summary>
        Held,
    }

    /// <summary>When the user last did something that reached (or would have reached) the device; null if never.</summary>
    public DateTimeOffset? LastUserInput
    {
        get
        {
            lock (_gate)
            {
                return _lastUserInput;
            }
        }
    }

    /// <summary>The user's touch is down, wheel inertia runs, the user's keys are held, or user input is waiting to be handled.</summary>
    public bool UserInputActive
    {
        get
        {
            lock (_gate)
            {
                return _touchActive || _keysHeld || _keyBusy > 0 || _keyPending > 0;
            }
        }
    }

    /// <summary>A lease is held or being taken (the user's input is held back).</summary>
    public bool HoldsInput
    {
        get
        {
            lock (_gate)
            {
                return _phase != ApiPhase.Idle;
            }
        }
    }

    internal ApiPhase Phase
    {
        get
        {
            lock (_gate)
            {
                return _phase;
            }
        }
    }

    /// <summary>
    /// Asks for the input. The bridge loop answers on its next tick (like <see cref="MirrorBridge.SuspendAsync"/>);
    /// the task does not complete while no loop runs (cancel it). Refused (never waits) when another lease exists,
    /// the bridge is suspended, or the user is in the middle of a gesture, keys held, or has input not yet handled.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled (nothing is held then).</exception>
    public async ValueTask<ApiInputAcquisition> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApiInputLease lease;
        Task<ApiInputRefusal> answer;
        lock (_gate)
        {
            if (_closed)
            {
                return new ApiInputAcquisition(ApiInputRefusal.Closed, null);
            }

            if (_phase != ApiPhase.Idle)
            {
                return new ApiInputAcquisition(ApiInputRefusal.Busy, null);
            }

            if (_suspendWanted)
            {
                return new ApiInputAcquisition(ApiInputRefusal.Suspended, null);
            }

            if (UserKeysBusy || _touchActive)
            {
                return new ApiInputAcquisition(ApiInputRefusal.UserActive, null);
            }

            _phase = ApiPhase.Acquiring;
            _lease = lease = new ApiInputLease(this);
            _ack = new TaskCompletionSource<ApiInputRefusal>(TaskCreationOptions.RunContinuationsAsynchronously);
            answer = _ack.Task;
        }

        ApiInputRefusal refusal;
        try
        {
            refusal = await answer.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Give back whatever the bridge granted meanwhile, or take the request back.
            Release(lease);
            throw;
        }

        return refusal == ApiInputRefusal.None
            ? new ApiInputAcquisition(ApiInputRefusal.None, lease)
            : new ApiInputAcquisition(refusal, null);
    }

    /// <summary>
    /// Ends the arbiter's life; <see cref="MirrorBridge.RunAsync"/> calls it when the loop stops (cancelled or failed), so
    /// that no loop is left to answer. Idempotent, any thread. A pending acquisition completes with
    /// <see cref="ApiInputRefusal.Closed"/>, every later one is refused with it, a held lease's token fires and the lease
    /// is taken away (its disposal is then a no-op), and a key the relay holds back is let through as a normal key
    /// (the keyboard path goes on while the mouse loop is gone). There is no reopening: create a new arbiter, and pass it
    /// to the new bridge and keyboard input, for every session.
    /// </summary>
    public void Close()
    {
        TaskCompletionSource<ApiInputRefusal>? ack;
        ApiInputLease? lease;
        lock (_gate)
        {
            _closed = true;
            lease = _lease;
            ack = _ack;
            _lease = null;
            _ack = null;
            _phase = ApiPhase.Idle;
        }

        lease?.MarkSessionEnded(); // before the token fires: whoever sees it cancelled must be able to tell why
        lease?.Fire();
        lease?.MarkReleased(); // a relay that waits for it carries on
        ack?.TrySetResult(ApiInputRefusal.Closed);
    }

    // ---- the bridge loop -----------------------------------------------------------------------------------------

    /// <summary>The bridge was asked to suspend (or resumed): a suspension wanted or in force refuses new leases.</summary>
    internal void SetSuspendWanted(bool wanted)
    {
        lock (_gate)
        {
            _suspendWanted = wanted;
        }
    }

    /// <summary>The bridge's view of the user's touch (engine down or scrolling, or input held back), at the end of every tick.</summary>
    internal void SetTouchActive(bool active)
    {
        lock (_gate)
        {
            _touchActive = active;
        }
    }

    /// <summary>
    /// Answers a pending <see cref="TryAcquireAsync"/> from the bridge loop. Returns true when the lease is now held.
    /// </summary>
    /// <param name="bridgeIdle">The bridge has no user gesture, no pending user press or shortcut.</param>
    internal bool CompleteAcquire(bool bridgeIdle)
    {
        TaskCompletionSource<ApiInputRefusal>? ack;
        ApiInputLease? refusedLease = null;
        ApiInputRefusal refusal;
        lock (_gate)
        {
            if (_phase != ApiPhase.Acquiring)
            {
                return _phase == ApiPhase.Held;
            }

            refusal = _suspendWanted ? ApiInputRefusal.Suspended
                : !bridgeIdle || UserKeysBusy ? ApiInputRefusal.UserActive
                : ApiInputRefusal.None;
            if (refusal == ApiInputRefusal.None)
            {
                _phase = ApiPhase.Held;
            }
            else
            {
                _phase = ApiPhase.Idle;
                refusedLease = _lease;
                _lease = null;
            }

            ack = _ack;
            _ack = null;
        }

        refusedLease?.MarkReleased(); // a relay that waited for it carries on
        ack?.TrySetResult(refusal);
        return refusal == ApiInputRefusal.None;
    }

    /// <summary>The user did something that reaches the device: stamps <see cref="LastUserInput"/>.</summary>
    internal void StampUserInput()
    {
        lock (_gate)
        {
            _lastUserInput = _time.GetUtcNow();
        }
    }

    /// <summary>
    /// The user did something that would have reached the device while the lease is held (or being taken): stamps
    /// <see cref="LastUserInput"/> and fires the lease's token (once).
    /// </summary>
    internal void NotifyUserInput()
    {
        ApiInputLease? lease;
        lock (_gate)
        {
            _lastUserInput = _time.GetUtcNow();
            lease = _phase == ApiPhase.Idle ? null : _lease;
        }

        lease?.Fire();
    }

    /// <summary>The probe wants the input: fires the token without counting it as user input.</summary>
    internal void InterruptForProbe()
    {
        ApiInputLease? lease;
        lock (_gate)
        {
            lease = _phase == ApiPhase.Idle ? null : _lease;
        }

        lease?.Fire();
    }

    internal void Release(ApiInputLease lease)
    {
        TaskCompletionSource<ApiInputRefusal>? ack = null;
        lock (_gate)
        {
            if (_lease == lease)
            {
                _phase = ApiPhase.Idle;
                _lease = null;
                ack = _ack;
                _ack = null;
            }
        }

        ack?.TrySetCanceled(); // taken back before the bridge answered
        lease.MarkReleased();
    }

    // ---- the keyboard relay --------------------------------------------------------------------------------------

    private bool UserKeysBusy => _keysHeld || _keyBusy > 0 || _keyPending > 0;

    /// <summary>
    /// The relay is about to apply one key event. Returns <see cref="KeyAdmission.Send"/> when it may (call
    /// <see cref="EndKeyEvent"/> afterwards) and <see cref="KeyAdmission.StateOnly"/> when a lease is held and the event
    /// would not reach the device anyway (a modifier, a stray release): apply it to the relay's state but send nothing.
    /// An event that does interrupt fires the token and waits here, asynchronously, until the lease is given back.
    /// After the wait (or at once) such a key going down is dropped when a probe suspension is wanted.
    /// </summary>
    /// <param name="interrupts">The event is a key going down (not a modifier): it would reach the device.</param>
    /// <param name="cancellationToken">Stops the wait (the relay is stopping).</param>
    internal async ValueTask<KeyAdmission> BeginKeyEventAsync(bool interrupts, CancellationToken cancellationToken)
    {
        var pending = false;
        try
        {
            while (true)
            {
                ApiInputLease lease;
                lock (_gate)
                {
                    if (_phase == ApiPhase.Idle)
                    {
                        if (pending)
                        {
                            _keyPending--;
                            pending = false;
                        }

                        if (interrupts && _suspendWanted)
                        {
                            return KeyAdmission.Drop;
                        }

                        _keyBusy++;
                        return KeyAdmission.Send;
                    }

                    if (!interrupts)
                    {
                        return KeyAdmission.StateOnly;
                    }

                    if (!pending)
                    {
                        // While it waits, no new lease is given (TryAcquireAsync sees the pending key).
                        _keyPending++;
                        pending = true;
                    }

                    _lastUserInput = _time.GetUtcNow();
                    lease = _lease!;
                }

                lease.Fire();
                await lease.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            if (pending)
            {
                lock (_gate)
                {
                    _keyPending--;
                }
            }

            throw;
        }
    }

    /// <summary>The relay finished one event that <see cref="BeginKeyEventAsync"/> admitted with <see cref="KeyAdmission.Send"/>.</summary>
    /// <param name="keysHeld">The relay holds keys down now.</param>
    /// <param name="reachedDevice">The event changed what the device is told (a report was produced).</param>
    internal void EndKeyEvent(bool keysHeld, bool reachedDevice)
    {
        lock (_gate)
        {
            _keyBusy--;
            _keysHeld = keysHeld;
            if (reachedDevice)
            {
                _lastUserInput = _time.GetUtcNow();
            }
        }
    }

    /// <summary>The relay released everything (it stopped).</summary>
    internal void KeysReleased()
    {
        lock (_gate)
        {
            _keysHeld = false;
        }
    }

    /// <summary>What <see cref="BeginKeyEventAsync"/> lets the relay do with an event.</summary>
    internal enum KeyAdmission
    {
        /// <summary>Apply it and send the report.</summary>
        Send,

        /// <summary>Apply it to the state only: a lease is held and it does not reach the device.</summary>
        StateOnly,

        /// <summary>
        /// Skip it entirely (do not even apply it): a key going down while the probe has (or is taking) the phone. Skipping
        /// the press leaves no key in the relay, so its release later is a no-op and nothing is sent after the probe's
        /// suspension. Releases and modifiers are never dropped (they keep the relay's state true to what was sent).
        /// </summary>
        Drop,
    }
}
