// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Runtime.CompilerServices;
using IceScreenTouch.Api.Ocr;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>
/// The text (OCR) endpoints (docs/API-DESIGN.md, chapter 16): <c>read</c> and <c>find</c> only read pixels, like a screenshot (no gate, no
/// <c>busy</c>); <c>tap-text</c> is an operation that searches with the gate held but without the input lease, and takes the lease only to tap.
/// </summary>
/// <remarks>
/// <para>
/// Order of the answers. <c>read</c> and <c>find</c>: license, the body (<c>invalid_request</c>), engine and language (<c>ocr_unavailable</c>),
/// the number waiting (<c>rate_limited</c>), the recognition turn (<c>rate_limited</c> when two already wait), the rate windows (<c>rate_limited</c>:
/// a reading counts as a screenshot and in its own window of <see cref="TextReadsPerSecond"/> a second), <c>not_running</c>, <c>no_picture</c>,
/// the capture, <c>picture_changed</c> (a <c>pixel</c> <c>ref</c> of the other orientation), the recognition. <c>tap-text</c>: license, the body,
/// the preconditions of a tap (<c>not_running</c>, <c>busy</c>, <c>user_active</c>, <c>not_connected</c>, <c>no_picture</c>), then as above.
/// </para>
/// <para>
/// While waiting (<c>timeoutMs</c> above 0) a reading that the turn, a rate window or a busy engine refuses is skipped until the next poll; a
/// stopped session or a missing picture ends the call at once. Logs carry the kind, the result, the engine id and the time: never text,
/// boxes or positions.
/// </para>
/// </remarks>
public sealed partial class ApiController
{
    /// <summary>Text readings in any second, over all clients (they also count as screenshots).</summary>
    public const int TextReadsPerSecond = 5;

    /// <summary><c>find</c> and <c>tap-text</c> calls with <c>timeoutMs</c> above 0 that may run at once; one more is refused.</summary>
    public const int MaxTextWaits = 2;

    /// <summary>Characters of the searched text.</summary>
    public const int MaxSearchTextLength = 200;

    /// <summary>The longest <c>timeoutMs</c>.</summary>
    public const int MaxTextTimeoutMs = 30_000;

    /// <summary>The shortest <c>pollMs</c>.</summary>
    public const int MinPollMs = 300;

    /// <summary>The longest <c>pollMs</c>.</summary>
    public const int MaxPollMs = 5_000;

    /// <summary>The pause between readings while waiting, by default.</summary>
    public const int DefaultPollMs = 500;

    /// <summary>The pause before the reading that confirms a match, by default.</summary>
    public const int DefaultSettleMs = 250;

    /// <summary>The longest <c>settleMs</c>.</summary>
    public const int MaxSettleMs = 2_000;

    /// <summary>Candidates listed in a <c>text_not_found</c>, <c>ambiguous_text</c> or <c>tap_outside_screen</c> answer.</summary>
    public const int MaxTextCandidates = 20;

    /// <summary>The confirming reading finds the "same" match when its centre moved at most this fraction of the screen's short side.</summary>
    public const double SameMatchDistance = 0.02;

    /// <summary>One reading of <c>read</c> and <c>find</c> ends after this long (a capture or an engine that does not answer).</summary>
    public static readonly TimeSpan TextReadingTimeout = TimeSpan.FromSeconds(5);

    // tap-text: the search ends at the latest after timeoutMs + settleMs + this (two readings' worth), whatever the capture or the engine do.
    private const int TextReadAllowanceMs = 2_000;

    private static readonly CoordinateSpec ScreenSpace = CoordinateSpec.Parse(null, null).OrThrow();

    private readonly ScreenshotRateLimiter _textLimit;
    private readonly OcrService _ocr;
    private int _textWaits;

    private enum TextPhase
    {
        Waiting,
        Capture,
        Recognize,
    }

    private enum TextSelect
    {
        Unique,
        First,
        Index,
    }

    // ---- read ----------------------------------------------------------------------------------------------

