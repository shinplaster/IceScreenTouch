using System.Globalization;
using IceScreenTouch.Core;

namespace IceScreenTouch.Probe;

/// <summary>A wheel input to replay: notches at given times (ms from the start; negative notches = wheel down).</summary>
public sealed record ScrollScenario(string Name, IReadOnlyList<(double AtMs, double Notches)> Wheel)
{
    /// <summary>Total notches.</summary>
    public double Notches => Wheel.Sum(w => w.Notches);

    /// <summary>One notch, and a quick twist of several notches.</summary>
    public static IReadOnlyList<ScrollScenario> All { get; } =
    [
        new("1 notch", [(0, -1)]),
        new("spin 5 x 40ms", [.. Enumerable.Range(0, 5).Select(i => (i * 40.0, -1.0))]),
    ];

    /// <summary>The same input the other way round.</summary>
    public ScrollScenario Reversed() => this with { Wheel = [.. Wheel.Select(w => (w.AtMs, -w.Notches))] };
}

/// <summary>What one wheel scroll did on the page.</summary>
/// <param name="Travel">How far the wheel asked the finger to move in total, in CSS px (= points).</param>
/// <param name="Scrolled">How far the page scrolled in the end (signed; positive = down).</param>
/// <param name="TouchTravel">Finger travel as the page saw it (first touchstart -> last touchend), if both arrived.</param>
/// <param name="Tail">Time from the last touchend to the last scroll event: how long the content kept moving after the lift.</param>
public sealed record ScrollTrial(double Travel, double Scrolled, double? TouchTravel, TimeSpan? Tail)
{
    /// <summary>Scrolled / travel: about 0.7 = the page moved with the finger and stopped (iOS eats some slop); more = momentum.</summary>
    public double Ratio => Travel > 0 ? Math.Abs(Scrolled) / Travel : double.NaN;

    /// <summary>Works out a trial from the events the page sent after the scroll started.</summary>
    public static ScrollTrial Analyze(IReadOnlyList<ProbeEvent> events, double scrollBefore, double travel)
    {
        ArgumentNullException.ThrowIfNull(events);
        var scrolls = events.Where(e => e.Type == "scroll" && e.Sy is not null).ToList();
        var after = scrolls.Count > 0 ? scrolls[^1].Sy!.Value : scrollBefore;
        var start = events.FirstOrDefault(e => e.Type == "touchstart");
        var end = events.LastOrDefault(e => e.Type is "touchend" or "touchcancel");
        double? touchTravel = start is not null && end is not null ? Math.Abs(end.Cy - start.Cy) : null;
        TimeSpan? tail = end is not null && scrolls.Count > 0 ? TimeSpan.FromSeconds(Math.Max(0, scrolls[^1].T - end.T)) : null;
        return new ScrollTrial(travel, after - scrollBefore, touchTravel, tail);
    }
}

/// <summary>The trials of one momentum setting and one <see cref="ScrollScenario"/>.</summary>
public sealed record ScrollProbeRow(double Momentum, string Scenario, IReadOnlyList<ScrollTrial> Trials)
{
    /// <summary>Mean <see cref="ScrollTrial.Ratio"/> (NaN when no trial has one).</summary>
    public double MeanRatio => Trials.Where(r => !double.IsNaN(r.Ratio)).Select(r => r.Ratio).DefaultIfEmpty(double.NaN).Average();

    /// <summary>Longest time the content kept moving after the lift, if any trial measured it.</summary>
    public TimeSpan? MaxTail => Trials.Any(r => r.Tail is not null) ? Trials.Where(r => r.Tail is not null).Max(r => r.Tail!.Value) : null;

    /// <summary>Some touches did not reach the page.</summary>
    public bool MissingTouches => Trials.Any(r => r.TouchTravel is null);
}

