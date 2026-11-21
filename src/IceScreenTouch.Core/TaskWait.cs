// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Core;

/// <summary>
/// Bounded waits that leave no timer behind. <c>Task.WhenAny(task, Task.Delay(timeout))</c> keeps the delay's timer
/// running until the timeout passes, even when the task finished long before (CA2027); this wait stops the timer as
/// soon as it ends.
/// </summary>
public static class TaskWait
{
    /// <summary>
    /// Waits until <paramref name="task"/> completes (in any state) or <paramref name="timeout"/> passes, whichever comes
    /// first, and returns whether the task completed first. A cancelled <paramref name="cancellationToken"/> ends the wait
    /// as well (the result is then <see langword="false"/> unless the task had completed). Like <c>Task.WhenAny</c>, it
    /// never throws the task's exception or an <see cref="OperationCanceledException"/>: awaiting the task afterwards
    /// observes its outcome.
    /// </summary>
    /// <param name="task">The task to wait for.</param>
    /// <param name="timeout">How long to wait at most (<see cref="Timeout.InfiniteTimeSpan"/> waits without a bound).</param>
    /// <param name="timeProvider">The clock of the timeout; <see cref="TimeProvider.System"/> when null.</param>
    /// <param name="cancellationToken">Ends the wait early.</param>
    public static async Task<bool> CompletesWithinAsync(
        Task task,
        TimeSpan timeout,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.IsCompleted)
        {
            return true;
        }

        using var stopTimer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delay = Task.Delay(timeout, timeProvider ?? TimeProvider.System, stopTimer.Token);
        var first = await Task.WhenAny(task, delay).ConfigureAwait(false);

        // Stops (disposes) the delay's timer when the task won; does nothing when the delay has already ended.
        await stopTimer.CancelAsync().ConfigureAwait(false);
        return first == task;
    }
}