    /// <summary><c>POST /v1/text/read</c>: the text of the screen, lines in reading order with their boxes (and words when asked).</summary>
    public async ValueTask<ApiResult<TextReadResponse>> ReadTextAsync(TextReadRequest? request, CancellationToken cancellationToken = default)
    {
        var started = _time.GetTimestamp();
        string? engine = null;
        ApiResult<TextReadResponse> result;
        try
        {
            result = await ReadTextCoreAsync(request, started, id => engine = id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = ApiResult.Fail<TextReadResponse>(ApiError.Create(ApiErrorCodes.Cancelled, "the request was cancelled"));
        }
        catch
        {
            _refusals.Record("text-read failed");
            throw;
        }

        // Up to five a second: the limiter writes the first line of each kind and then a count every 10 s.
        _refusals.Record(TextLogLine("text-read", result.Error?.Code, engine));
        return result;
    }

    private async ValueTask<ApiResult<TextReadResponse>> ReadTextCoreAsync(TextReadRequest? request, long started, Action<string> setEngine, CancellationToken cancellationToken)
    {
        if (!_isApiAllowed())
        {
            return ApiResult.Fail<TextReadResponse>(PurchaseRequired());
        }

        TextScope scope;
        bool words;
        try
        {
            Require(request, "a body is required");
            scope = ParseScope(request.Lang, request.Engine, request.Space, request.Ref, request.Region);
            words = request.Words ?? false;
        }
        catch (ApiRequestException ex)
        {
            return ApiResult.Fail<TextReadResponse>(ex.Error);
        }

        var (target, unavailable) = await ResolveTargetAsync(scope, cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return ApiResult.Fail<TextReadResponse>(unavailable);
        }

        setEngine(target!.Info.Id);
        var outcome = await ShootWithDeadlineAsync(target, scope, waiting: false, cancellationToken).ConfigureAwait(false);
        if (outcome.Error is { } error)
        {
            return ApiResult.Fail<TextReadResponse>(error);
        }

        var shot = outcome.Shot!;
        return ApiResult.Ok(new TextReadResponse(
            shot.Reading.Language,
            EngineOf(shot.Reading.Engine),
            PictureOf(shot.Frame),
            SpaceName(scope.Space),
            ElapsedMs(started),
            [.. shot.Reading.Lines.Select(l => LineOf(l, shot.Frame, scope.Space, words))]));
    }

    // ---- find ----------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /v1/text/find</c>: the matches of the text, reading again every <c>pollMs</c> until one is found or <c>timeoutMs</c> has passed (then
    /// an empty list, not an error). Not an operation: <c>POST /v1/cancel</c> does not stop it; the client going away does.
    /// </summary>
    public async ValueTask<ApiResult<TextFindResponse>> FindTextAsync(TextFindRequest? request, CancellationToken cancellationToken = default)
    {
        var started = _time.GetTimestamp();
        string? engine = null;
        ApiResult<TextFindResponse> result;
        try
        {
            result = await FindTextCoreAsync(request, started, id => engine = id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            result = ApiResult.Fail<TextFindResponse>(ApiError.Create(ApiErrorCodes.Cancelled, "the request was cancelled"));
        }
        catch
        {
            _refusals.Record("text-find failed");
            throw;
        }

        _refusals.Record(TextLogLine("text-find", result.Error?.Code, engine));
        return result;
    }

    private async ValueTask<ApiResult<TextFindResponse>> FindTextCoreAsync(TextFindRequest? request, long started, Action<string> setEngine, CancellationToken cancellationToken)
    {
        if (!_isApiAllowed())
        {
            return ApiResult.Fail<TextFindResponse>(PurchaseRequired());
        }

        TextScope scope;
        TextQuery query;
        try
        {
            Require(request, "a body is required");
            scope = ParseScope(request.Lang, request.Engine, request.Space, request.Ref, request.Region);
            query = ParseQuery(request.Text, request.Match, request.Loose, request.CaseSensitive, request.TimeoutMs, request.PollMs);
        }
        catch (ApiRequestException ex)
        {
            return ApiResult.Fail<TextFindResponse>(ex.Error);
        }

        var (target, unavailable) = await ResolveTargetAsync(scope, cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return ApiResult.Fail<TextFindResponse>(unavailable);
        }

        setEngine(target!.Info.Id);
        if (!TryCountWait(query))
        {
            return ApiResult.Fail<TextFindResponse>(TooManyWaits());
        }

        try
        {
            var searchStart = _time.GetTimestamp();
            var attempts = 0;
            TextShot? last = null;
            while (true)
            {
                var outcome = await ShootWithDeadlineAsync(target, scope, waiting: attempts > 0, cancellationToken).ConfigureAwait(false);
                if (outcome.Error is { } error)
                {
                    return ApiResult.Fail<TextFindResponse>(error);
                }

                if (outcome.Shot is { } shot)
                {
                    attempts++;
                    last = shot;
                    var matches = TextMatcher.Find(shot.Reading.Lines, query.Text, query.Mode, query.Options);
                    if (matches.Count > 0)
                    {
                        return ApiResult.Ok(FindResponse(shot, scope, started, attempts, matches));
                    }
                }

                var remaining = query.TimeoutMs - _time.GetElapsedTime(searchStart).TotalMilliseconds;
                if (remaining <= 0 && last is not null)
                {
                    return ApiResult.Ok(FindResponse(last, scope, started, attempts, []));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(remaining, 1, query.PollMs)), _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            ReleaseWait(query);
        }
    }

    private TextFindResponse FindResponse(TextShot shot, TextScope scope, long started, int attempts, IReadOnlyList<TextMatch> matches) =>
        new(
            shot.Reading.Language,
            EngineOf(shot.Reading.Engine),
            PictureOf(shot.Frame),
            SpaceName(scope.Space),
            ElapsedMs(started),
            attempts,
            [.. matches.Select(m => MatchOf(m, shot.Frame, scope.Space))]);

    // ---- tap-text ------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>POST /v1/touch/tap-text</c>: finds the text (waiting up to <c>timeoutMs</c>), confirms it with a second reading after <c>settleMs</c>,
    /// then taps its centre (plus <c>offset</c>) like <c>POST /v1/touch/tap</c>. While it searches it holds the gate but not the input lease: the
    /// user's mouse keeps working, and using the mirror ends the call with <c>user_interrupted</c> (nothing is tapped). The point is decided again
    /// with the state after the lease was taken; a rotation since the confirming picture is <c>picture_changed</c>.
    /// </summary>
    public async ValueTask<ApiResult<TapTextResponse>> TapTextAsync(TapTextRequest? request, CancellationToken cancellationToken = default)
    {
        var started = _time.GetTimestamp();
        string? engine = null;
        ApiResult<TapTextResponse> result;
        try
        {
            result = await TapTextCoreAsync(request, started, id => engine = id, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _refusals.Record("text-tap failed");
            throw;
        }

        LogResult("text-tap", result.Error?.Code ?? "ok", started, engine);
        return result;
    }

    private async ValueTask<ApiResult<TapTextResponse>> TapTextCoreAsync(TapTextRequest? request, long started, Action<string> setEngine, CancellationToken cancellationToken)
    {
        if (!_isApiAllowed())
        {
            return ApiResult.Fail<TapTextResponse>(PurchaseRequired());
        }

        TextScope scope;
        TextQuery query;
        TextSelect select;
        int index;
        PointD offset;
        int settle, hold, count, interval;
        try
        {
            Require(request, "a body is required");
            scope = ParseScope(request.Lang, request.Engine, request.Space, request.Ref, request.Region);
            query = ParseQuery(request.Text, request.Match, request.Loose, request.CaseSensitive, request.TimeoutMs, request.PollMs);
            select = request.Select?.ToUpperInvariant() switch
            {
                null or "UNIQUE" => TextSelect.Unique,
                "FIRST" => TextSelect.First,
                "INDEX" => TextSelect.Index,
                _ => throw Invalid("select must be unique, first or index"),
            };
            if (select == TextSelect.Index)
            {
                index = request.Index is { } i && i >= 0 ? i : throw Invalid("select index needs index (0 or more)");
            }
            else
            {
                index = request.Index is null ? 0 : throw Invalid("index only applies to select index");
            }

            offset = ParseOffset(request.Offset, scope.Space);
            settle = request.SettleMs ?? DefaultSettleMs;
            if (settle is < 0 or > MaxSettleMs)
            {
                throw Invalid($"settleMs must be 0 to {MaxSettleMs}");
            }

            hold = Math.Max(NonNegative(request.HoldMs, DefaultTapHoldMs, "holdMs"), (int)Timing.MinHold.TotalMilliseconds);
            count = request.Count ?? 1;
            if (count is < 1 or > MaxTapCount)
            {
                throw Invalid($"count must be 1 to {MaxTapCount}");
            }

            interval = Math.Max(NonNegative(request.IntervalMs, DefaultTapIntervalMs, "intervalMs"), (int)Timing.MinGap.TotalMilliseconds);
            LimitTotal((long)query.TimeoutMs + settle + TextReadAllowanceMs + ((long)count * hold) + ((long)(count - 1) * interval));
        }
        catch (ApiRequestException ex)
        {
            return ApiResult.Fail<TapTextResponse>(ex.Error);
        }

        var admission = Admit(Needs.Session | Needs.Input | Needs.Picture);
        if (admission.Error is not null)
        {
            return ApiResult.Fail<TapTextResponse>(admission.Error);
        }

        var handle = admission.Handle!;
        var admitted = admission.State!;

        // From here on the caller going away (or POST /v1/cancel) ends the search and opens the gate.
        var callerRegistration = handle.CancelOn(cancellationToken);
        CancellationTokenRegistration userRegistration = default;
        IInputLease? lease = null;
        var countedWait = false;
        var phase = new StrongBox<TextPhase>(TextPhase.Waiting);
        using var searchTimer = new CancellationTokenSource(TimeSpan.FromMilliseconds(query.TimeoutMs + settle + TextReadAllowanceMs), _time);
        using var search = CancellationTokenSource.CreateLinkedTokenSource(handle.Token, searchTimer.Token);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (target, unavailable) = await ResolveTargetAsync(scope, search.Token).ConfigureAwait(false);
            if (unavailable is not null)
            {
                return ApiResult.Fail<TapTextResponse>(unavailable);
            }

            setEngine(target!.Info.Id);
            if (!TryCountWait(query))
            {
                return ApiResult.Fail<TapTextResponse>(TooManyWaits());
            }

            countedWait = query.TimeoutMs > 0;
            var found = await SearchToTapAsync(target, scope, query, select, index, settle, admitted, phase, search.Token).ConfigureAwait(false);
            if (found.Error is { } searchError)
            {
                return ApiResult.Fail<TapTextResponse>(searchError);
            }

            var (shot, match, matches, attempts) = found.Value;

            // The point on the confirming picture, in screen units of that picture.
            var unit = new PointD((match.Box.CenterX / shot.Frame.Width) + offset.X, (match.Box.CenterY / shot.Frame.Height) + offset.Y);
            if (unit.X is < 0 or > 1 || unit.Y is < 0 or > 1)
            {
                return ApiResult.Fail<TapTextResponse>(CandidatesError(ApiErrorCodes.TapOutsideScreen, "the offset puts the tap outside the screen; nothing was tapped", shot, scope, matches));
            }

            // The lease only now: the user's mouse worked while the text was searched.
            phase.Value = TextPhase.Waiting;
            try
            {
                lease = await AcquireInputAsync(handle, cancellationToken).ConfigureAwait(false);
            }
            catch (InputRefusedException ex)
            {
                // The user is in the middle of a gesture: they touched the mirror after the search began.
                return ApiResult.Fail<TapTextResponse>(ex.Code == ApiErrorCodes.UserActive ? UserInterruptedSearch() : ApiError.Create(ex.Code, ex.Message));
            }

            var guard = new InputGuard(_host) { Lease = lease };
            userRegistration = handle.CancelOn(lease.UserInput);
            var now = _host.GetSnapshot();
            if (lease.UserInput.IsCancellationRequested || UserTouched(admitted, now))
            {
                return ApiResult.Fail<TapTextResponse>(UserInterruptedSearch());
            }

            if (StateError(now) is { } stateError)
            {
                return ApiResult.Fail<TapTextResponse>(stateError);
            }

            // Decided again on the state after the lease: the picture the match was found on must still be how the screen is seen and rotated.
            var rotationNow = shot.Frame.IsLandscape ? now.LandscapeRotation : now.PortraitRotation;
            if (now.Picture!.IsLandscape != shot.Frame.IsLandscape || rotationNow != shot.Frame.Rotation || RefTurned(scope.Space, now.Picture.IsLandscape))
            {
                return ApiResult.Fail<TapTextResponse>(ApiError.Create(ApiErrorCodes.PictureChanged, "the screen rotated (or the assumed rotation changed) while the text was searched; nothing was tapped"));
            }

            var at = ScreenSpace.Bind(now).OrThrow().ToAxis(unit);
            var player = new TouchPlayer(guard.Touch, _time);
            var token = handle.Token;
            for (var i = 0; i < count; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(interval), _time, token).ConfigureAwait(false);
                }

                await player.TapAsync(at, TimeSpan.FromMilliseconds(hold), token).ConfigureAwait(false);
            }

            var tapped = MatchOf(match, shot.Frame, scope.Space);
            return ApiResult.Ok(new TapTextResponse(
                shot.Reading.Language,
                EngineOf(shot.Reading.Engine),
                PictureOf(shot.Frame),
                SpaceName(scope.Space),
                ElapsedMs(started),
                attempts,
                new TextTapped(tapped.Text, tapped.LineText, tapped.Box, tapped.Center, tapped.BoxLevel, PointOf(unit.X * shot.Frame.Width, unit.Y * shot.Frame.Height, shot.Frame, scope.Space), tapped.Confidence),
                matches.Count));
        }
        catch (OperationCanceledException) when (searchTimer.IsCancellationRequested && !handle.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The search ran out of its hard time: a capture that did not come back is no picture, anything else found nothing.
            return ApiResult.Fail<TapTextResponse>(phase.Value == TextPhase.Capture
                ? ApiError.Create(ApiErrorCodes.NoPicture, "the picture of the device could not be captured in time")
                : ApiError.Create(ApiErrorCodes.TextNotFound, "the text was not found in time; nothing was tapped"));
        }
        catch (OperationCanceledException) when (handle.Token.IsCancellationRequested || cancellationToken.IsCancellationRequested || lease?.UserInput.IsCancellationRequested == true)
        {
            // The taps (if any) have lifted the finger by now (their finally blocks).
            return ApiResult.Fail<TapTextResponse>(InterruptionError(lease));
        }
        finally
        {
            callerRegistration.Dispose();
            userRegistration.Dispose();
            if (countedWait)
            {
                _ = Interlocked.Decrement(ref _textWaits);
            }

            try
            {
                if (lease is not null)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                Release(handle);
            }
        }
    }

    // Reads until a match is chosen and confirmed (16.7.2), or the time is up. Every reading first checks the state (as Admit did) and the user.
    private async Task<SearchOutcome> SearchToTapAsync(
        OcrTarget target, TextScope scope, TextQuery query, TextSelect select, int index, int settle, ApiHostSnapshot admitted, StrongBox<TextPhase> phase, CancellationToken cancellationToken)
    {
        var searchStart = _time.GetTimestamp();
        var attempts = 0;
        TextShot? last = null;
        IReadOnlyList<TextMatch> lastMatches = [];
        var lastAmbiguous = false;
        (TextShot Shot, TextMatch Match)? candidate = null;
        while (true)
        {
            var now = _host.GetSnapshot();
            if (UserTouched(admitted, now))
            {
                return SearchOutcome.Fail(UserInterruptedSearch());
            }

            if (StateError(now) is { } stateError)
            {
                return SearchOutcome.Fail(stateError);
            }

            var outcome = await ShootAsync(target, scope, waiting: attempts > 0, phase, cancellationToken).ConfigureAwait(false);
            phase.Value = TextPhase.Waiting;
            if (outcome.Error is { } error)
            {
                return SearchOutcome.Fail(error);
            }

            var pastDeadline = _time.GetElapsedTime(searchStart).TotalMilliseconds >= query.TimeoutMs;
            if (outcome.Shot is { } shot)
            {
                attempts++;
                last = shot;
                lastMatches = TextMatcher.Find(shot.Reading.Lines, query.Text, query.Mode, query.Options);
                var pick = Pick(lastMatches, select, index, out lastAmbiguous);
                if (pick is not null)
                {
                    if (settle == 0 || (candidate is { } c && SameMatch(c.Shot, c.Match, shot, pick)))
                    {
                        return new SearchOutcome(null, (shot, pick, lastMatches, attempts));
                    }

                    // A first sighting is confirmed after settleMs, even past the deadline (that reading only); a confirmation that found
                    // another match at the deadline ends the search.
                    if (candidate is not null && pastDeadline)
                    {
                        break;
                    }

                    candidate = (shot, pick);
                    await Task.Delay(TimeSpan.FromMilliseconds(settle), _time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                candidate = null;
            }

            var remaining = query.TimeoutMs - _time.GetElapsedTime(searchStart).TotalMilliseconds;
            if (remaining <= 0)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(remaining, 1, query.PollMs)), _time, cancellationToken).ConfigureAwait(false);
        }

        return last is null
            ? SearchOutcome.Fail(ApiError.Create(ApiErrorCodes.TextNotFound, "the text was not found; nothing was tapped"))
            : SearchOutcome.Fail(lastAmbiguous
                ? CandidatesError(ApiErrorCodes.AmbiguousText, "the text was found more than once; narrow it with region, or choose with select", last, scope, lastMatches)
                : CandidatesError(ApiErrorCodes.TextNotFound, "the text was not found; nothing was tapped", last, scope, lastMatches));
    }

    private static TextMatch? Pick(IReadOnlyList<TextMatch> matches, TextSelect select, int index, out bool ambiguous)
    {
        ambiguous = select == TextSelect.Unique && matches.Count > 1;
        return select switch
        {
            TextSelect.Unique => matches.Count == 1 ? matches[0] : null,
            TextSelect.First => matches.Count > 0 ? matches[0] : null,
            _ => matches.Count > index ? matches[index] : null,
        };
    }

    // The confirming reading found the same match: its centre moved at most SameMatchDistance of the short side, on a picture of the same orientation.
    private static bool SameMatch(TextShot firstShot, TextMatch first, TextShot shot, TextMatch match)
    {
        if (firstShot.Frame.IsLandscape != shot.Frame.IsLandscape)
        {
            return false;
        }

        var (w, h) = ((double)shot.Frame.Width, (double)shot.Frame.Height);
        var dx = ((first.Box.CenterX / firstShot.Frame.Width) - (match.Box.CenterX / w)) * w;
        var dy = ((first.Box.CenterY / firstShot.Frame.Height) - (match.Box.CenterY / h)) * h;
        return Math.Sqrt((dx * dx) + (dy * dy)) <= SameMatchDistance * Math.Min(w, h);
    }

    // The state a tap needs, as Admit checks it (the user's input is checked on its own).
    private static ApiError? StateError(ApiHostSnapshot state) =>
        !state.Running ? ApiError.Create(ApiErrorCodes.NotRunning, "the mirroring session is not running")
        : state.ProbeSuspended ? ApiError.Create(ApiErrorCodes.Busy, "a measurement is running")
        : !state.Subscribed ? ApiError.Create(ApiErrorCodes.NotConnected, "the target device is not connected")
        : state.Picture is null ? ApiError.Create(ApiErrorCodes.NoPicture, "there is no picture of the device")
        : null;

    // The user used the mirror since the admission: a change of the last input (not a comparison of times, which a clock change could fool), or a
    // button or key held now.
    private static bool UserTouched(ApiHostSnapshot admitted, ApiHostSnapshot now) =>
        now.UserInputActive || now.LastUserInput != admitted.LastUserInput;

    private static ApiError UserInterruptedSearch() =>
        ApiError.Create(ApiErrorCodes.UserInterrupted, "the user used the mirror while the text was searched; nothing was tapped");

    // A pixel ref that is not square and whose orientation differs from the picture.
    private static bool RefTurned(CoordinateSpec space, bool pictureIsLandscape) =>
        space.Reference is { } r && r.Width != r.Height && (r.Width > r.Height) != pictureIsLandscape;

    private static ApiError CandidatesError(string code, string detail, TextShot shot, TextScope scope, IReadOnlyList<TextMatch> matches) =>
        ApiError.Create(code, detail) with
        {
            Extras = new ApiErrorExtras
            {
                Matches = [.. matches.Take(MaxTextCandidates).Select(m => MatchOf(m, shot.Frame, scope.Space))],
                MatchCount = matches.Count,
            },
        };

    // ---- One reading ---------------------------------------------------------------------------------------

    // read and find: one reading with its own deadline (a capture or an engine that does not answer must not hold the request).
    private async Task<ShotOutcome> ShootWithDeadlineAsync(OcrTarget target, TextScope scope, bool waiting, CancellationToken cancellationToken)
    {
        using var timer = new CancellationTokenSource(TextReadingTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timer.Token);
        var phase = new StrongBox<TextPhase>(TextPhase.Waiting);
        try
        {
            return await ShootAsync(target, scope, waiting, phase, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return ShotOutcome.Fail(phase.Value == TextPhase.Capture
                ? ApiError.Create(ApiErrorCodes.NoPicture, "the picture of the device could not be captured in time")
                : ApiError.Create(ApiErrorCodes.InternalError, "the text could not be recognized in time"));
        }
    }

    // The turn, the rate windows, the state, the capture, the ref, the recognition. While waiting, a refusal by the turn, a rate window or a
    // busy engine is a skipped reading (neither a shot nor an error).
    private async Task<ShotOutcome> ShootAsync(OcrTarget target, TextScope scope, bool waiting, StrongBox<TextPhase> phase, CancellationToken cancellationToken)
    {
        var turn = await _ocr.EnterTurnAsync(cancellationToken).ConfigureAwait(false);
        if (turn is null)
        {
            return waiting ? default : ShotOutcome.Fail(ApiError.Create(ApiErrorCodes.RateLimited, "too many text readings are running or waiting"));
        }

        using (turn)
        {
            if (!_screenshotLimit.TryAcquire(_textLimit))
            {
                return waiting ? default : ShotOutcome.Fail(ApiError.Create(ApiErrorCodes.RateLimited, $"at most {TextReadsPerSecond} text readings per second, and {ScreenshotRateLimiter.Limit} screenshots and readings together"));
            }

            var state = _host.GetSnapshot();
            if (!state.Running)
            {
                return ShotOutcome.Fail(ApiError.Create(ApiErrorCodes.NotRunning, "the mirroring session is not running"));
            }

            var noPicture = ShotOutcome.Fail(ApiError.Create(ApiErrorCodes.NoPicture, "there is no picture of the device"));
            if (state.Picture is null)
            {
                return noPicture;
            }

            phase.Value = TextPhase.Capture;
            var frame = await _host.CapturePictureAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return noPicture;
            }

            if (RefTurned(scope.Space, frame.IsLandscape))
            {
                return ShotOutcome.Fail(ApiError.Create(ApiErrorCodes.PictureChanged, "the screen rotated since the screenshot; take a new screenshot"));
            }

            phase.Value = TextPhase.Recognize;
            try
            {
                var reading = await _ocr.RecognizeAsync(turn, target, frame, scope.RegionOf(frame), cancellationToken).ConfigureAwait(false);
                return new ShotOutcome(new TextShot(frame, reading), null);
            }
            catch (OcrUnavailableException ex)
            {
                return ShotOutcome.Fail(Unavailable(ex.Details));
            }
            catch (OcrEngineException ex) when (ex.Failure == OcrFailure.Busy)
            {
                return waiting ? default : ShotOutcome.Fail(ApiError.Create(ApiErrorCodes.RateLimited, "the OCR engine is busy"));
            }
            catch (OcrEngineException ex) when (ex.Failure == OcrFailure.LanguageUnavailable)
            {
                return ShotOutcome.Fail(Unavailable(await _ocr.LanguageUnavailableAsync(target, cancellationToken).ConfigureAwait(false)));
            }
            catch (OcrEngineException ex) when (ex.Failure == OcrFailure.EngineUnavailable)
            {
                return ShotOutcome.Fail(Unavailable(new OcrUnavailable(OcrUnavailable.EngineReason, target.Info.Id, [], _ocr.AvailableEngineIds)));
            }
        }
    }

    private async ValueTask<(OcrTarget? Target, ApiError? Error)> ResolveTargetAsync(TextScope scope, CancellationToken cancellationToken)
    {
        try
        {
            return (await _ocr.ResolveAsync(scope.Engine, scope.Language, cancellationToken).ConfigureAwait(false), null);
        }
        catch (OcrUnavailableException ex)
        {
            return (null, Unavailable(ex.Details));
        }
        catch (OcrEngineException ex) when (ex.Failure == OcrFailure.Busy)
        {
            return (null, ApiError.Create(ApiErrorCodes.RateLimited, "the OCR engine is busy"));
        }
    }

    private static ApiError Unavailable(OcrUnavailable details)
    {
        var detail = details.Reason == OcrUnavailable.LanguageReason
            ? details.Available.Count == 0
                ? "this PC has no OCR language; add one in the Windows settings"
                : "this PC has no OCR for that language; see available"
            : details.EngineId is null ? "this application has no OCR engine" : "that OCR engine is not available; see engines";
        return ApiError.Create(ApiErrorCodes.OcrUnavailable, detail) with
        {
            Extras = new ApiErrorExtras { Reason = details.Reason, Engine = details.EngineId, Available = details.Available, Engines = details.Engines },
        };
    }

    private bool TryCountWait(TextQuery query)
    {
        if (query.TimeoutMs == 0)
        {
            return true;
        }

        if (Interlocked.Increment(ref _textWaits) > MaxTextWaits)
        {
            _ = Interlocked.Decrement(ref _textWaits);
            return false;
        }

        return true;
    }

    private void ReleaseWait(TextQuery query)
    {
        if (query.TimeoutMs > 0)
        {
            _ = Interlocked.Decrement(ref _textWaits);
        }
    }

    private static ApiError TooManyWaits() =>
        ApiError.Create(ApiErrorCodes.RateLimited, $"at most {MaxTextWaits} text searches with timeoutMs may wait at once");

    // ---- Parsing -------------------------------------------------------------------------------------------

    private static TextScope ParseScope(string? lang, string? engine, string? space, PixelRef? reference, TextRegion? region)
    {
        if (lang is not null && !OcrNames.IsWellFormedLanguageTag(lang))
        {
            throw Invalid("lang must be a language tag such as ja or en-US (letters, digits and hyphens, at most 35)");
        }

        if (engine is not null && !OcrNames.IsWellFormedEngineId(engine))
        {
            throw Invalid("engine must be an engine id (lower-case letters, digits and hyphens, at most 32)");
        }

        var spec = CoordinateSpec.Parse(space, reference).OrThrow();
        if (spec.Space == CoordinateSpace.Device)
        {
            throw Invalid("space must be screen or pixel");
        }

        if (region is not null)
        {
            var (w, h) = spec.Reference is { } r ? ((double)r.Width, (double)r.Height) : (1.0, 1.0);
            const double slack = 1e-9;
            if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
                || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
                || region.X + region.Width > w + slack || region.Y + region.Height > h + slack)
            {
                throw Invalid(spec.Space == CoordinateSpace.Pixel
                    ? "region must be a rectangle of positive size inside the screenshot (within ref)"
                    : "region must be a rectangle of positive size inside the screen (0..1)");
            }
        }

        return new TextScope(lang, engine, spec, region);
    }

