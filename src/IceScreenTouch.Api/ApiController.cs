// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>
/// The transport-independent core of the external API (docs/API-DESIGN.md): runs one operation at a time,
/// checks the preconditions in a fixed order, converts coordinates, enforces the limits, and gives way to the
/// user (the user's input on the mirror stops the operation, lifts every finger and releases every key).
/// </summary>
/// <remarks>
/// Precondition order (the first that fails is the answer): license (<c>purchase_required</c>), the request itself
/// (<c>invalid_request</c>: shape, ranges, limits, key names), <c>not_running</c>, probe measurement running
/// (<c>busy</c>), another operation or a session running (<c>busy</c>), <c>user_active</c> (the user used the mirror
/// within <see cref="UserActiveWindow"/>), <c>not_connected</c>, <c>no_picture</c> (only for <c>screen</c> and
/// <c>pixel</c> positions), <c>no_keyboard</c> / <c>no_home_button</c>. After that, the request can still fail with
/// <c>invalid_request</c> where the check needs the picture (pinch fingers that leave the screen). Nothing is sent
/// before every check has passed.
/// <para>
/// Interruption: when the user touches the mirror (<see cref="IInputLease.UserInput"/>), <see cref="CancelCurrent"/>
/// is called, or the caller's token is cancelled (the client went away), the operation stops, lifts every finger
/// at its last position, releases every key, and only then gives the input back to the bridge.
/// </para>
/// <para>Logging is limited to the kind of operation, the result code and the time: never positions, keys or text.</para>
/// </remarks>
public sealed class ApiController : IDisposable
{
    /// <summary>A new operation is refused with <c>user_active</c> for this long after the user's last input.</summary>
    public static readonly TimeSpan UserActiveWindow = TimeSpan.FromSeconds(1);

    /// <summary>The longest operation: holds, durations, counts times intervals and paths all stay within this.</summary>
    public const int MaxDurationMs = 60_000;

    /// <summary>Taps in one <c>tap</c> request.</summary>
    public const int MaxTapCount = 10;

    /// <summary>Points in one <c>path</c> request.</summary>
    public const int MaxPathPoints = 1000;

    /// <summary>
    /// Characters in one <c>type</c> request. The 60 s limit applies too, on an estimate (see <see cref="TypeTextAsync"/>).
    /// </summary>
    public const int MaxTextLength = 1_000;

    /// <summary>The longest pause between two typed characters.</summary>
    public const int MaxTypeIntervalMs = 1_000;

    /// <summary>Default hold of a tap.</summary>
    public const int DefaultTapHoldMs = 80;

    /// <summary>Default pause between taps.</summary>
    public const int DefaultTapIntervalMs = 120;

    /// <summary>Default hold of a long press.</summary>
    public const int DefaultLongPressHoldMs = 800;

    /// <summary>Default duration of a swipe.</summary>
    public const int DefaultSwipeDurationMs = 300;

    /// <summary>Default duration of a pinch.</summary>
    public const int DefaultPinchDurationMs = 400;

    /// <summary>Default hold of a key press.</summary>
    public const int DefaultKeyHoldMs = 30;

    /// <summary>Default pause after each typed character.</summary>
    public const int DefaultTypeIntervalMs = 20;

    // The modifier gaps inside KeyboardTyping.PressAsync (2 x 15 ms), counted into the limit of a key press.
    private const int KeyPressOverheadMs = 30;

    private static readonly TouchTiming Timing = TouchTiming.Default;

    private readonly IApiHost _host;
    private readonly Func<bool> _isApiAllowed;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly RejectionLog _refusals;
    private readonly Lock _gate = new();
    private Handle? _current;

    /// <summary>Creates the controller.</summary>
    /// <param name="host">The application.</param>
    /// <param name="isApiAllowed">True when the license includes the API (false = <c>purchase_required</c>).</param>
    /// <param name="time">The clock for every wait and measurement.</param>
    /// <param name="log">Receives one line per operation (kind, result code, time).</param>
    public ApiController(IApiHost host, Func<bool> isApiAllowed, TimeProvider? time = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(isApiAllowed);
        _host = host;
        _isApiAllowed = isApiAllowed;
        _time = time ?? TimeProvider.System;
        _log = log;
        _refusals = new RejectionLog(log, _time);
    }

    /// <summary>An operation or a session is running.</summary>
    public bool IsOperating
    {
        get
        {
            lock (_gate)
            {
                return _current is not null;
            }
        }
    }

    // ---- Status and settings -------------------------------------------------------------------------------

