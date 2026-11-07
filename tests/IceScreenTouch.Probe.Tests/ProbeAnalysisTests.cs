using IceScreenTouch.Probe;

namespace IceScreenTouch.Probe.Tests;

/// <summary>A simulated iPhone running the probe page: 393 x 852 screen, Safari viewport starting 59 px down.</summary>
internal static class Phone
{
    public const double ScreenW = 393, ScreenH = 852, Top = 59, ViewW = 393, ViewH = 734;

    public static ProbeEvent Ev(string type, long id, double cx, double cy, double t, double sw = ScreenW, double sh = ScreenH) => new()
    {
        Type = type,
        Id = id,
        PointerType = "touch",
        Cx = cx,
        Cy = cy,
        Nx = (int)Math.Round(cx / ViewW * 10000),
        Ny = (int)Math.Round(cy / ViewH * 10000),
        Vw = ViewW,
        Vh = ViewH,
        Sw = sw,
        Sh = sh,
        T = t,
    };

    /// <summary>Where a portrait digitizer position lands in the page (client px).</summary>
    public static (double X, double Y) Portrait(int x, int y) => ((x / 10000.0 * ScreenW) - 0, (y / 10000.0 * ScreenH) - Top);

    /// <summary>9 taps sent 1 s apart, each received 70 ms later at <paramref name="map"/>(x, y).</summary>
    public static (List<SentTap> Sent, List<ProbeEvent> Events) Taps(Func<int, int, (double X, double Y)> map, double latency = 0.07)
    {
        var sent = new List<SentTap>();
        var events = new List<ProbeEvent>();
        var t = 10.0;
        long id = 1;
        foreach (var (x, y) in ProbeAnalysis.TapGrid)
        {
            sent.Add(new SentTap(x, y, t));
            var (cx, cy) = map(x, y);
            events.Add(Ev("pointerdown", id, cx, cy, t + latency));
            events.Add(Ev("pointerup", id, cx, cy, t + latency + 0.12));
            id++;
            t += 1.0;
        }

        return (sent, events);
    }

    /// <summary>The probe swipe (y 7500 -> 2500) as the page would see it.</summary>
    public static List<ProbeEvent> Swipe(double t0, bool liftAtOrigin = false)
    {
        var ev = new List<ProbeEvent>();
        for (var i = 0; i <= 20; i++)
        {
            var (x, y) = Portrait(5000, 7500 - (5000 * i / 20));
            ev.Add(Ev(i == 0 ? "pointerdown" : "pointermove", 50, x, y, t0 + (0.03 * i)));
        }

        var last = ev[^1];
        ev.Add(liftAtOrigin ? Ev("pointerup", 50, 0, -Top, t0 + 0.7) : Ev("pointerup", 50, last.Cx, last.Cy, t0 + 0.7));
        return ev;
    }

    /// <summary>The probe pinch (radius 800 -> 2800 around the center) as two pointers.</summary>
    public static List<ProbeEvent> Pinch(double t0, int fingers = 2)
    {
        var ev = new List<ProbeEvent>();
        for (var i = 0; i <= 20; i++)
        {
            var r = 800 + (2000 * i / 20);
            var type = i == 0 ? "pointerdown" : "pointermove";
            var (ax, ay) = Portrait(5000 - r, 5000);
            ev.Add(Ev(type, 60, ax, ay, t0 + (0.03 * i)));
            if (fingers == 2)
            {
                var (bx, by) = Portrait(5000 + r, 5000);
                ev.Add(Ev(type, 61, bx, by, t0 + (0.03 * i)));
            }
        }

        foreach (var id in fingers == 2 ? new long[] { 60, 61 } : [60])
        {
            var last = ev.Last(e => e.Id == id);
            ev.Add(Ev("pointerup", id, last.Cx, last.Cy, t0 + 0.7));
        }

        return ev;
    }
}