    private static TextQuery ParseQuery(string? text, string? match, bool? loose, bool? caseSensitive, int? timeoutMs, int? pollMs)
    {
        Require(text, "text is required");
        if (text.Length is 0 or > MaxSearchTextLength)
        {
            throw Invalid($"text must have 1 to {MaxSearchTextLength} characters");
        }

        var mode = match?.ToUpperInvariant() switch
        {
            null or "EXACT" => TextMatchMode.Exact,
            "CONTAINS" => TextMatchMode.Contains,
            _ => throw Invalid("match must be exact or contains"),
        };
        var options = new TextCompareOptions(loose ?? false, caseSensitive ?? false);
        if (TextNormalizer.Normalize(text, options).Length == 0)
        {
            throw Invalid("text has nothing to search for (only white space)");
        }

        var timeout = timeoutMs ?? 0;
        if (timeout is < 0 or > MaxTextTimeoutMs)
        {
            throw Invalid($"timeoutMs must be 0 to {MaxTextTimeoutMs}");
        }

        var poll = pollMs ?? DefaultPollMs;
        if (poll is < MinPollMs or > MaxPollMs)
        {
            throw Invalid($"pollMs must be {MinPollMs} to {MaxPollMs}");
        }

        return new TextQuery(text, mode, options, timeout, poll);
    }