    /// <summary><c>GET /v1/status</c>. Not an operation: allowed while one runs.</summary>
    public ApiResult<ApiStatus> GetStatus() =>
        _isApiAllowed() ? ApiResult.Ok<ApiStatus>(_host.GetStatus() with { Operating = IsOperating }) : ApiResult.Fail<ApiStatus>(PurchaseRequired());

    /// <summary><c>GET /v1/rotation</c>. Not an operation.</summary>
    public ApiResult<RotationInfo> GetRotation() =>
        _isApiAllowed() ? ApiResult.Ok<RotationInfo>(ToInfo(_host.GetSnapshot())) : ApiResult.Fail<RotationInfo>(PurchaseRequired());

    /// <summary>
    /// <c>PUT /v1/rotation</c>: landscape 90 or 270, portrait 0 or 180, at least one. An operation (it is
    /// <c>busy</c> while another runs) but it needs no input, no picture and no connected device.
    /// </summary>
    public async ValueTask<ApiResult<RotationInfo>> SetRotationAsync(RotationRequest? request, CancellationToken cancellationToken = default) =>
        await ExecuteAsync("set-rotation", () =>
        {
            Require(request, "a body is required");
            if (request.Landscape is null && request.Portrait is null)
            {
                throw Invalid("landscape or portrait is required");
            }

            Rotation? landscape = request.Landscape switch
            {
                null => null,
                90 => Rotation.R90,
                270 => Rotation.R270,
                _ => throw Invalid("landscape must be 90 or 270"),
            };
            Rotation? portrait = request.Portrait switch
            {
                null => null,
                0 => Rotation.R0,
                180 => Rotation.R180,
                _ => throw Invalid("portrait must be 0 or 180"),
            };
            return new OpSpec<RotationInfo>(Needs.None, _ => ct =>
            {
                ct.ThrowIfCancellationRequested();
                _host.SetRotation(landscape, portrait);
                return ValueTask.FromResult(ToInfo(_host.GetSnapshot()));
            });
        }, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// <c>PUT /v1/target</c>: chooses the device that gets the input (null = automatic). An unknown id is
    /// <c>invalid_request</c>. The host switches when no finger or key is down (they are not, during this call).
    /// </summary>
    public async ValueTask<ApiResult> SetTargetAsync(TargetRequest? request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("set-target", () =>
        {
            Require(request, "a body is required");
            var id = request.DeviceId;
            return new OpSpec<bool>(Needs.Session, _ => ct =>
            {
                ct.ThrowIfCancellationRequested();
                return _host.ChooseTarget(id) ? ValueTask.FromResult(true) : throw Invalid("unknown device id");
            });
        }, cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    // ---- Control -------------------------------------------------------------------------------------------

    /// <summary>Stops the running operation (as <see cref="CancelCurrent"/>) and reports the held-back log counts; call when the application shuts down.</summary>
    public void Dispose()
    {
        CancelCurrent();
        _refusals.Flush();
    }

    /// <summary>
    /// <c>POST /v1/cancel</c>: ends the running operation (it lifts and answers <c>cancelled</c>) or tells the open
    /// session to stop. Returns false when nothing was running.
    /// </summary>
    public bool CancelCurrent()
    {
        Handle? handle;
        lock (_gate)
        {
            handle = _current;
        }

        if (handle is null)
        {
            return false;
        }

        handle.Cancel();
        return true;
    }

    /// <summary>
    /// Opens a session: the long-lived lease of the WebSocket. While it is open every other operation is
    /// <c>busy</c> (status and screenshots are not operations and stay allowed). It needs the same
    /// preconditions as touch input (running, no probe, idle, user not active, connected) but no picture: the
    /// session asks for a <see cref="ApiSession.ResolveFrame"/> per message. Dispose the session when it ends; lift
    /// every finger and release every key first.
    /// </summary>
    public async ValueTask<ApiResult<ApiSession>> TryOpenSessionAsync(CancellationToken cancellationToken = default)
    {
        var started = _time.GetTimestamp();
        if (!_isApiAllowed())
        {
            return Finish(ApiResult.Fail<ApiSession>(PurchaseRequired()));
        }

        var admission = Admit(Needs.Session | Needs.Input);
        if (admission.Error is not null)
        {
            return Finish(ApiResult.Fail<ApiSession>(admission.Error));
        }

        var handle = admission.Handle!;
        IInputLease? lease = null;
        try
        {
            lease = await AcquireInputAsync(handle, cancellationToken).ConfigureAwait(false);
            var session = new ApiSession(this, handle, lease, started, cancellationToken);
            lease = null; // owned by the session now
            handle = null;
            return Finish(ApiResult.Ok<ApiSession>(session));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || handle?.Token.IsCancellationRequested == true)
        {
            return Finish(ApiResult.Fail<ApiSession>(ApiError.Create(ApiErrorCodes.Cancelled, "the request was cancelled")));
        }
        catch (InputRefusedException ex)
        {
            return Finish(ApiResult.Fail<ApiSession>(ApiError.Create(ex.Code, ex.Message)));
        }
        finally
        {
            try
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                if (handle is not null)
                {
                    Release(handle);
                }
            }
        }

        ApiResult<ApiSession> Finish(ApiResult<ApiSession> result)
        {
            LogResult("session-open", result.Error?.Code ?? "ok", started);
            return result;
        }
    }

    // ---- Touch ---------------------------------------------------------------------------------------------

    /// <summary><c>POST /v1/touch/tap</c>: <c>count</c> taps (1..<see cref="MaxTapCount"/>), each held at least 70 ms.</summary>
    public async ValueTask<ApiResult> TapAsync(TapRequest? request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("tap", () =>
        {
            Require(request, "a body is required");
            var spec = CoordinateSpec.Parse(request.Space, request.Ref).OrThrow();
            var unit = spec.ToUnit(request.X, request.Y).OrThrow();
            var hold = Math.Max(NonNegative(request.HoldMs, DefaultTapHoldMs, "holdMs"), (int)Timing.MinHold.TotalMilliseconds);
            var count = request.Count ?? 1;
            if (count is < 1 or > MaxTapCount)
            {
                throw Invalid($"count must be 1 to {MaxTapCount}");
            }

            var interval = Math.Max(NonNegative(request.IntervalMs, DefaultTapIntervalMs, "intervalMs"), (int)Timing.MinGap.TotalMilliseconds);
            LimitTotal(((long)count * hold) + ((long)(count - 1) * interval));
            return new OpSpec<bool>(TouchNeeds(spec), state =>
            {
                var at = spec.Bind(state).OrThrow().ToAxis(unit);
                return async ct =>
                {
                    var player = new TouchPlayer(_host.Touch, _time);
                    for (var i = 0; i < count; i++)
                    {
                        if (i > 0)
                        {
                            await Task.Delay(TimeSpan.FromMilliseconds(interval), _time, ct).ConfigureAwait(false);
                        }

                        await player.TapAsync(at, TimeSpan.FromMilliseconds(hold), ct).ConfigureAwait(false);
                    }

                    return true;
                };
            });
        }, cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    /// <summary><c>POST /v1/touch/long-press</c>: one finger held for <c>holdMs</c> (default 800).</summary>
    public async ValueTask<ApiResult> LongPressAsync(LongPressRequest? request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("long-press", () =>
        {
            Require(request, "a body is required");
            var spec = CoordinateSpec.Parse(request.Space, request.Ref).OrThrow();
            var unit = spec.ToUnit(request.X, request.Y).OrThrow();
            var hold = Math.Max(NonNegative(request.HoldMs, DefaultLongPressHoldMs, "holdMs"), (int)Timing.MinHold.TotalMilliseconds);
            LimitTotal(hold);
            return new OpSpec<bool>(TouchNeeds(spec), state =>
            {
                var at = spec.Bind(state).OrThrow().ToAxis(unit);
                return async ct =>
                {
                    await new TouchPlayer(_host.Touch, _time).TapAsync(at, TimeSpan.FromMilliseconds(hold), ct).ConfigureAwait(false);
                    return true;
                };
            });
        }, cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    /// <summary>
    /// <c>POST /v1/touch/swipe</c>: one finger down at <c>from</c>, stays there <c>holdBeforeMs</c>, moves linearly to
    /// <c>to</c> in <c>durationMs</c> (a frame every 16 ms), stays there <c>holdAfterMs</c>, lifts at <c>to</c>.
    /// </summary>
    public async ValueTask<ApiResult> SwipeAsync(SwipeRequest? request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("swipe", () =>
        {
            Require(request, "a body is required");
            Require(request.From, "from is required");
            Require(request.To, "to is required");
            var spec = CoordinateSpec.Parse(request.Space, request.Ref).OrThrow();
            var from = spec.ToUnit(request.From.X, request.From.Y).OrThrow();
            var to = spec.ToUnit(request.To.X, request.To.Y).OrThrow();
            var duration = NonNegative(request.DurationMs, DefaultSwipeDurationMs, "durationMs");
            var before = NonNegative(request.HoldBeforeMs, 0, "holdBeforeMs");
            var after = NonNegative(request.HoldAfterMs, 0, "holdAfterMs");
            var total = (long)duration + before + after;
            LimitTotal(total);
            return new OpSpec<bool>(TouchNeeds(spec), state =>
            {
                var frame = spec.Bind(state).OrThrow();
                var a = frame.ToAxis(from);
                var b = frame.ToAxis(to);
                var timeline = new Timeline([(0, a), (before, a), (before + duration, b), (total, b)]);
                return async ct =>
                {
                    await PlayAsync(new TouchPlayer(_host.Touch, _time), total, t => (timeline.At(t), null), ct).ConfigureAwait(false);
                    return true;
                };
            });
        }, cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    /// <summary>
    /// <c>POST /v1/touch/path</c>: one finger along <c>points</c> (1..<see cref="MaxPathPoints"/>; <c>t</c> in ms
    /// since the touch, not decreasing, the first at least 0, the last at most 60 s). A frame goes out every 16 ms
    /// at the position interpolated between the points, and the last frame is the last point. If the first
    /// <c>t</c> is above 0 the finger waits at the first point until then.
    /// </summary>
    public async ValueTask<ApiResult> PathAsync(PathRequest? request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("path", () =>
        {
            Require(request, "a body is required");
            Require(request.Points, "points is required");
            if (request.Points.Count is < 1 or > MaxPathPoints)
            {
                throw Invalid($"points must have 1 to {MaxPathPoints} entries");
            }

            var spec = CoordinateSpec.Parse(request.Space, request.Ref).OrThrow();
            var units = new List<(double T, PointD Unit)>(request.Points.Count);
            var previous = 0;
            foreach (var p in request.Points)
            {
                Require(p, "a point is null");
                if (p.T < previous)
                {
                    throw Invalid("t must not be negative or decrease from point to point");
                }

                previous = p.T;
                units.Add((p.T, spec.ToUnit(p.X, p.Y).OrThrow()));
            }

            LimitTotal(previous);
            return new OpSpec<bool>(TouchNeeds(spec), state =>
            {
                var frame = spec.Bind(state).OrThrow();
                var keys = new List<(double T, AxisPoint P)>(units.Count + 1);
                if (units[0].T > 0)
                {
                    keys.Add((0, frame.ToAxis(units[0].Unit)));
                }

                keys.AddRange(units.Select(u => (u.T, frame.ToAxis(u.Unit))));
                var timeline = new Timeline(keys);
                return async ct =>
                {
                    await PlayAsync(new TouchPlayer(_host.Touch, _time), previous, t => (timeline.At(t), null), ct).ConfigureAwait(false);
                    return true;
                };
            });
        }, cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    /// <summary>
    /// <c>POST /v1/touch/pinch</c>: two fingers placed horizontally in the request's space around <c>center</c>,
    /// each <c>radius</c> from it, the radius going from <c>startRadius</c> to <c>endRadius</c> in <c>durationMs</c>.
    /// The radius is a fraction of the screen's SHORT side: in <c>screen</c> space it is converted to x with the
    /// picture's aspect, in <c>pixel</c> space with the aspect of <c>ref</c>; in <c>device</c> space it is the
    /// fraction of the digitizer's x axis, which always spans the device's short side (no picture is needed). A
    /// finger that would leave the screen at the start or the end is <c>invalid_request</c>.
    /// </summary>
    public async ValueTask<ApiResult> PinchAsync(PinchRequest? request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("pinch", () =>
        {
            Require(request, "a body is required");
            Require(request.Center, "center is required");
            var spec = CoordinateSpec.Parse(request.Space, request.Ref).OrThrow();
            var center = spec.ToUnit(request.Center.X, request.Center.Y).OrThrow();
            if (!double.IsFinite(request.StartRadius) || !double.IsFinite(request.EndRadius) || request.StartRadius < 0 || request.EndRadius < 0)
            {
                throw Invalid("startRadius and endRadius must be 0 or more");
            }

            var duration = NonNegative(request.DurationMs, DefaultPinchDurationMs, "durationMs");
            LimitTotal(duration);
            var total = Math.Max(duration, (int)Timing.MinHold.TotalMilliseconds);
            return new OpSpec<bool>(TouchNeeds(spec), state =>
            {
                var frame = spec.Bind(state).OrThrow();
                var (startHalf, endHalf) = (frame.HalfWidth(request.StartRadius), frame.HalfWidth(request.EndRadius));
                foreach (var half in new[] { startHalf, endHalf })
                {
                    if (center.X - half < 0 || center.X + half > 1)
                    {
                        throw Invalid("a finger of the pinch would leave the screen");
                    }
                }

                return async ct =>
                {
                    await PlayAsync(new TouchPlayer(_host.Touch, _time), total, t =>
                    {
                        var f = duration == 0 ? 1.0 : Math.Min(t / duration, 1.0);
                        var half = startHalf + ((endHalf - startHalf) * f);
                        return (frame.ToAxis(new PointD(center.X - half, center.Y)), frame.ToAxis(new PointD(center.X + half, center.Y)));
                    }, ct).ConfigureAwait(false);
                    return true;
                };
            });
        }, cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    // ---- Keys and home -------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /v1/keys/press</c>: one key (names of chapter 12 of the design) with modifiers, held for
    /// <c>holdMs</c> (default 30; unlike touches it is not raised to 70).
    /// </summary>
    public async ValueTask<ApiResult> PressKeyAsync(KeyPressRequest? request, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("key-press", () =>
        {
            Require(request, "a body is required");
            if (!ApiKeys.TryParse(request.Key, request.Modifiers, out var key, out var error))
            {
                throw Invalid(error);
            }

            var hold = NonNegative(request.HoldMs, DefaultKeyHoldMs, "holdMs");
            LimitTotal((long)hold + KeyPressOverheadMs);
            return new OpSpec<bool>(Needs.Session | Needs.Input | Needs.Keyboard, _ => async ct =>
            {
                await _host.Keyboard.PressAsync(key, TimeSpan.FromMilliseconds(hold), cancellationToken: ct, time: _time).ConfigureAwait(false);
                return true;
            });
        }, cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    /// <summary>
    /// <c>POST /v1/keys/type</c>: types <c>text</c> on a US keyboard (at most <see cref="MaxTextLength"/> characters).
    /// The estimated time (per typed character 30 ms hold + <c>intervalMs</c>, plus 30 ms when it needs Shift) must stay
    /// within 60 s, else <c>invalid_request</c>. The characters without a key (Japanese, emoji...) are skipped, cost
    /// nothing and are returned.
    /// </summary>
    public async ValueTask<ApiResult<TypeTextResponse>> TypeTextAsync(TypeTextRequest? request, CancellationToken cancellationToken = default) =>
        await ExecuteAsync("key-type", () =>
        {
            Require(request, "a body is required");
            Require(request.Text, "text is required");
            if (request.Text.Length > MaxTextLength)
            {
                throw Invalid($"text must have at most {MaxTextLength} characters");
            }

            var interval = NonNegative(request.IntervalMs, DefaultTypeIntervalMs, "intervalMs");
            if (interval > MaxTypeIntervalMs)
            {
                throw Invalid($"intervalMs must be at most {MaxTypeIntervalMs}");
            }

            var text = request.Text;
LimitTotal(EstimateTypingMs(text, interval), "split the text into several requests (an operation can take at most 60 seconds)");
            return new OpSpec<TypeTextResponse>(Needs.Session | Needs.Input | Needs.Keyboard, _ => async ct =>
            {
                var skipped = await _host.Keyboard.TypeAsync(text, HidKeyboardLayout.Us, TimeSpan.FromMilliseconds(interval), cancellationToken: ct, time: _time).ConfigureAwait(false);
                return new TypeTextResponse(skipped);
            });
        }, cancellationToken).ConfigureAwait(false);

    /// <summary><c>POST /v1/home</c>: the home button.</summary>
    public async ValueTask<ApiResult> HomeAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync("home", () =>
            new OpSpec<bool>(Needs.Session | Needs.Input | Needs.HomeButton, _ => async ct =>
            {
                await _host.HomeButton.PressHomeAsync(ct).ConfigureAwait(false);
                return true;
            }), cancellationToken).ConfigureAwait(false);
        return result.WithoutValue();
    }

    // ---- Machinery -----------------------------------------------------------------------------------------

    internal void Release(Handle handle)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, handle))
            {
                _current = null;
            }
        }

        handle.Dispose();
    }

    /// <summary>
    /// Logs the outcome of an operation. Successes and the two ways an operation is stopped (user, cancel) are
    /// logged one line each; every refusal (busy, user_active, invalid_request, ...) goes through a limiter (first line
    /// at once, then a count per 10 s), so a script in a loop cannot flood the log.
    /// </summary>
    internal void LogResult(string kind, string code, long startedTimestamp)
    {
        if (_log is null)
        {
            return;
        }

        if (code is "ok" or "closed" or ApiErrorCodes.UserInterrupted or ApiErrorCodes.Cancelled)
        {
            _log($"[api] {kind} {code} {(long)_time.GetElapsedTime(startedTimestamp).TotalMilliseconds} ms");
        }
        else
        {
            _refusals.Record($"{kind} {code}");
        }
    }

    /// <summary>The state to convert coordinates for a message of an open session.</summary>
    internal ApiHostSnapshot Snapshot() => _host.GetSnapshot();

    private static ApiError PurchaseRequired() =>
        ApiError.Create(ApiErrorCodes.PurchaseRequired, "the API is available after purchase");

    private static RotationInfo ToInfo(ApiHostSnapshot state) =>
        new((int)state.LandscapeRotation, (int)state.PortraitRotation);

    private static Needs TouchNeeds(CoordinateSpec spec) =>
        Needs.Session | Needs.Input | (spec.NeedsPicture ? Needs.Picture : Needs.None);

    private static ApiRequestException Invalid(string detail) => new(ApiError.InvalidRequest(detail));

    private static void Require([System.Diagnostics.CodeAnalysis.NotNull] object? value, string detail)
    {
        if (value is null)
        {
            throw Invalid(detail);
        }
    }

    private static int NonNegative(int? value, int fallback, string name)
    {
        var v = value ?? fallback;
        return v < 0 ? throw Invalid($"{name} must not be negative") : v;
    }

    private static void LimitTotal(long totalMs, string? detail = null)
    {
        if (totalMs > MaxDurationMs)
        {
            throw Invalid(detail ?? $"an operation can take at most {MaxDurationMs / 1000} seconds");
        }
    }

    // What KeyboardTyping.TypeAsync takes for text: per typed character the key hold (30 ms, as PressAsync) plus the
    // interval, plus two modifier gaps (2 x 15 ms) when the key needs a modifier (Shift). Characters without a key
    // are skipped and cost nothing.
    private static long EstimateTypingMs(string text, int intervalMs)
    {
        long total = 0;
        foreach (var c in text.ReplaceLineEndings("\n"))
        {
            if (HidKeyboard.TryMap(c, HidKeyboardLayout.Us, out var key))
            {
                total += DefaultKeyHoldMs + intervalMs + (key.Modifiers != 0 ? KeyPressOverheadMs : 0);
            }
        }

        return total;
    }

    /// <summary>
    /// Runs one operation: license, the request, the state, the gate, the lease; then the job; then lifts (the
    /// job's own finally) and gives the input back.
    /// </summary>
    private async ValueTask<ApiResult<T>> ExecuteAsync<T>(string kind, Func<OpSpec<T>> parse, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        ApiResult<T> result;
        try
        {
            result = await RunAsync(parse, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _refusals.Record($"{kind} failed");
            throw;
        }

        LogResult(kind, result.Error?.Code ?? "ok", started);
        return result;
    }

    private async ValueTask<ApiResult<T>> RunAsync<T>(Func<OpSpec<T>> parse, CancellationToken cancellationToken)
    {
        if (!_isApiAllowed())
        {
            return ApiResult.Fail<T>(PurchaseRequired());
        }

        OpSpec<T> spec;
        try
        {
            spec = parse();
        }
        catch (ApiRequestException ex)
        {
            return ApiResult.Fail<T>(ex.Error);
        }

        var admission = Admit(spec.Needs);
        if (admission.Error is not null)
        {
            return ApiResult.Fail<T>(admission.Error);
        }

        var handle = admission.Handle!;
        IInputLease? lease = null;
        CancellationTokenRegistration callerRegistration = default;
        CancellationTokenRegistration userRegistration = default;
        try
        {
            Func<CancellationToken, ValueTask<T>> job;
            try
            {
                job = spec.Resolve(admission.State!);
            }
            catch (ApiRequestException ex)
            {
                return ApiResult.Fail<T>(ex.Error);
            }

            if ((spec.Needs & Needs.Input) != 0)
            {
                lease = await AcquireInputAsync(handle, cancellationToken).ConfigureAwait(false);
            }

            // The caller's token and the user's input fire on whatever thread cancels them (Kestrel, the mouse hook,
            // the UI): they only queue the cancellation of the handle, so the lift runs on the thread pool.
            callerRegistration = handle.CancelOn(cancellationToken);
            if (lease is not null)
            {
                userRegistration = handle.CancelOn(lease.UserInput);
            }

            var token = handle.Token;
            try
            {
                if (cancellationToken.IsCancellationRequested || lease?.UserInput.IsCancellationRequested == true)
                {
                    throw new OperationCanceledException(token);
                }

                return ApiResult.Ok<T>(await job(token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || cancellationToken.IsCancellationRequested || lease?.UserInput.IsCancellationRequested == true)
            {
                // The job has lifted every finger and released every key by now (their finally blocks).
                return ApiResult.Fail<T>(InterruptionError(lease));
            }
            catch (ApiRequestException ex)
            {
                return ApiResult.Fail<T>(ex.Error);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || handle.Token.IsCancellationRequested)
        {
            // The caller went away while the lease was being taken.
            return ApiResult.Fail<T>(ApiError.Create(ApiErrorCodes.Cancelled, "the request was cancelled"));
        }
        catch (InputRefusedException ex)
        {
            // The bridge refused the input: the user is in the middle of a gesture, or the bridge is suspended or busy.
            return ApiResult.Fail<T>(ApiError.Create(ex.Code, ex.Message));
        }
        finally
        {
            callerRegistration.Dispose();
            userRegistration.Dispose();
            try
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                // The gate opens even when giving the input back fails.
                Release(handle);
            }
        }
    }

    // The handle's token joins the caller's: POST /v1/cancel or a server stop (CancelCurrent, Dispose) also ends a
    // wait for the bridge's answer, so the gate always opens again.
    private async ValueTask<IInputLease> AcquireInputAsync(Handle handle, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, handle.Token);
        return await _host.AcquireInputAsync(linked.Token).ConfigureAwait(false);
    }

    /// <summary>Why a cancelled operation stopped: the user wins over a cancel.</summary>
    internal static ApiError InterruptionError(IInputLease? lease) =>
        lease is { UserInput.IsCancellationRequested: true }
            ? ApiError.Create(ApiErrorCodes.UserInterrupted, "the user used the mirror; every finger and key was released")
            : ApiError.Create(ApiErrorCodes.Cancelled, "the operation was cancelled; every finger and key was released");

    /// <summary>
    /// The preconditions in their fixed order, from <c>not_running</c> on, and the gate. On success the caller owns
    /// the returned handle (and must <see cref="Release"/> it).
    /// </summary>
    private Admission Admit(Needs needs)
    {
        var state = _host.GetSnapshot();
        if ((needs & Needs.Session) != 0)
        {
            if (!state.Running)
            {
                return Refuse(ApiErrorCodes.NotRunning, "the mirroring session is not running");
            }

            if (state.ProbeSuspended)
            {
                return Refuse(ApiErrorCodes.Busy, "a measurement is running");
            }
        }

        Handle handle;
        lock (_gate)
        {
            if (_current is not null)
            {
                return Refuse(ApiErrorCodes.Busy, "another operation or session is running");
            }

            handle = _current = new Handle();
        }

        var error = CheckState(needs, state);
        if (error is not null)
        {
            Release(handle);
            return new Admission(error, null, null);
        }

        return new Admission(null, handle, state);
    }

    private ApiError? CheckState(Needs needs, ApiHostSnapshot state)
    {
        if ((needs & Needs.Input) != 0)
        {
            if (state.UserInputActive)
            {
                return ApiError.Create(ApiErrorCodes.UserActive, "the user is holding the mouse or a key on the mirror");
            }

            if (state.LastUserInput is { } last && _time.GetUtcNow() - last < UserActiveWindow)
            {
                return ApiError.Create(ApiErrorCodes.UserActive, "the user used the mirror a moment ago");
            }

            if (!state.Subscribed)
            {
                return ApiError.Create(ApiErrorCodes.NotConnected, "the target device is not connected");
            }
        }

        if ((needs & Needs.Picture) != 0 && state.Picture is null)
        {
            return ApiError.Create(ApiErrorCodes.NoPicture, "there is no picture of the device");
        }

        if ((needs & Needs.Keyboard) != 0 && !state.HasKeyboard)
        {
            return ApiError.Create(ApiErrorCodes.NoKeyboard, "the published HID map has no keyboard");
        }

        if ((needs & Needs.HomeButton) != 0 && !state.HasHomeButton)
        {
            return ApiError.Create(ApiErrorCodes.NoHomeButton, "the published HID map has no home button");
        }

        return null;
    }

    private static Admission Refuse(string code, string detail) => new(ApiError.Create(code, detail), null, null);

    /// <summary>
    /// Sends frames every 16 ms (<see cref="TouchTiming.MoveInterval"/>) for <paramref name="totalMs"/>, the last
    /// one at exactly the end, then lifts at the last position (also when cancelled). If sending falls behind, frames
    /// are dropped (the position follows the clock) instead of queueing. The finger stays down at least
    /// <see cref="TouchTiming.MinHold"/>.
    /// </summary>
    private async ValueTask PlayAsync(TouchPlayer player, double totalMs, Func<double, (AxisPoint First, AxisPoint? Second)> at, CancellationToken cancellationToken)
    {
        totalMs = Math.Max(totalMs, Timing.MinHold.TotalMilliseconds);
        var interval = Timing.MoveInterval.TotalMilliseconds;
        var start = _time.GetTimestamp();
        try
        {
            var t = 0.0;
            while (true)
            {
                var (first, second) = at(t);
                if (second is { } other)
                {
                    await player.DownAsync(first, other, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await player.DownAsync(first, cancellationToken).ConfigureAwait(false);
                }

                if (t >= totalMs)
                {
                    break;
                }

                var next = Math.Min(t + interval, totalMs);
                var elapsed = _time.GetElapsedTime(start).TotalMilliseconds;
                if (next > elapsed)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(next - elapsed), _time, cancellationToken).ConfigureAwait(false);
                    elapsed = _time.GetElapsedTime(start).TotalMilliseconds;
                }

                t = Math.Min(Math.Max(next, elapsed), totalMs);
            }
        }
        finally
        {
            await player.LiftAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [Flags]
    private enum Needs
    {
        None = 0,

        // Running, no probe measurement.
        Session = 1,

        // The input lease, the user not active, the device connected.
        Input = 2,
        Picture = 4,
        Keyboard = 8,
        HomeButton = 16,
    }

    private readonly record struct Admission(ApiError? Error, Handle? Handle, ApiHostSnapshot? State);

    private sealed record OpSpec<T>(Needs Needs, Func<ApiHostSnapshot, Func<CancellationToken, ValueTask<T>>> Resolve);

    /// <summary>Linear interpolation between keyframes (time in ms, not decreasing), for increasing times.</summary>
    private sealed class Timeline(IReadOnlyList<(double T, AxisPoint P)> keys)
    {
        private readonly (double T, AxisPoint P)[] _keys = [.. keys];
        private int _index = 1;

        public AxisPoint At(double t)
        {
            if (t <= _keys[0].T)
            {
                return _keys[0].P;
            }

            while (_index < _keys.Length && t > _keys[_index].T)
            {
                _index++;
            }

            if (_index >= _keys.Length)
            {
                return _keys[^1].P;
            }

            var (t0, p0) = _keys[_index - 1];
            var (t1, p1) = _keys[_index];
            if (t1 <= t0)
            {
                return p1;
            }

            var f = (t - t0) / (t1 - t0);
            return new AxisPoint(Lerp(p0.X, p1.X, f), Lerp(p0.Y, p1.Y, f));
        }

        private static ushort Lerp(ushort a, ushort b, double f) => (ushort)Math.Round(a + ((b - a) * f));
    }

    /// <summary>
    /// The running operation or session. Its token fires on <see cref="ApiController.CancelCurrent"/>, on the caller's
    /// token and on the user's input (see <see cref="CancelOn"/>). Cancelling never runs the code that waits on the
    /// token (the lift, the lease's disposal) on the thread that asked: it is queued to the thread pool.
    /// </summary>
    internal sealed class Handle : IDisposable
    {
        private readonly Lock _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private bool _disposed;

        public Handle() => Token = _cts.Token;

        /// <summary>The token of the operation; valid (and readable) after <see cref="Dispose"/> too.</summary>
        public CancellationToken Token { get; }

        /// <summary>Queues the cancellation to the thread pool; does nothing once the handle is disposed.</summary>
        public void Cancel() => ThreadPool.UnsafeQueueUserWorkItem(static handle => handle.CancelNow(), this, preferLocal: false);

        /// <summary>Cancels <see cref="Token"/> when <paramref name="source"/> is cancelled (the cancellation itself is queued).</summary>
        public CancellationTokenRegistration CancelOn(CancellationToken source) =>
            source.CanBeCanceled ? source.UnsafeRegister(static state => ((Handle)state!).Cancel(), this) : default;

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _cts.Dispose();
            }
        }

        private void CancelNow()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _cts.Cancel();
                }
            }
        }
    }
}
