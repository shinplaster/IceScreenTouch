// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Probe;

/// <summary>A tap the PC sent: digitizer position and the send time (<see cref="ProbeClock"/>).</summary>
public sealed record SentTap(int X, int Y, double T);

/// <summary>A sent tap and the pointerdown the page received for it (null when nothing arrived).</summary>
public sealed record TapMatch(SentTap Sent, ProbeEvent? Hit)
{
    public double? LatencyMs => Hit is null ? null : (Hit.T - Sent.T) * 1000;
}

/// <summary>Least-squares line y = A*x + B.</summary>
public readonly record struct LineFit(double A, double B);

/// <summary>Which way the digitizer axes land on the page.</summary>
/// <param name="Swapped">Digitizer X drives page Y (landscape).</param>
/// <param name="SignP">Slope sign of page X.</param>
/// <param name="SignQ">Slope sign of page Y.</param>
/// <param name="Rotation">r0 / r90 / r180 / r270 (names as in poc/poc3-integrated), or null if mirrored.</param>
public sealed record OrientationResult(bool Swapped, char SignP, char SignQ, string? Rotation)
{
    public string Describe()
    {
        var (srcP, srcQ) = Swapped ? ("sent_y", "sent_x") : ("sent_x", "sent_y");
        var text = $"orientation: page_x ~ {SignP}{srcP}, page_y ~ {SignQ}{srcQ}  ->  axes {(Swapped ? "SWAPPED" : "not swapped")}";
        return text + (Rotation is null ? ", mirrored (unexpected)" : $", matches rotation {Rotation}");
    }
}

/// <summary>Result of the 9-point tap phase.</summary>
public sealed record TapAnalysis(
    IReadOnlyList<TapMatch> Taps,
    int Matched,
    int ExtraDowns,
    LineFit? FitX,
    LineFit? FitY,
    double OriginLeft,
    double LeftSpread,
    double OriginTop,
    double TopSpread,
    double ScreenWidth,
    double ScreenHeight,
    OrientationResult? Orientation);

/// <summary>Result of a one- or two-finger gesture phase.</summary>
/// <param name="Events">Pointer events (down/move/up) of the phase.</param>
/// <param name="Moves">Number of pointermove events.</param>
/// <param name="PointerIds">Distinct pointer ids.</param>
/// <param name="LiftProblems">Fingers that were not lifted where they last were (empty = OK).</param>
public sealed record GestureAnalysis(IReadOnlyList<ProbeEvent> Events, int Moves, IReadOnlyList<long> PointerIds, IReadOnlyList<string> LiftProblems)
{
    public bool LiftOk => Events.Count > 0 && LiftProblems.Count == 0;

    public IEnumerable<ProbeEvent> Of(long id) => Events.Where(e => e.Id == id);
}

/// <summary>One pass/fail item of the M2 verdict.</summary>
public sealed record Check(string Name, bool Ok, string Detail);

/// <summary>
/// Pure analysis of what the probe page received. Port of run_probe() in poc/poc2-ble-hid/ble_hid_poc.py,
/// plus explicit pass/fail checks for the M2 completion criteria.
/// </summary>
public static class ProbeAnalysis
{
    /// <summary>Max spread (px) of the implied viewport origin that still counts as a 1:1 mapping.</summary>
    public const double MaxOriginSpreadPx = 3.0;

    /// <summary>A pointerup farther than this (px) from the finger's last position is a lift jump.</summary>
    public const double MaxLiftDistancePx = 20.0;

    /// <summary>Tap matching window for the last tap (s), like the PoC.</summary>
    public const double LastTapWindow = 1.2;

    /// <summary>The 3 x 3 tap grid. Both axes stay away from the edges (Safari's bars, landscape).</summary>
    public static IReadOnlyList<(int X, int Y)> TapGrid { get; } =
        [.. from gy in new[] { 3000, 5000, 7000 } from gx in new[] { 3000, 5000, 7000 } select (gx, gy)];

    /// <summary>Least squares y = a*x + b; null when x has no spread.</summary>
    public static LineFit? Fit(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        var n = xs.Count;
        if (n < 2)
        {
            return null;
        }

        double mx = xs.Average(), my = ys.Average();
        double sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++)
        {
            sxx += (xs[i] - mx) * (xs[i] - mx);
            sxy += (xs[i] - mx) * (ys[i] - my);
        }

        if (sxx == 0)
        {
            return null;
        }

