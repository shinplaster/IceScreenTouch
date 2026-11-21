// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

/// <summary>A bounded wait reports which side ended first and leaves no timer running (CA2027, issue #25).</summary>
public class TaskWaitTests
{
    [Fact]
    public async Task ReturnsTrueWithoutATimerWhenTheTaskHasAlreadyCompleted()
    {
        var time = new TimerCountingTimeProvider();

        Assert.True(await TaskWait.CompletesWithinAsync(Task.CompletedTask, TimeSpan.FromSeconds(1), time));
        Assert.Equal(0, time.Created);
    }

    [Fact]
    public async Task StopsTheTimerWhenTheTaskCompletesFirst()
    {
        var time = new TimerCountingTimeProvider();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = TaskWait.CompletesWithinAsync(done.Task, TimeSpan.FromSeconds(1), time);
        Assert.Equal(1, time.Created);
        Assert.Equal(1, time.Running);
        done.SetResult();

        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, time.Running);
    }

    [Fact]
    public async Task ReturnsFalseWhenTheTimeoutPassesFirst()
    {
        var time = new TimerCountingTimeProvider();
        var never = new TaskCompletionSource();

        var wait = TaskWait.CompletesWithinAsync(never.Task, TimeSpan.FromSeconds(1), time);
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(wait.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, time.Running);
    }

    [Fact]
    public async Task DoesNotThrowTheTasksException()
    {
        var failing = Task.FromException(new InvalidOperationException("boom"));
        var failingLater = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var wait = TaskWait.CompletesWithinAsync(failingLater.Task, TimeSpan.FromSeconds(30));
        failingLater.SetException(new InvalidOperationException("boom"));

        Assert.True(await TaskWait.CompletesWithinAsync(failing, TimeSpan.FromSeconds(30)));
        Assert.True(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(await TaskWait.CompletesWithinAsync(Task.FromCanceled(new CancellationToken(canceled: true)), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task ACancelledTokenEndsTheWaitWithoutThrowing()
    {
        var time = new TimerCountingTimeProvider();
        var never = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();

        var wait = TaskWait.CompletesWithinAsync(never.Task, TimeSpan.FromSeconds(1), time, cts.Token);
        await cts.CancelAsync();

        Assert.False(await wait.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, time.Running);
        Assert.False(await TaskWait.CompletesWithinAsync(never.Task, TimeSpan.FromSeconds(1), time, cts.Token));
    }

    /// <summary>A fake clock that counts the timers it created and the ones not disposed yet.</summary>
    private sealed class TimerCountingTimeProvider : FakeTimeProvider
    {
        private int _created;
        private int _running;

        public int Created => Volatile.Read(ref _created);

        public int Running => Volatile.Read(ref _running);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _created);
            Interlocked.Increment(ref _running);
            return new CountedTimer(base.CreateTimer(callback, state, dueTime, period), this);
        }

        private sealed class CountedTimer(ITimer inner, TimerCountingTimeProvider owner) : ITimer
        {
            private int _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    Interlocked.Decrement(ref owner._running);
                }

                inner.Dispose();
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
