using System.Net;
using IceScreenTouch.Probe;

namespace IceScreenTouch.Probe.Tests;

public sealed class ScrollProbeTests
{
    private static ProbeEvent Ev(string type, double t, double? sy = null, double cy = 0) => new() { Type = type, T = t, Sy = sy, Cy = cy };

    [Fact]
    public void ATrialComparesTheScrollWithTheFingerAndMeasuresTheTail()
    {
        ProbeEvent[] events =
        [
            Ev("touchstart", 1.00, 30000, cy: 400),
            Ev("scroll", 1.05, 30020),
            Ev("scroll", 1.15, 30085),
            Ev("touchend", 1.25, 30085, cy: 315),
            Ev("scroll", 1.40, 30200),
            Ev("scroll", 1.90, 30255),
        ];

        var trial = ScrollTrial.Analyze(events, 30000, 85);

        Assert.Equal(255, trial.Scrolled);
        Assert.Equal(3.0, trial.Ratio, 6);
        Assert.Equal(85, trial.TouchTravel);
        Assert.Equal(650, trial.Tail!.Value.TotalMilliseconds, 3);
    }

    [Fact]
    public void ATrialWithoutEventsScrolledNothing()
    {
        var trial = ScrollTrial.Analyze([], 100, 85);

        Assert.Equal(0, trial.Scrolled);
        Assert.Equal(0, trial.Ratio);
        Assert.Null(trial.TouchTravel);
        Assert.Null(trial.Tail);
    }

    [Fact]
    public void ScenariosAreWheelDownAndCanBeReversed()
    {
        Assert.All(ScrollScenario.All, sc => Assert.True(sc.Notches < 0));
        Assert.Contains(ScrollScenario.All, sc => sc.Wheel.Count > 1);
        var spin = ScrollScenario.All[1];
        Assert.Equal(-spin.Notches, spin.Reversed().Notches);
        Assert.Equal(spin.Wheel.Select(w => w.AtMs), spin.Reversed().Wheel.Select(w => w.AtMs));
    }

    [Fact]
    public async Task PlayFeedsTheWheelAndRunsUntilTheFingerIsUp()
    {
        var sink = new CountingSink();
        var engine = new Core.TouchGestureEngine(sink) { Wheel = Core.WheelOptions.Default with { Momentum = 0 } };

        await ScrollProbe.PlayAsync(engine, ScrollScenario.All[1], CancellationToken.None);

        Assert.False(engine.IsScrolling);
        Assert.Equal(1, engine.Stats.Scrolls);
        Assert.True(sink.Reports > 10, $"{sink.Reports} reports");
    }

    private sealed class CountingSink : Core.ITouchSink
    {
        public int Reports { get; private set; }

        public bool IsSubscribed => true;

        public ValueTask SendAsync(ReadOnlyMemory<Core.TouchContact> contacts, CancellationToken cancellationToken)
        {
            Reports++;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void TheServerTracksTheScrollPosition()
    {
        var clock = 10.0;
        var server = new ProbeServer(0, IPAddress.Loopback, () => clock);
        Assert.Null(server.LastScrollY);

        server.Add("""{"type":"hello","state":"visible","sy":30000}""");
        Assert.Equal(30000, server.LastScrollY);
        Assert.Equal(double.NegativeInfinity, server.LastScrollAt);

        clock = 11;
        server.Add("""{"type":"scroll","sy":30120.5}""");
        Assert.Equal(30120.5, server.LastScrollY);
        Assert.Equal(11, server.LastScrollAt);
        Assert.Single(server.Snapshot());
    }

    [Fact]
    public async Task ServesTheScrollPage()
    {
        await using var server = new ProbeServer(0, IPAddress.Loopback);
        await server.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };

        var body = await http.GetStringAsync(new Uri("/scroll", UriKind.Relative));

        Assert.Contains("<title>scroll probe</title>", body, StringComparison.Ordinal);
        Assert.Contains("type:'scroll'", body, StringComparison.Ordinal);
    }
}