/// <summary>
/// Replays wheel input through the engine's wheel scroll on the scroll page (/scroll), for each momentum
/// setting and <see cref="ScrollScenario"/>, and prints how far the page scrolled compared with the wheel and
/// how long it kept moving after the lift.
/// </summary>
public sealed class ScrollProbe(ITouchSink sink, ProbeServer server, TextWriter output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // A square "picture" with the cursor in the middle; only the unit coordinates matter.
    private static readonly RectD Picture = new(0, 0, 1000, 1000);
    private static readonly PointD Cursor = new(500, 500);

    /// <summary>The momentum settings compared by default.</summary>
    public static IReadOnlyList<double> DefaultMomentums { get; } = [0, 0.25, 0.5, 1.0];

    /// <summary>Runs every momentum and scenario <paramref name="trials"/> times; prints a table and returns the rows.</summary>
    public async Task<IReadOnlyList<ScrollProbeRow>> RunAsync(int trials = 3, double step = 0.10, IReadOnlyList<double>? momentums = null, CancellationToken ct = default)
    {
        var rows = new List<ScrollProbeRow>();
        var screenHeight = server.HelloFor(ProbePage.Scroll) is { Sh: > 0 } hello ? hello.Sh : 852;
        output.WriteLine(string.Create(Inv, $"scroll probe: {trials} scrolls each, {step:P0} of the screen per notch ({step * screenHeight:F0} pt)"));
        output.WriteLine("momentum  input           ratio (each)              tail ms (each)        mean ratio");
        foreach (var momentum in momentums is { Count: > 0 } ? momentums : DefaultMomentums)
        {
            foreach (var scenario in ScrollScenario.All)
            {
                var engine = new TouchGestureEngine(sink) { Wheel = WheelOptions.Default with { StepPerNotch = step, Momentum = momentum } };
                var results = new List<ScrollTrial>();
                for (var i = 0; i < trials; i++)
                {
                    await WaitStillAsync(ct).ConfigureAwait(false);
                    var before = server.LastScrollY ?? 0;
                    server.Clear();

                    // Alternate wheel down / up so that the page stays mid-way.
                    var input = i % 2 == 0 ? scenario : scenario.Reversed();
                    await PlayAsync(engine, input, ct).ConfigureAwait(false);
                    await WaitStillAsync(ct).ConfigureAwait(false);
                    results.Add(ScrollTrial.Analyze(server.Snapshot(), before, Math.Abs(input.Notches) * step * screenHeight));
                }

                var row = new ScrollProbeRow(momentum, scenario.Name, results);
                rows.Add(row);
                var ratios = string.Join(" ", results.Select(r => r.Ratio.ToString("F2", Inv)));
                var tails = string.Join(" ", results.Select(r => r.Tail is { } t ? t.TotalMilliseconds.ToString("F0", Inv) : "-"));
                output.WriteLine(string.Create(Inv, $"{momentum,6:P0}    {scenario.Name,-15} {ratios,-25} {tails,-21} {row.MeanRatio:F2}"));
                if (row.MissingTouches)
                {
                    output.WriteLine("   (some touches did not reach the page: is /scroll open in Safari, in front, with Zoom on?)");
                }
            }
        }

        output.WriteLine("ratio ~0.7 = the page moved with the wheel and stopped (iOS eats ~28 pt of slop); larger = momentum.");
        return rows;
    }

    /// <summary>
    /// Feeds the wheel input at its times and ticks the engine every 4 ms until the finger is up. When
    /// cancelled, the finger is lifted before the cancellation propagates.
    /// </summary>
    internal static async Task PlayAsync(TouchGestureEngine engine, ScrollScenario input, CancellationToken ct, TimeProvider? time = null)
    {
        try
        {
            await PlayCoreAsync(engine, input, time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (engine.IsDown || engine.IsScrolling)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await engine.AbortAsync(timeout.Token).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task PlayCoreAsync(TouchGestureEngine engine, ScrollScenario input, TimeProvider? time, CancellationToken ct)
    {
        time ??= TimeProvider.System;
        var start = time.GetTimestamp();
        var next = 0;
        while (next < input.Wheel.Count || engine.IsScrolling)
        {
            var now = time.GetElapsedTime(start).TotalMilliseconds;
            while (next < input.Wheel.Count && input.Wheel[next].AtMs <= now)
            {
                await engine.WheelAsync(Cursor, Picture, 0, input.Wheel[next].Notches, ct).ConfigureAwait(false);
                next++;
            }

            await engine.ScrollTickAsync(ct).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(4), time, ct).ConfigureAwait(false);
        }
    }

    // Waits until the page has not scrolled for 600 ms (at most 6 s).
    private async Task WaitStillAsync(CancellationToken ct)
    {
        var deadline = ProbeClock.Now() + 6;
        await Task.Delay(300, ct).ConfigureAwait(false);
        while (ProbeClock.Now() < deadline && ProbeClock.Now() - server.LastScrollAt < 0.6)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }
}
