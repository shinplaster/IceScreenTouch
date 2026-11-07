using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using IceScreenTouch.Platform.Windows;
using Xunit.Abstractions;

namespace IceScreenTouch.Windows.Tests;

// Screen sampling for the latency probe, against a topmost stand-in window. No input is injected.
public sealed class ScreenSamplerTests(ITestOutputHelper output) : IDisposable
{
    private readonly StandInWindow _window = StandInWindow.Show(new Rectangle(240, 200, 300, 300), topMost: true, color: Color.Black);

    public void Dispose() => _window.Dispose();

    private Point Center
    {
        get
        {
            var c = _window.ClientOnScreen;
            return new Point(c.Left + (c.Width / 2), c.Top + (c.Height / 2));
        }
    }

    [Fact]
    public void ReadsTheBrightnessOfTheWindowUnderneath()
    {
        using var sampler = new ScreenSampler();
        var black = sampler.MeanLuma(Center.X, Center.Y);

        _window.ShowBlock(new Rectangle(0, 0, 300, 300), Color.White);
        var white = WaitFor(sampler, l => l > 200, TimeSpan.FromSeconds(2));

        Assert.NotNull(black);
        Assert.True(black < 10, $"black read as {black}");
        Assert.NotNull(white);
    }

    [Fact]
    public void SamplesAreFastAndAChangeShowsUpWithinAFewFrames()
    {
        using var sampler = new ScreenSampler();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 50; i++)
        {
            _ = sampler.MeanLuma(Center.X, Center.Y);
        }

        var perSample = watch.Elapsed.TotalMilliseconds / 50;

        // Repaint -> the sampler sees it: the part of a latency measurement that is the PC's compositor.
        var delays = new List<double>();
        for (var i = 0; i < 6; i++)
        {
            var white = i % 2 == 0;
            var start = Stopwatch.GetTimestamp();
            _window.ShowBlock(new Rectangle(0, 0, 300, 300), white ? Color.White : Color.Black);
            Assert.NotNull(WaitFor(sampler, l => white ? l > 128 : l < 128, TimeSpan.FromSeconds(2)));
            delays.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }

        output.WriteLine(FormattableString.Invariant($"per sample {perSample:F2} ms; repaint -> seen {string.Join(" ", delays.Select(d => d.ToString("F0", CultureInfo.InvariantCulture)))} ms"));
        // On some Windows builds a screen BitBlt waits for the next composed frame (16.7 ms at 60 Hz); that
        // costs nothing, since the screen cannot change between frames. Allow one frame at 50 Hz plus slack.
        Assert.True(perSample < 25, FormattableString.Invariant($"a sample took {perSample:F1} ms"));
        Assert.True(delays.Order().ElementAt(delays.Count / 2) < 100, "a repaint took too long to show");
    }

    private double? WaitFor(ScreenSampler sampler, Func<double, bool> done, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            if (sampler.MeanLuma(Center.X, Center.Y) is { } l && done(l))
            {
                return l;
            }
        }

        return null;
    }
}
