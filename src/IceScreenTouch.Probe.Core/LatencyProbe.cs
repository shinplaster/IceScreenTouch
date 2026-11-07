using System.Globalization;
using IceScreenTouch.Core;

namespace IceScreenTouch.Probe;

/// <summary>Reads how bright the watched spot of the mirror picture is now (0 = black .. 255 = white).</summary>
public interface IPictureSampler
{
    /// <summary>The mean brightness of the spot, or null when the screen cannot be read right now.</summary>
    double? MeanLuma();
}

/// <summary>
/// One press or release of the latency probe. Times are <see cref="ProbeClock"/> seconds; the page's are on
/// its synced clock (error about half the round trip at most, usually far less).
/// </summary>
/// <param name="What">"down" or "up".</param>
/// <param name="SentAt">When the touch report was handed to Bluetooth.</param>
/// <param name="SeenAt">When the middle of the mirror picture changed colour on the PC screen, if it did.</param>
/// <param name="EventAt">When the page got the pointer event, if it reported it.</param>
/// <param name="PaintAt">The page's first animation frame after the colour change, if it reported it.</param>
public sealed record LatencyTrial(string What, double SentAt, double? SeenAt, double? EventAt, double? PaintAt)
{
    /// <summary>Touch sent -> the change seen on the PC: what a user feels.</summary>
    public double? TotalMs => SeenAt is { } s ? (s - SentAt) * 1000 : null;

    /// <summary>Touch sent -> the page got it (Bluetooth and iOS input).</summary>
    public double? TouchMs => EventAt is { } e ? (e - SentAt) * 1000 : null;

    /// <summary>The page's frame -> the change seen on the PC (iPhone display, encoding, Wi-Fi, receiver, compositor).</summary>
    public double? PictureMs => SeenAt is { } s && PaintAt is { } p ? (s - p) * 1000 : null;
}

/// <summary>What the latency probe measured.</summary>
/// <param name="Trials">Every press and release, in order.</param>
/// <param name="RttMs">The page's best clock-sync round trip (ms), if it said.</param>
/// <param name="SampleMs">How long one look at the screen took on average (the resolution of the times seen).</param>
public sealed record LatencyResult(IReadOnlyList<LatencyTrial> Trials, double? RttMs, double SampleMs)
{
    /// <summary>The presses.</summary>
    public IEnumerable<LatencyTrial> Downs => Trials.Where(t => t.What == "down");

    /// <summary>The releases.</summary>
    public IEnumerable<LatencyTrial> Ups => Trials.Where(t => t.What == "up");

    /// <summary>Presses and releases whose change was seen.</summary>
    public int Seen => Trials.Count(t => t.SeenAt is not null);

    /// <summary>The median of the values that exist, or null.</summary>
    public static double? Median(IEnumerable<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Where(v => v is not null).Select(v => v!.Value).Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}

/// <summary>
/// Measures the delay from a touch to its effect on the mirror window. The latency page (/latency) is black
/// and turns white while a finger is down. The probe presses in the middle of the screen over Bluetooth and
/// watches the middle of the mirror picture on the PC screen until it turns white, then releases and watches
/// it turn black again. The page reports, on a clock synced to the PC, when it got the touch and when it
/// painted, which splits the delay into the touch part and the picture part.
/// </summary>
public sealed class LatencyProbe(ITouchSink sink, ProbeServer server, IPictureSampler sampler, TextWriter output, Func<double>? clock = null)
{
    /// <summary>Brightness that separates black from white.</summary>
    public const double Threshold = 128;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly AxisPoint Center = TouchPlayer.Axis(HidConstants.AxisMax / 2, HidConstants.AxisMax / 2);

    private readonly Func<double> _clock = clock ?? ProbeClock.Now;
    private double _sampleSeconds;
    private int _samples;

