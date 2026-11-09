// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

public class PairedSubscriberGateTests
{
    private const string Phone = "BluetoothLE#BluetoothLE02:11:22:33:44:55-02:aa:bb:cc:dd:ee";
    private const string Stranger = "BluetoothLE#BluetoothLE02:11:22:33:44:55-02:99:99:99:99:99";

    private sealed class Harness : IDisposable
    {
        public Harness(Func<string, CancellationToken, Task<bool?>> lookup)
        {
            Gate = new PairedSubscriberGate(lookup, line => { lock (Log) { Log.Add(line); } }, Time);
        }

        public FakeTimeProvider Time { get; } = new();

        public List<string> Log { get; } = [];

        public PairedSubscriberGate Gate { get; }

        public int Count(string text)
        {
            lock (Log)
            {
                return Log.Count(l => l.Contains(text, StringComparison.Ordinal));
            }
        }

        // Lets the clock run (one second at a time, giving the checks time to run) until the condition holds.
        public async Task AdvanceUntilAsync(Func<bool> condition)
        {
            for (var i = 0; i < 3000 && !condition(); i++)
            {
                Time.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(1);
            }

            Assert.True(condition());
        }

        public static async Task WaitForAsync(Func<bool> condition)
        {
            for (var i = 0; i < 500 && !condition(); i++)
            {
                await Task.Delay(5);
            }

            Assert.True(condition());
        }

        public void Dispose() => Gate.Dispose();
    }

    private static Harness NewHarness(Func<string, Task<bool?>> lookup) => new((id, _) => lookup(id));

    [Fact]
    public async Task UnknownAndUncheckedDevicesAreNotPaired()
    {
        var release = new TaskCompletionSource<bool?>();
        using var h = NewHarness(_ => release.Task);

        Assert.False(h.Gate.IsPaired(Phone));
        h.Gate.Update([Phone]);
        Assert.False(h.Gate.IsPaired(Phone));
        Assert.Empty(h.Gate.Filter([Phone]));

        release.SetResult(true);
        await Harness.WaitForAsync(() => h.Gate.IsPaired(Phone));
    }

    [Fact]
    public async Task OnlyPairedSubscribersPassTheFilterAndAnIgnoredOneIsLoggedOnce()
    {
        using var h = NewHarness(id => Task.FromResult<bool?>(id == Phone));
        var added = 0;
        h.Gate.PairedDeviceAdded += (_, _) => Interlocked.Increment(ref added);

        h.Gate.Update([Phone, Stranger]);
        await Harness.WaitForAsync(() => h.Gate.IsPaired(Phone) && h.Count("ignoring it for now") == 1);
        for (var i = 0; i < 40; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(1);
        }

        Assert.Equal([Phone], h.Gate.Filter([Stranger, Phone]));
        Assert.False(h.Gate.IsPaired(Stranger));
        Assert.Equal(1, added);
        Assert.Equal(1, h.Count("ignoring it for now"));
    }

    [Fact]
    public async Task StillUnconfirmedDeviceIsMentionedAtMostOncePerMinuteAtTheSteadyInterval()
    {
        using var h = NewHarness(_ => Task.FromResult<bool?>(false));

        h.Gate.Update([Stranger]);
        await Harness.WaitForAsync(() => h.Count("ignoring it for now") == 1);
        for (var i = 0; i < 60; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(1);
        }

        // About 600 s later: a reminder per minute at most (never one per lookup), none of them with the raw id.
        var reminders = h.Count("still not confirmed paired");
        Assert.InRange(reminders, 5, 10);
        Assert.Equal(reminders, h.Count(LogPrivacy.LogId(Stranger) + " is still not confirmed"));
        Assert.Equal(0, h.Count("99:99"));
    }

    [Fact]
    public async Task IgnoredDeviceIsLoggedWithoutItsRawId()
    {
        using var h = NewHarness(_ => Task.FromResult<bool?>(false));

        h.Gate.Update([Stranger]);
        await Harness.WaitForAsync(() => h.Count("ignoring it for now") == 1);

        string line;
        lock (h.Log)
        {
            line = Assert.Single(h.Log);
        }

        Assert.Contains(LogPrivacy.LogId(Stranger), line);
        Assert.DoesNotContain("99:99", line);
    }

    [Fact]
    public async Task TransientFailureIsRetriedAndAPairedPhoneIsAccepted()
    {
        var calls = 0;
        using var h = NewHarness(_ => Task.FromResult<bool?>(Interlocked.Increment(ref calls) == 1 ? null : true));

        h.Gate.Update([Phone]);
        await h.AdvanceUntilAsync(() => h.Gate.IsPaired(Phone));

        Assert.Equal(2, calls);
        Assert.Equal(1, h.Count("lookup failed"));
    }

    [Fact]
    public async Task PhoneConfirmedLongAfterTheFirstLookupsIsAcceptedAndTheDelayIsLogged()
    {
        var calls = 0;
        using var h = NewHarness(_ => Task.FromResult<bool?>(Interlocked.Increment(ref calls) > 8));

        h.Gate.Update([Phone]);
        await h.AdvanceUntilAsync(() => h.Gate.IsPaired(Phone));

        Assert.Equal(9, calls);
        Assert.Equal(1, h.Count("ignoring it for now"));
        Assert.Equal(1, h.Count("after being ignored"));
        Assert.Equal(1, h.Count("s after it subscribed"));
    }