    // The offset as a fraction of the screen: screen -1..1, pixel within the ref size.
    private static PointD ParseOffset(ApiPoint? offset, CoordinateSpec space)
    {
        if (offset is null)
        {
            return default;
        }

        var (w, h) = space.Reference is { } r ? ((double)r.Width, (double)r.Height) : (1.0, 1.0);
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || Math.Abs(offset.X) > w || Math.Abs(offset.Y) > h)
        {
            throw Invalid(space.Space == CoordinateSpace.Pixel ? "offset must be within the ref size" : "offset x and y must be in -1..1");
        }

        return new PointD(offset.X / w, offset.Y / h);
    }

    // ---- Results -------------------------------------------------------------------------------------------

    private long ElapsedMs(long started) => (long)_time.GetElapsedTime(started).TotalMilliseconds;

    private static string SpaceName(CoordinateSpec space) => space.Space == CoordinateSpace.Pixel ? "pixel" : "screen";

    private static TextEngine EngineOf(OcrEngineInfo info) => new(info.Id, info.Name, info.Version);

    private static TextPicture PictureOf(ApiPictureFrame frame) =>
        new(frame.Width, frame.Height, frame.IsLandscape ? "landscape" : "portrait", (int)frame.Rotation);

    private static TextLineResult LineOf(TextLine line, ApiPictureFrame frame, CoordinateSpec space, bool words) =>
        new(
            line.Text,
            BoxOf(line.Box, frame, space),
            PointOf(line.Box.CenterX, line.Box.CenterY, frame, space),
            Round3(line.Confidence),
            words ? [.. line.Words.Select(w => new TextWordResult(w.Text, BoxOf(w.Box, frame, space), Round3(w.Confidence)))] : null);

    private static TextMatchResult MatchOf(TextMatch match, ApiPictureFrame frame, CoordinateSpec space) =>
        new(
            match.Text,
            match.LineText,
            BoxOf(match.Box, frame, space),
            PointOf(match.Box.CenterX, match.Box.CenterY, frame, space),
            match.WordLevel ? "word" : "line",
            Round3(match.Confidence));

    // Pixels of the picture in the request's space: screen 0..1 (4 decimals), or pixels of the ref size (2 decimals).
    private static TextBox BoxOf(OcrRect box, ApiPictureFrame frame, CoordinateSpec space)
    {
        var (sx, sy, digits) = Scale(frame, space);
        return new TextBox(Math.Round(box.X * sx, digits), Math.Round(box.Y * sy, digits), Math.Round(box.Width * sx, digits), Math.Round(box.Height * sy, digits));
    }

    private static TextPoint PointOf(double x, double y, ApiPictureFrame frame, CoordinateSpec space)
    {
        var (sx, sy, digits) = Scale(frame, space);
        return new TextPoint(Math.Round(x * sx, digits), Math.Round(y * sy, digits));
    }

    private static (double X, double Y, int Digits) Scale(ApiPictureFrame frame, CoordinateSpec space) =>
        space.Reference is { } r
            ? ((double)r.Width / frame.Width, (double)r.Height / frame.Height, 2)
            : (1.0 / frame.Width, 1.0 / frame.Height, 4);

    private static double? Round3(double? value) => value is { } v ? Math.Round(v, 3) : null;

    private static string TextLogLine(string kind, string? code, string? engine) =>
        code is null && engine is not null ? $"{kind} ok {engine}" : $"{kind} {code ?? "ok"}";

    private static OcrStatusInfo ToStatusInfo(OcrStatus status) =>
        new(
            status.Available,
            status.Engine is { } e ? new OcrEngineDetail(e.Id, e.Name, e.Version, KindName(e.Kind), new OcrEngineCapabilities(e.Capabilities.WordBoxes, e.Capabilities.Confidence)) : null,
            [.. status.Engines.Select(s => new OcrEngineSummary(s.Info.Id, s.Info.Name, KindName(s.Info.Kind), s.Available))],
            status.Languages,
            status.DefaultLanguage);

    private static string KindName(OcrEngineKind kind) => kind == OcrEngineKind.External ? "external" : "builtin";

    /// <summary>The language, engine, space and region of a text request (checked).</summary>
    private sealed record TextScope(string? Language, string? Engine, CoordinateSpec Space, TextRegion? Region)
    {
        // The region in pixels of the captured picture (inside it).
        public OcrRect? RegionOf(ApiPictureFrame frame)
        {
            if (Region is not { } r)
            {
                return null;
            }

            var (sx, sy) = Space.Reference is { } reference ? ((double)frame.Width / reference.Width, (double)frame.Height / reference.Height) : (frame.Width, frame.Height);
            var x = Math.Clamp(r.X * sx, 0, frame.Width);
            var y = Math.Clamp(r.Y * sy, 0, frame.Height);
            var width = Math.Clamp(r.Width * sx, 0, frame.Width - x);
            var height = Math.Clamp(r.Height * sy, 0, frame.Height - y);
            return new OcrRect(x, y, Math.Max(width, 1e-6), Math.Max(height, 1e-6));
        }
    }

    /// <summary>The searched text and how (checked).</summary>
    private sealed record TextQuery(string Text, TextMatchMode Mode, TextCompareOptions Options, int TimeoutMs, int PollMs);

    /// <summary>A picture and its text.</summary>
    private sealed record TextShot(ApiPictureFrame Frame, OcrReading Reading);

    /// <summary>A reading, an error, or neither (a reading skipped while waiting).</summary>
    private readonly record struct ShotOutcome(TextShot? Shot, ApiError? Error)
    {
        public static ShotOutcome Fail(ApiError error) => new(null, error);
    }

    /// <summary>The match to tap, or why there is none.</summary>
    private readonly record struct SearchOutcome(ApiError? Error, (TextShot Shot, TextMatch Match, IReadOnlyList<TextMatch> Matches, int Attempts) Value)
    {
        public static SearchOutcome Fail(ApiError error) => new(error, default);
    }
}
