// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text.Json;
using IceScreenTouch.Core;

namespace IceScreenTouch.Probe;

/// <summary>The outcome of one probe run.</summary>
public sealed record ProbeResult(TapAnalysis Taps, GestureAnalysis Swipe, GestureAnalysis Pinch, IReadOnlyList<Check> Checks)
{
    public bool Passed => Checks.All(c => c.Ok);
}

/// <summary>
/// Plays the probe gestures on the phone and prints what the Safari page received:
/// 9 taps (mapping, orientation, latency), a one-finger swipe and a two-finger pinch.
/// </summary>
public sealed class ProbeRunner(TouchPlayer player, ProbeServer server, TextWriter output)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>
    /// Runs the probe and prints the result; null when nothing reached the page. When cancelled, a finger
    /// that is down is lifted before the cancellation propagates.
    /// </summary>
    public async Task<ProbeResult?> RunAsync(string expectedRotation = "r0", string? savePath = null, CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(expectedRotation, savePath, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (player.IsDown)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await player.LiftAsync(timeout.Token).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<ProbeResult?> RunCoreAsync(string expectedRotation, string? savePath, CancellationToken ct)
    {
        server.Clear();
        var sent = new List<SentTap>();
        foreach (var (gx, gy) in ProbeAnalysis.TapGrid)
        {
            var t0 = ProbeClock.Now();
            await player.TapAsync(new AxisPoint((ushort)gx, (ushort)gy), TimeSpan.FromMilliseconds(120), ct).ConfigureAwait(false);
            sent.Add(new SentTap(gx, gy, t0));
            await Task.Delay(700, ct).ConfigureAwait(false);
        }

        await Task.Delay(500, ct).ConfigureAwait(false);

        var events = server.Snapshot();
        var downs = events.Count(e => e.Type == "pointerdown");
        output.WriteLine($"probe page received {events.Count} events ({downs} pointerdown)");
        if (events.Count == 0)
        {
            output.WriteLine("  -> nothing arrived. Is the probe page open in Safari and in the foreground?");
            return null;
        }

        var taps = ProbeAnalysis.AnalyzeTaps(sent, events);
        PrintTaps(taps);

        // Phase 2: single-finger swipe (expects pointermove events).
        var tSwipe = ProbeClock.Now();
        await player.SwipeAsync(new AxisPoint(5000, 7500), new AxisPoint(5000, 2500), TimeSpan.FromMilliseconds(600), cancellationToken: ct).ConfigureAwait(false);
        await Task.Delay(600, ct).ConfigureAwait(false);
        var swipe = ProbeAnalysis.AnalyzeGesture(server.Snapshot(), tSwipe);
        if (swipe.Events.Count == 0)
        {
            output.WriteLine("swipe: NO events reached the page");
        }
        else
        {
            output.WriteLine(string.Create(Inv, $"swipe: {swipe.Events.Count} events, {swipe.Moves} pointermove, client y range {swipe.Events.Min(e => e.Cy):F0}..{swipe.Events.Max(e => e.Cy):F0}"));
            output.WriteLine("   " + LiftText(swipe));
        }

        // Phase 3: two-finger pinch (expects two distinct pointer ids).
        var tPinch = ProbeClock.Now();
        await player.PinchAsync(new AxisPoint(5000, 5000), 800, 2800, TimeSpan.FromMilliseconds(600), cancellationToken: ct).ConfigureAwait(false);
        await Task.Delay(600, ct).ConfigureAwait(false);
        var pinch = ProbeAnalysis.AnalyzeGesture(server.Snapshot(), tPinch);
        if (pinch.Events.Count == 0)
        {
            output.WriteLine("pinch: NO events reached the page");
        }
        else
        {
            var spans = string.Join(", ", pinch.PointerIds.Select(id =>
            {
                var xs = pinch.Of(id).Select(e => e.Cx).ToList();
                return string.Create(Inv, $"id {id}: x {xs.Min():F0}..{xs.Max():F0}");
            }));
            output.WriteLine($"pinch: {pinch.Events.Count} events, distinct pointer ids = {pinch.PointerIds.Count}  ({spans})");
            output.WriteLine("  -> 2 distinct ids that move apart means multi-touch works");
            output.WriteLine("   " + LiftText(pinch));
        }

        var checks = ProbeAnalysis.Evaluate(taps, swipe, pinch, expectedRotation);
        output.WriteLine();
        output.WriteLine("Checks:");
        foreach (var c in checks)
        {
            output.WriteLine($"  [{(c.Ok ? "PASS" : "FAIL")}] {c.Name,-18} {c.Detail}");
        }

        var result = new ProbeResult(taps, swipe, pinch, checks);
        output.WriteLine(result.Passed ? "PROBE PASSED" : "PROBE FAILED");

        if (savePath is not null)
        {
            await File.WriteAllTextAsync(savePath, JsonSerializer.Serialize([.. server.Snapshot()], ProbeJson.Default.ListProbeEvent), ct).ConfigureAwait(false);
            output.WriteLine($"events saved to {savePath}");
        }

        return result;
    }

    private void PrintTaps(TapAnalysis taps)
    {
        output.WriteLine(" sent(x,y)      -> observed norm(x,y)  client(x,y)    type   latency");
        foreach (var t in taps.Taps)
        {
            var s = t.Sent;
            output.WriteLine(t.Hit is not { } h
                ? string.Create(Inv, $" ({s.X,5},{s.Y,5}) -> (no pointerdown in window)")
                : string.Create(Inv, $" ({s.X,5},{s.Y,5}) -> ({h.Nx,6},{h.Ny,6})   ({h.Cx,6:F1},{h.Cy,6:F1})  {h.PointerType,-6} {t.LatencyMs,5:F0} ms"));
        }

        output.WriteLine($"matched {taps.Matched}/{taps.Taps.Count} points; extra pointerdowns: {taps.ExtraDowns}");
        if (taps.FitX is { } fx && taps.FitY is { } fy)
        {
            output.WriteLine(string.Create(Inv, $"fit: observed_x = {fx.A:F3}*sent_x + {fx.B:F0}   observed_y = {fy.A:F3}*sent_y + {fy.B:F0}   (relative to the Safari viewport)"));
        }

        if (taps.Matched > 0)
        {
            output.WriteLine(string.Create(Inv,
                $"implied viewport origin if 0..10000 spans the {taps.ScreenWidth}x{taps.ScreenHeight} screen: left={taps.OriginLeft:F1}px (spread {taps.LeftSpread:F1}) top={taps.OriginTop:F1}px (spread {taps.TopSpread:F1})"));
            output.WriteLine("  -> a spread of a few px or less means the mapping is linear and 1:1 over the whole screen");
            var lat = taps.Taps.Where(t => t.LatencyMs is not null).Select(t => t.LatencyMs!.Value).Order().ToList();
            output.WriteLine(string.Create(Inv, $"latency (send start -> page received): min {lat[0]:F0} ms, median {lat[lat.Count / 2]:F0} ms, max {lat[^1]:F0} ms"));
        }

        if (taps.Orientation is { } o)
        {
            output.WriteLine("  " + o.Describe());
        }
    }

    private static string LiftText(GestureAnalysis g) =>
        g.LiftProblems.Count == 0 ? "lift OK (each finger lifted at its last position)" : "LIFT JUMP: " + string.Join("; ", g.LiftProblems);
}