    [Fact]
    public async Task ThrowingLookupCountsAsNotKnownAndIsTriedAgain()
    {
        var calls = 0;
        using var h = NewHarness(_ =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult<bool?>(true);
        });

        h.Gate.Update([Phone]);
        await h.AdvanceUntilAsync(() => h.Gate.IsPaired(Phone));

        Assert.Equal(3, calls);
        Assert.Equal(1, h.Count("boom"));
        Assert.Equal(1, h.Count(LogPrivacy.LogId(Phone) + " subscribed"));
    }

    [Fact]
    public async Task ThrowingEventHandlerAndThrowingLoggerDoNotEndTheCheck()
    {
        var logCalls = 0;
        using var gate = new PairedSubscriberGate((_, _) => Task.FromResult<bool?>(true), _ =>
        {
            Interlocked.Increment(ref logCalls);
            throw new InvalidOperationException("log failed");
        });
        gate.PairedDeviceAdded += (_, _) => throw new InvalidOperationException("handler failed");

        gate.Update([Phone]);
        await Harness.WaitForAsync(() => gate.IsPaired(Phone) && logCalls >= 2);
    }

    [Fact]
    public async Task ADeviceThatSubscribesAgainIsCheckedAgain()
    {
        var calls = 0;
        var answer = false;
        using var h = NewHarness(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<bool?>(answer);
        });

        h.Gate.Update([Phone]);
        await Harness.WaitForAsync(() => calls >= 1);
        Assert.False(h.Gate.IsPaired(Phone));

        h.Gate.Update([]);
        answer = true;
        h.Gate.Update([Phone]);
        await Harness.WaitForAsync(() => h.Gate.IsPaired(Phone));
    }

    [Fact]
    public async Task UpdateGivesAnUnconfirmedDeviceAnExtraLookupButNotTooSoon()
    {
        var calls = 0;
        using var h = NewHarness(_ =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult<bool?>(false);
        });

        h.Gate.Update([Phone]);
        await Harness.WaitForAsync(() => calls == 1);
        await Task.Delay(30);

        h.Gate.Update([Phone]); // right after the lookup: no extra one
        await Task.Delay(30);
        Assert.Equal(1, calls);

        h.Time.Advance(TimeSpan.FromSeconds(1.2)); // before the first pause (1.5 s) is over
        await Task.Delay(30);
        h.Gate.Update([Phone]);
        await Harness.WaitForAsync(() => calls == 2);
    }

    [Fact]
    public async Task ResultForADeviceThatLeftIsDiscardedAndItsChecksStop()
    {
        var release = new TaskCompletionSource<bool?>();
        var calls = 0;
        using var h = NewHarness(_ =>
        {
            Interlocked.Increment(ref calls);
            return release.Task;
        });
        var added = 0;
        h.Gate.PairedDeviceAdded += (_, _) => Interlocked.Increment(ref added);

        h.Gate.Update([Phone]);
        await Harness.WaitForAsync(() => calls == 1);
        h.Gate.Update([]);
        release.SetResult(false);
        for (var i = 0; i < 30; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(1);
        }

        Assert.False(h.Gate.IsPaired(Phone));
        Assert.Equal(0, added);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DisposeWhileALookupRunsCancelsItWithoutAnEvent()
    {
        var started = new TaskCompletionSource();
        var cancelled = new TaskCompletionSource();
        using var h = new Harness(async (_, ct) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }

            return true;
        });
        var added = 0;
        h.Gate.PairedDeviceAdded += (_, _) => Interlocked.Increment(ref added);

        h.Gate.Update([Phone]);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Gate.Dispose();
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Gate.Update([Phone]); // after Dispose: ignored

        Assert.False(h.Gate.IsPaired(Phone));
        Assert.Equal(0, added);
    }

    [Fact]
    public async Task ConcurrentAndStaleUpdatesEndWithTheLastOneApplied()
    {
        using var h = NewHarness(id => Task.FromResult<bool?>(id == Phone));

        Parallel.For(0, 200, i => h.Gate.Update(i % 2 == 0 ? [Phone, Stranger] : [Stranger]));
        h.Gate.Update([Phone]);
        await Harness.WaitForAsync(() => h.Gate.IsPaired(Phone));

        // An older snapshot applied last would remove a live subscriber: the gate follows the last call.
        h.Gate.Update([Stranger]);
        Assert.False(h.Gate.IsPaired(Phone));
        h.Gate.Update([Phone]);
        await Harness.WaitForAsync(() => h.Gate.IsPaired(Phone));
    }

    [Fact]
    public async Task StillSubscribedDeviceIsNotCheckedTwice()
    {
        var calls = 0;
        using var h = NewHarness(_ => Task.FromResult<bool?>(Interlocked.Increment(ref calls) > 0));

        h.Gate.Update([Phone]);
        await Harness.WaitForAsync(() => h.Gate.IsPaired(Phone));
        h.Gate.Update([Phone]);
        h.Gate.Update([Phone, Stranger]);
        await Harness.WaitForAsync(() => h.Gate.IsPaired(Stranger));
        await Task.Delay(30);

        Assert.Equal(2, calls);
    }
}