        var a = sxy / sxx;
        return new LineFit(a, my - (a * mx));
    }

    /// <summary>
    /// Tells how the digitizer axes map onto the page by comparing the straight and the swapped pairing
    /// (least squares) and reading the slope signs:
    /// r0 page_x ~ +sent_x, page_y ~ +sent_y; r90 page_x ~ +sent_y, page_y ~ -sent_x;
    /// r180 page_x ~ -sent_x, page_y ~ -sent_y; r270 page_x ~ -sent_y, page_y ~ +sent_x.
    /// </summary>
    public static OrientationResult? Orientation(IReadOnlyList<TapMatch> matched)
    {
        var hits = matched.Where(m => m.Hit is not null).ToList();
        double[] sx = [.. hits.Select(m => (double)m.Sent.X)];
        double[] sy = [.. hits.Select(m => (double)m.Sent.Y)];
        double[] px = [.. hits.Select(m => m.Hit!.Cx)];
        double[] py = [.. hits.Select(m => m.Hit!.Cy)];

        var straight = (P: Sse(sx, px), Q: Sse(sy, py));
        var swapped = (P: Sse(sy, px), Q: Sse(sx, py));
        if ((straight.P is null || straight.Q is null) && (swapped.P is null || swapped.Q is null))
        {
            return null;
        }

        static double Cost((double Sse, double A)? p, (double Sse, double A)? q) =>
            p is null || q is null ? double.PositiveInfinity : p.Value.Sse + q.Value.Sse;

        var isSwapped = Cost(swapped.P, swapped.Q) < Cost(straight.P, straight.Q);
        var (ap, aq) = isSwapped ? (swapped.P!.Value.A, swapped.Q!.Value.A) : (straight.P!.Value.A, straight.Q!.Value.A);
        char sp = ap > 0 ? '+' : '-', sq = aq > 0 ? '+' : '-';
        var name = (isSwapped, sp, sq) switch
        {
            (false, '+', '+') => "r0",
            (true, '+', '-') => "r90",
            (false, '-', '-') => "r180",
            (true, '-', '+') => "r270",
            _ => null,
        };
        return new OrientationResult(isSwapped, sp, sq, name);

        static (double Sse, double A)? Sse(double[] xs, double[] ys)
        {
            if (Fit(xs, ys) is not { } f)
            {
                return null;
            }

            var sse = 0.0;
            for (var i = 0; i < xs.Length; i++)
            {
                var d = (f.A * xs[i]) + f.B - ys[i];
                sse += d * d;
            }

            return (sse, f.A);
        }
    }

    /// <summary>
    /// Matches each sent tap with the first pointerdown received between its send time and the next
    /// tap's send time, then derives the mapping (fit, implied viewport origin and its spread, orientation).
    /// </summary>
    public static TapAnalysis AnalyzeTaps(IReadOnlyList<SentTap> sent, IReadOnlyList<ProbeEvent> events)
    {
        var downs = events.Where(e => e.Type == "pointerdown").ToList();
        var taps = new List<TapMatch>(sent.Count);
        for (var i = 0; i < sent.Count; i++)
        {
            var t0 = sent[i].T;
            var t1 = i + 1 < sent.Count ? sent[i + 1].T : t0 + LastTapWindow;
            taps.Add(new TapMatch(sent[i], downs.FirstOrDefault(e => e.T >= t0 && e.T < t1)));
        }

        var matched = taps.Where(t => t.Hit is not null).ToList();
        var fitX = Fit([.. matched.Select(m => (double)m.Sent.X)], [.. matched.Select(m => (double)m.Hit!.Nx)]);
        var fitY = Fit([.. matched.Select(m => (double)m.Sent.Y)], [.. matched.Select(m => (double)m.Hit!.Ny)]);

        double left = 0, leftSpread = 0, top = 0, topSpread = 0, sw = 0, sh = 0;
        if (matched.Count > 0)
        {
            // If the digitizer spans the whole screen 1:1, every point implies the same viewport origin.
            var lefts = matched.Select(m => (m.Sent.X / 10000.0 * m.Hit!.Sw) - m.Hit.Cx).ToList();
            var tops = matched.Select(m => (m.Sent.Y / 10000.0 * m.Hit!.Sh) - m.Hit.Cy).ToList();
            left = lefts.Average();
            leftSpread = lefts.Max() - lefts.Min();
            top = tops.Average();
            topSpread = tops.Max() - tops.Min();
            sw = matched[0].Hit!.Sw;
            sh = matched[0].Hit!.Sh;
        }

        var orientation = matched.Count >= 4 ? Orientation(matched) : null;
        return new TapAnalysis(taps, matched.Count, downs.Count - matched.Count, fitX, fitY, left, leftSpread, top, topSpread, sw, sh, orientation);
    }

    /// <summary>Pointer events at or after <paramref name="since"/>, with the lift check of each finger.</summary>
    public static GestureAnalysis AnalyzeGesture(IReadOnlyList<ProbeEvent> events, double since)
    {
        var ev = events.Where(e => e.T >= since && e.IsPointer).ToList();
        var ids = ev.Select(e => e.Id).Distinct().Order().ToList();
        return new GestureAnalysis(ev, ev.Count(e => e.Type == "pointermove"), ids, LiftProblems(ev));
    }

    /// <summary>Each finger must be lifted where it last was; a jump means a bogus (0, 0) lift frame.</summary>
    public static IReadOnlyList<string> LiftProblems(IReadOnlyList<ProbeEvent> events)
    {
        var bad = new List<string>();
        foreach (var id in events.Select(e => e.Id).Distinct().Order())
        {
            var mine = events.Where(e => e.Id == id).ToList();
            var up = mine.LastOrDefault(e => e.Type == "pointerup");
            var before = mine.LastOrDefault(e => e.Type != "pointerup");
            if (up is null)
            {
                bad.Add($"id {id}: no pointerup");
                continue;
            }

            if (before is null)
            {
                bad.Add($"id {id}: no move before up");
                continue;
            }

            var d = Math.Sqrt(Math.Pow(up.Cx - before.Cx, 2) + Math.Pow(up.Cy - before.Cy, 2));
            if (d > MaxLiftDistancePx)
            {
                bad.Add($"id {id}: lifted {d:F0}px away from its last position");
            }
        }

        return bad;
    }

    /// <summary>
    /// The M2 completion checks: 9 points map 1:1 in portrait, the swipe moves up and lifts in place,
    /// and the pinch has two fingers that move apart and lift in place.
    /// </summary>
    public static IReadOnlyList<Check> Evaluate(TapAnalysis taps, GestureAnalysis swipe, GestureAnalysis pinch, string expectedRotation = "r0")
    {
        var checks = new List<Check>
        {
            new("taps matched", taps.Matched == taps.Taps.Count && taps.Taps.Count > 0, $"{taps.Matched}/{taps.Taps.Count}, extra pointerdowns {taps.ExtraDowns}"),
            new("1:1 mapping", taps.Matched >= 2 && taps.LeftSpread <= MaxOriginSpreadPx && taps.TopSpread <= MaxOriginSpreadPx,
                $"origin spread x {taps.LeftSpread:F1}px, y {taps.TopSpread:F1}px (limit {MaxOriginSpreadPx}px)"),
            new("orientation", taps.Orientation?.Rotation == expectedRotation, $"{taps.Orientation?.Rotation ?? "unknown"} (expected {expectedRotation})"),
        };

        // Swipe: sent y 7500 -> 2500, so the finger must travel up by about half the screen.
        var swipeYs = swipe.Events.Select(e => e.Cy).ToList();
        var swipeUp = swipe.Events.Count >= 2 && swipe.Events[0].Cy - swipe.Events[^1].Cy >= 0.3 * Math.Max(1, ScreenHeight(swipe, taps));
        checks.Add(new("swipe moves", swipe.Moves >= 5 && swipeUp,
            swipe.Events.Count == 0 ? "no events" : $"{swipe.Moves} pointermove, y {swipeYs[0]:F0} -> {swipeYs[^1]:F0}"));
        checks.Add(new("swipe lift", swipe.LiftOk, swipe.LiftProblems.Count == 0 ? (swipe.Events.Count == 0 ? "no events" : "lifted in place") : string.Join("; ", swipe.LiftProblems)));

        // Pinch: radius 800 -> 2800 on each side, so the fingers must end farther apart than they started.
        var apart = false;
        var pinchDetail = $"{pinch.PointerIds.Count} distinct ids";
        if (pinch.PointerIds.Count == 2)
        {
            var a = pinch.Of(pinch.PointerIds[0]).ToList();
            var b = pinch.Of(pinch.PointerIds[1]).ToList();
            var startGap = Math.Abs(a[0].Cx - b[0].Cx);
            var endGap = Math.Abs(a[^1].Cx - b[^1].Cx);
            apart = endGap - startGap >= 0.2 * Math.Max(1, ScreenWidth(pinch, taps));
            pinchDetail += $", gap {startGap:F0} -> {endGap:F0}px";
        }

        checks.Add(new("pinch two fingers", pinch.PointerIds.Count == 2 && apart, pinchDetail));
        checks.Add(new("pinch lift", pinch.LiftOk, pinch.LiftProblems.Count == 0 ? (pinch.Events.Count == 0 ? "no events" : "lifted in place") : string.Join("; ", pinch.LiftProblems)));
        return checks;
    }

    private static double ScreenHeight(GestureAnalysis g, TapAnalysis t) => g.Events.Count > 0 && g.Events[0].Sh > 0 ? g.Events[0].Sh : t.ScreenHeight;

    private static double ScreenWidth(GestureAnalysis g, TapAnalysis t) => g.Events.Count > 0 && g.Events[0].Sw > 0 ? g.Events[0].Sw : t.ScreenWidth;
}
