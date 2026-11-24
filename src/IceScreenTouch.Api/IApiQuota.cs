// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Api;

/// <summary>The two counters of the trial quota (docs/API-DESIGN.md, chapter 5).</summary>
public enum ApiQuotaCounter
{
    /// <summary>Operations that send input: tap, long press, swipe, path, pinch, keys, home, tap-text, and one per WebSocket connection.</summary>
    Operations,

    /// <summary>Pictures of the screen: screenshot, text/read, text/find.</summary>
    Captures,
}

/// <summary>
/// What is left of the trial quota: <c>trial</c> in <c>GET /v1/status</c>. The quota counts over the whole trial (it does not reset).
/// </summary>
/// <param name="OperationsLeft">Operations left (0 when used up).</param>
/// <param name="OperationsLimit">Operations in the whole trial.</param>
/// <param name="CapturesLeft">Captures left (0 when used up).</param>
/// <param name="CapturesLimit">Captures in the whole trial.</param>
/// <param name="StreamLimitSeconds">The longest a WebSocket connection may stay open, in seconds.</param>
public sealed record ApiTrialInfo(int OperationsLeft, int OperationsLimit, int CapturesLeft, int CapturesLimit, int StreamLimitSeconds)
{
    /// <summary>What is left of <paramref name="counter"/>.</summary>
    public int Left(ApiQuotaCounter counter) => counter == ApiQuotaCounter.Operations ? OperationsLeft : CapturesLeft;

    /// <summary>The limit of <paramref name="counter"/>.</summary>
    public int Limit(ApiQuotaCounter counter) => counter == ApiQuotaCounter.Operations ? OperationsLimit : CapturesLimit;
}

/// <summary>
/// The trial quota of the API. The application decides when it applies (the Store trial); a purchased app or a build of your own
/// has none (<see cref="Current"/> is null and <see cref="TryTake"/> always succeeds). Thread-safe.
/// </summary>
public interface IApiQuota
{
    /// <summary>What is left now, or null when the API is not limited.</summary>
    ApiTrialInfo? Current { get; }

    /// <summary>
    /// Counts one use of <paramref name="counter"/>. True when it was counted or nothing is limited; false when the counter is used
    /// up (nothing was counted), with its <paramref name="limit"/>.
    /// </summary>
    bool TryTake(ApiQuotaCounter counter, out int limit);
}