    /// <summary>How long to wait for a change before giving up on a press or release.</summary>
    public TimeSpan ChangeTimeout { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long the spot must stay black before a press (the previous release has settled).</summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>A random pause before each press (from 0 up to this), so the presses do not lock to the frame rate.</summary>
    public TimeSpan Jitter { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>How long the finger stays down after the white was seen.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Whether the watched spot is black now (the page is shown there and nothing touches it).</summary>
    public bool SpotIsDark() => sampler.MeanLuma() is { } l && l < Threshold;

    /// <summary>Presses and releases <paramref name="trials"/> times; prints one line each and returns them all.</summary>
    public async Task<LatencyResult> RunAsync(int trials = 10, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(trials, 1);
        var player = new TouchPlayer(sink);
        var results = new List<LatencyTrial>();
        output.WriteLine(string.Create(Inv, $"latency probe: {trials} presses in the middle of the screen; the page clock is synced over a {server.HelloFor(ProbePage.Latency)?.RttMs:0.0} ms round trip"));
        output.WriteLine("            total   touch   picture   (ms: sent -> seen on the PC / sent -> page / page frame -> seen)");
        try
        {
            for (var i = 0; i < trials; i++)
            {
                if (!await WaitDarkAsync(ct).ConfigureAwait(false))
                {
                    output.WriteLine("  the middle of the mirror picture does not turn black: is /latency shown, in front, and not covered?");
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * Jitter.TotalMilliseconds), ct).ConfigureAwait(false);
                server.Clear();

                var downAt = _clock();
                await player.DownAsync(Center, ct).ConfigureAwait(false);
                var downSeen = Watch(white: true, ct);
                if (downSeen is not null)
                {
                    await Task.Delay(Hold, ct).ConfigureAwait(false);
                }

                var upAt = _clock();
                await player.LiftAsync(ct).ConfigureAwait(false);
                // Without a white, a black spot says nothing about the release.
                var upSeen = downSeen is null ? null : Watch(white: false, ct);

                // The page posts after its next frame; give the posts a moment to arrive.
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
                var events = server.Snapshot().Where(e => e.Type == "lat").ToList();
                var down = Match(events, "down", downAt, downSeen, downAt);
                var up = Match(events, "up", upAt, upSeen, upAt);
                results.Add(down);
                results.Add(up);
                output.WriteLine($"  #{i + 1,-2} down {Ms(down.TotalMs),5} {Ms(down.TouchMs),7} {Ms(down.PictureMs),9}");
                output.WriteLine($"      up   {Ms(up.TotalMs),5} {Ms(up.TouchMs),7} {Ms(up.PictureMs),9}");
            }
        }
        finally
        {
            if (player.IsDown)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await player.LiftAsync(timeout.Token).ConfigureAwait(false);
            }
        }

        var result = new LatencyResult(results, server.HelloFor(ProbePage.Latency)?.RttMs, _samples > 0 ? _sampleSeconds / _samples * 1000 : 0);
        output.WriteLine(string.Create(
            Inv,
            $"median: press {Ms(LatencyResult.Median(result.Downs.Select(t => t.TotalMs)))} ms (touch {Ms(LatencyResult.Median(result.Downs.Select(t => t.TouchMs)))}, picture {Ms(LatencyResult.Median(result.Downs.Select(t => t.PictureMs)))}), release {Ms(LatencyResult.Median(result.Ups.Select(t => t.TotalMs)))} ms; one look at the screen = {result.SampleMs:0.0} ms"));
        return result;
    }

    private static string Ms(double? ms) => ms is { } v ? v.ToString("0", Inv) : "-";

    // The pointer event of this press or release: the first one of its kind that the page got after the send.
    private static LatencyTrial Match(List<ProbeEvent> events, string what, double sentAt, double? seenAt, double after)
    {
        var e = events.FirstOrDefault(e => e.What == what && e.EventAt is { } t && t > after - 0.05);
        return new LatencyTrial(what, sentAt, seenAt, e?.EventAt, e?.PaintAt);
    }

    // Looks at the spot until it turns white (or black); returns the time of the first look that saw it.
    private double? Watch(bool white, CancellationToken ct)
    {
        var deadline = _clock() + ChangeTimeout.TotalSeconds;
        while (_clock() < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var before = _clock();
            var luma = sampler.MeanLuma();
            var after = _clock();
            _sampleSeconds += after - before;
            _samples++;
            if (luma is { } l && (white ? l >= Threshold : l < Threshold))
            {
                // The change happened at some point before this look: take the middle of the look.
                return (before + after) / 2;
            }
        }

        return null;
    }

    private async Task<bool> WaitDarkAsync(CancellationToken ct)
    {
        var deadline = _clock() + 3;
        var darkSince = double.NaN;
        while (_clock() < deadline)
        {
            var dark = SpotIsDark();
            var now = _clock();
            if (!dark)
            {
                darkSince = double.NaN;
            }
            else if (double.IsNaN(darkSince))
            {
                darkSince = now;
            }
            else if (now - darkSince >= Settle.TotalSeconds)
            {
                return true;
            }

            await Task.Delay(10, ct).ConfigureAwait(false);
        }

        return false;
    }
}