public class ProbeAnalysisTests
{
    [Fact]
    public void FitRecoversALine()
    {
        var fit = ProbeAnalysis.Fit([0, 1, 2, 3], [1, 3, 5, 7]);
        Assert.NotNull(fit);
        Assert.Equal(2.0, fit.Value.A, 9);
        Assert.Equal(1.0, fit.Value.B, 9);
    }

    [Fact]
    public void FitNeedsSpreadInX()
    {
        Assert.Null(ProbeAnalysis.Fit([5, 5, 5], [1, 2, 3]));
        Assert.Null(ProbeAnalysis.Fit([5], [1]));
    }

    [Fact]
    public void PortraitTapsMatchOneToOne()
    {
        var (sent, events) = Phone.Taps(Phone.Portrait);
        var a = ProbeAnalysis.AnalyzeTaps(sent, events);

        Assert.Equal(9, a.Matched);
        Assert.Equal(0, a.ExtraDowns);
        Assert.Equal(0, a.OriginLeft, 6);
        Assert.Equal(Phone.Top, a.OriginTop, 6);
        Assert.True(a.LeftSpread < 1e-9 && a.TopSpread < 1e-9);
        Assert.Equal("r0", a.Orientation?.Rotation);
        Assert.All(a.Taps, t => Assert.Equal(70, t.LatencyMs!.Value, 6));
    }

    [Theory]
    [InlineData("r90")]
    [InlineData("r180")]
    [InlineData("r270")]
    public void OrientationIsNamedLikePoc3(string rotation)
    {
        // Landscape pages: the digitizer stays in the portrait frame, so the axes swap and/or flip.
        Func<int, int, (double, double)> map = rotation switch
        {
            "r90" => (x, y) => (y / 10000.0 * 852, (1 - (x / 10000.0)) * 393),
            "r180" => (x, y) => ((1 - (x / 10000.0)) * 393, (1 - (y / 10000.0)) * 852),
            _ => (x, y) => ((1 - (y / 10000.0)) * 852, x / 10000.0 * 393),
        };
        var (sent, events) = Phone.Taps(map);
        var o = ProbeAnalysis.AnalyzeTaps(sent, events).Orientation;

        Assert.NotNull(o);
        Assert.Equal(rotation, o.Rotation);
        Assert.Equal(rotation is "r90" or "r270", o.Swapped);
    }

    [Fact]
    public void NonLinearMappingShowsAsOriginSpread()
    {
        // A digitizer that only covers 80% of the screen width implies a different origin per column.
        var (sent, events) = Phone.Taps((x, y) => (x / 10000.0 * 393 * 0.8, (y / 10000.0 * 852) - Phone.Top));
        var a = ProbeAnalysis.AnalyzeTaps(sent, events);
        Assert.True(a.LeftSpread > ProbeAnalysis.MaxOriginSpreadPx);
    }

    [Fact]
    public void MissingAndExtraPointerdownsAreCounted()
    {
        var (sent, events) = Phone.Taps(Phone.Portrait);
        events.RemoveAll(e => e.Id == 3); // tap 3 never arrived
        events.Add(Phone.Ev("pointerdown", 99, 10, 10, 15.5)); // a stray touch inside tap 6's window, after its own down
        var a = ProbeAnalysis.AnalyzeTaps(sent, events);

        Assert.Equal(8, a.Matched);
        Assert.Null(a.Taps[2].Hit);
        Assert.Equal(1, a.ExtraDowns);
    }

    [Fact]
    public void ADownBeforeTheSendIsNotMatched()
    {
        // Windows are [send, next send): a down that precedes its send cannot belong to that tap.
        var sent = new List<SentTap> { new(5000, 5000, 10) };
        var a = ProbeAnalysis.AnalyzeTaps(sent, [Phone.Ev("pointerdown", 1, 196, 367, 9.99)]);
        Assert.Equal(0, a.Matched);
        Assert.Equal(1, a.ExtraDowns);
    }

    [Fact]
    public void LiftAtTheLastPositionIsOk()
    {
        Assert.Empty(ProbeAnalysis.LiftProblems(Phone.Swipe(0)));
    }

