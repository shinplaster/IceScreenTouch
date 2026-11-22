// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Net;
using IceScreenTouch.Core;
using IceScreenTouch.Probe;

namespace IceScreenTouch.Probe.Tests;

// The latency probe against a simulated phone and mirror window (about a second per test). The measuring tests run on a clock
// that moves only when the simulated screen is looked at, so what they measure does not depend on how fast the runner is.
public sealed class LatencyProbeTests
{
    private const double TouchMs = 30;
    private const double PaintMs = 8;
    private const double PictureMs = 50;
    private const double LookMs = 2;

    [Fact]
    public async Task MeasuresTheWholeDelayAndSplitsIt()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        var phone = new SimulatedPhone(server, new LookClock());

        var result = await Probe(server, phone).RunAsync(3);

        Assert.Equal(6, result.Trials.Count);
        Assert.Equal(6, result.Seen);
        foreach (var trials in new[] { result.Downs, result.Ups })
        {
            // A change is seen by the first look that ends after it and timed at the middle of that look: within a look of the truth.
            Assert.InRange(LatencyResult.Median(trials.Select(t => t.TotalMs))!.Value, TouchMs + PaintMs + PictureMs - LookMs, TouchMs + PaintMs + PictureMs + LookMs);
            Assert.InRange(LatencyResult.Median(trials.Select(t => t.TouchMs))!.Value, TouchMs - 0.01, TouchMs + 0.01);
            Assert.InRange(LatencyResult.Median(trials.Select(t => t.PictureMs))!.Value, PictureMs - LookMs, PictureMs + LookMs);
        }

        Assert.Equal(LookMs, result.SampleMs, 0.01);
        Assert.False(phone.FingerDown);
    }

    [Fact]
    public async Task WithoutThePagesReportsOnlyTheTotalIsKnown()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        var phone = new SimulatedPhone(server, new LookClock()) { ReportsEvents = false };

        var result = await Probe(server, phone).RunAsync(2);

        Assert.Equal(4, result.Seen);
        Assert.All(result.Trials, t => Assert.Null(t.TouchMs));
        Assert.All(result.Trials, t => Assert.Null(t.PictureMs));
        Assert.NotNull(LatencyResult.Median(result.Downs.Select(t => t.TotalMs)));
    }

    [Fact]
    public async Task APictureThatNeverChangesIsNotSeenAndTheFingerIsLifted()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        var phone = new SimulatedPhone(server) { Frozen = true };

        var result = await new LatencyProbe(phone, server, phone, TextWriter.Null) { ChangeTimeout = TimeSpan.FromMilliseconds(150), Settle = TimeSpan.FromMilliseconds(30), Jitter = TimeSpan.Zero }.RunAsync(1);

        Assert.Equal(2, result.Trials.Count);
        Assert.Equal(0, result.Seen);
        Assert.False(phone.FingerDown);
    }

    [Fact]
    public async Task StopsWhenTheSpotIsNotBlack()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        var phone = new SimulatedPhone(server) { Frozen = true, White = true };
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        var result = await new LatencyProbe(phone, server, phone, output).RunAsync(3);

        Assert.Empty(result.Trials);
        Assert.Contains("does not turn black", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, phone.Reports);
    }

    [Fact]
    public void TheMedianSkipsMissingValues()
    {
        Assert.Null(LatencyResult.Median([]));
        Assert.Null(LatencyResult.Median([null, null]));
        Assert.Equal(20, LatencyResult.Median([30, null, 10, 20]));
        Assert.Equal(15, LatencyResult.Median([10, 20, null]));
    }

    private static LatencyProbe Probe(ProbeServer server, SimulatedPhone phone) =>
        new(phone, server, phone, TextWriter.Null, phone.Now)
        {
            Settle = TimeSpan.FromMilliseconds(40),
            Jitter = TimeSpan.FromMilliseconds(20),
            Hold = TimeSpan.FromMilliseconds(30),
        };

    /// <summary>A clock (seconds) that stands still until <see cref="Advance"/> moves it.</summary>
    private sealed class LookClock
    {
        private long _ticks;

        public double Now() => (double)Interlocked.Read(ref _ticks) / TimeSpan.TicksPerSecond;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>
    /// The phone and the mirror window: a touch reaches the page after <see cref="TouchMs"/>, the page paints
    /// <see cref="PaintMs"/> later, and the PC shows it <see cref="PictureMs"/> after that. A look at the screen takes
    /// <see cref="LookMs"/>: on <paramref name="clock"/> when there is one (it moves only then), else in real time.
    /// </summary>
    private sealed class SimulatedPhone(ProbeServer server, LookClock? clock = null) : ITouchSink, IPictureSampler
    {
        private readonly Lock _lock = new();
        private readonly List<(double At, bool White)> _changes = [];

        public bool ReportsEvents { get; init; } = true;

        public bool Frozen { get; init; }

        public bool White { get; init; }

        public bool FingerDown { get; private set; }

        public int Reports { get; private set; }

        public bool IsSubscribed => true;

        /// <summary>The time in seconds: the look clock's, or <see cref="ProbeClock"/>'s.</summary>
        public double Now() => clock?.Now() ?? ProbeClock.Now();

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken ct)
        {
            Reports++;
            if (contacts.Length == 0)
            {
                return ValueTask.CompletedTask;
            }

            var down = contacts.Span[0].Tip;
            FingerDown = down;
            var now = Now();
            var eventAt = now + (TouchMs / 1000);
            var paintAt = eventAt + (PaintMs / 1000);
            lock (_lock)
            {
                _changes.Add((paintAt + (PictureMs / 1000), down));
            }

            if (ReportsEvents)
            {
                server.Add(string.Create(CultureInfo.InvariantCulture, $$"""{"type":"lat","what":"{{(down ? "down" : "up")}}","ts":{{eventAt:R}},"tp":{{paintAt:R}}}"""));
            }

            return ValueTask.CompletedTask;
        }

        public double? MeanLuma()
        {
            // A look at the screen takes a while.
            if (clock is null)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(LookMs));
            }
            else
            {
                clock.Advance(TimeSpan.FromMilliseconds(LookMs));
            }

            if (Frozen)
            {
                return White ? 250 : 5;
            }

            var now = Now();
            var white = false;
            lock (_lock)
            {
                foreach (var (at, w) in _changes)
                {
                    if (at <= now)
                    {
                        white = w;
                    }
                }
            }

            return white ? 250 : 5;
        }
    }
}