    [Fact]
    public void LiftJumpIsDetected()
    {
        var problems = ProbeAnalysis.LiftProblems(Phone.Swipe(0, liftAtOrigin: true));
        Assert.Single(problems);
        Assert.Contains("lifted", problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MissingPointerupIsAProblem()
    {
        var ev = Phone.Swipe(0);
        ev.RemoveAt(ev.Count - 1);
        Assert.Equal(["id 50: no pointerup"], ProbeAnalysis.LiftProblems(ev));
    }

    [Fact]
    public void GestureOnlyTakesPointerEventsSinceTheStart()
    {
        var ev = new List<ProbeEvent> { Phone.Ev("pointerdown", 1, 1, 1, 0.5), Phone.Ev("pointercancel", 50, 1, 1, 5) };
        ev.AddRange(Phone.Swipe(1));
        var g = ProbeAnalysis.AnalyzeGesture(ev, 1);

        Assert.Equal(22, g.Events.Count);
        Assert.Equal(20, g.Moves);
        Assert.Equal([50L], g.PointerIds);
        Assert.True(g.LiftOk);
    }

    [Fact]
    public void AGoodRunPassesEveryCheck()
    {
        var (sent, events) = Phone.Taps(Phone.Portrait);
        events.AddRange(Phone.Swipe(30));
        events.AddRange(Phone.Pinch(40));

        var checks = Evaluate(sent, events);

        Assert.All(checks, c => Assert.True(c.Ok, $"{c.Name}: {c.Detail}"));
    }

    [Fact]
    public void OneFingerPinchFails()
    {
        var (sent, events) = Phone.Taps(Phone.Portrait);
        events.AddRange(Phone.Swipe(30));
        events.AddRange(Phone.Pinch(40, fingers: 1));

        var checks = Evaluate(sent, events);

        Assert.False(checks.Single(c => c.Name == "pinch two fingers").Ok);
        Assert.True(checks.Single(c => c.Name == "swipe moves").Ok);
    }

    [Fact]
    public void LiftJumpInTheSwipeFails()
    {
        var (sent, events) = Phone.Taps(Phone.Portrait);
        events.AddRange(Phone.Swipe(30, liftAtOrigin: true));
        events.AddRange(Phone.Pinch(40));

        Assert.False(Evaluate(sent, events).Single(c => c.Name == "swipe lift").Ok);
    }

    [Fact]
    public void LandscapeFailsThePortraitExpectationButPassesWhenExpected()
    {
        var (sent, events) = Phone.Taps((x, y) => (y / 10000.0 * 852, (1 - (x / 10000.0)) * 393));
        var taps = ProbeAnalysis.AnalyzeTaps(sent, events);
        var none = ProbeAnalysis.AnalyzeGesture([], 0);

        Assert.False(ProbeAnalysis.Evaluate(taps, none, none).Single(c => c.Name == "orientation").Ok);
        Assert.True(ProbeAnalysis.Evaluate(taps, none, none, "r90").Single(c => c.Name == "orientation").Ok);
    }

    [Fact]
    public void NothingReceivedFailsEverything()
    {
        var sent = ProbeAnalysis.TapGrid.Select((p, i) => new SentTap(p.X, p.Y, i)).ToList();
        var none = ProbeAnalysis.AnalyzeGesture([], 0);
        var checks = ProbeAnalysis.Evaluate(ProbeAnalysis.AnalyzeTaps(sent, []), none, none);
        Assert.All(checks, c => Assert.False(c.Ok, c.Name));
    }

    private static IReadOnlyList<Check> Evaluate(List<SentTap> sent, List<ProbeEvent> events) =>
        ProbeAnalysis.Evaluate(
            ProbeAnalysis.AnalyzeTaps(sent, events.Where(e => e.T < 30).ToList()),
            ProbeAnalysis.AnalyzeGesture(events.Where(e => e.T < 40).ToList(), 30),
            ProbeAnalysis.AnalyzeGesture(events, 40));
}
