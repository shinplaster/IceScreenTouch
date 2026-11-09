// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using IceScreenTouch.Core;
using IceScreenTouch.Probe;

namespace IceScreenTouch.Probe.Tests;

public sealed class ProbeReadinessTests
{
    private static ProbeEvent Hello(double t, string state = "visible", double? sy = null) => new() { Type = "hello", State = state, T = t, Sy = sy };

    [Fact]
    public void ReadyWhenSubscribedAndTheRightPageIsFreshlyVisible()
    {
        Assert.Equal(ProbeMissing.None, ProbeReadiness.Check(ProbePage.Touch, true, Hello(100), 105));
        Assert.Equal(ProbeMissing.None, ProbeReadiness.Check(ProbePage.Scroll, true, Hello(100, sy: 30000), 103));
    }

    [Fact]
    public void ReportsEverythingThatIsMissing()
    {
        Assert.Equal(ProbeMissing.Subscription | ProbeMissing.PageNotOpened, ProbeReadiness.Check(ProbePage.Touch, false, null, 0));
        Assert.Equal(ProbeMissing.PageNotInFront, ProbeReadiness.Check(ProbePage.Touch, true, Hello(100, "hidden"), 101));
        Assert.Equal(ProbeMissing.Subscription | ProbeMissing.PageNotInFront, ProbeReadiness.Check(ProbePage.Touch, false, Hello(100), 112));
    }

    [Fact]
    public void TheScrollPageNeedsFresherHellos()
    {
        // The touch page says hello every 5 s, the scroll page every 2 s.
        Assert.Equal(ProbeMissing.None, ProbeReadiness.Check(ProbePage.Touch, true, Hello(100), 107));
        Assert.Equal(ProbeMissing.PageNotInFront, ProbeReadiness.Check(ProbePage.Scroll, true, Hello(100, sy: 0), 107));
    }

    [Fact]
    public void TheKeyboardPageNeedsTheCursorInItsBox()
    {
        ProbeEvent Keys(bool? focused, double t = 100) => new() { Type = "hello", PageId = "keys", State = "visible", Focused = focused, T = t };

        Assert.Equal(ProbeMissing.None, ProbeReadiness.Check(ProbePage.Keys, true, Keys(true), 103));
        Assert.Equal(ProbeMissing.FieldNotFocused, ProbeReadiness.Check(ProbePage.Keys, true, Keys(false), 103));
        Assert.Equal(ProbeMissing.FieldNotFocused, ProbeReadiness.Check(ProbePage.Keys, true, Keys(null), 103));
        Assert.Equal(ProbeMissing.PageNotInFront, ProbeReadiness.Check(ProbePage.Keys, true, Keys(true), 107));
        Assert.Equal(ProbeMissing.WrongPage, ProbeReadiness.Check(ProbePage.Keys, true, Hello(100), 101));
        Assert.Equal(ProbeMissing.WrongPage, ProbeReadiness.Check(ProbePage.Touch, true, Keys(true), 101));
        Assert.Equal("/keys", ProbeReadiness.Path(ProbePage.Keys));
        Assert.Contains("tap the text box", ProbeReadiness.Describe(ProbeMissing.FieldNotFocused, ProbePage.Keys), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOtherPageIsTheWrongPage()
    {
        Assert.Equal(ProbeMissing.WrongPage, ProbeReadiness.Check(ProbePage.Touch, true, Hello(100, sy: 30000), 101));
        Assert.Equal(ProbeMissing.WrongPage, ProbeReadiness.Check(ProbePage.Scroll, true, Hello(100), 101));
    }

    [Fact]
    public void TheLatencyPageNamesItself()
    {
        var latency = new ProbeEvent { Type = "hello", State = "visible", T = 100, PageId = "latency", RttMs = 4 };

        Assert.Equal(ProbePage.Latency, ProbeReadiness.PageOf(latency));
        Assert.Equal(ProbeMissing.None, ProbeReadiness.Check(ProbePage.Latency, true, latency, 103));
        Assert.Equal(ProbeMissing.PageNotInFront, ProbeReadiness.Check(ProbePage.Latency, true, latency, 107));
        Assert.Equal(ProbeMissing.WrongPage, ProbeReadiness.Check(ProbePage.Touch, true, latency, 101));
        Assert.Equal(ProbeMissing.WrongPage, ProbeReadiness.Check(ProbePage.Latency, true, Hello(100), 101));
        Assert.Equal("/latency", ProbeReadiness.Path(ProbePage.Latency));
        Assert.Equal("/touch", ProbeReadiness.Path(ProbePage.Touch));
        Assert.Equal(ProbePage.Touch, ProbeReadiness.PageOf(new ProbeEvent { Type = "hello", PageId = "touch" }));
    }

    [Fact]
    public void DescribesForTheConsole()
    {
        Assert.Equal("nothing", ProbeReadiness.Describe(ProbeMissing.None, ProbePage.Touch));
        var text = ProbeReadiness.Describe(ProbeMissing.Subscription | ProbeMissing.WrongPage, ProbePage.Scroll);
        Assert.Contains("not subscribed", text, StringComparison.Ordinal);
        Assert.Contains("open /scroll", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PageUrlsUseThePortActuallyListenedOn()
    {
        await using var server = new ProbeServer(0, IPAddress.Loopback);
        await server.StartAsync();

        var urls = server.PageUrls(ProbePage.Scroll);

        Assert.All(urls, u => Assert.EndsWith($":{server.Port}/scroll?k={server.Key}", u, StringComparison.Ordinal));
        Assert.Equal(ProbeServer.LanAddresses().Count, urls.Count);
    }

    [Theory]
    [InlineData("Microsoft Wi-Fi Direct Virtual Adapter #2", true)]
    [InlineData("microsoft wi-fi direct virtual adapter", true)]
    [InlineData("Realtek Gaming 2.5GbE Family Controller #2", false)]
    [InlineData("Intel(R) Wi-Fi 6E AX211 160MHz", false)]
    [InlineData(null, false)]
    public void WifiDirectAdaptersAreLeftOut(string? description, bool wifiDirect)
    {
        Assert.Equal(wifiDirect, ProbeServer.IsWifiDirect(description));
    }

    [Fact]
    public void NoLanAddressIsOnAWifiDirectAdapter()
    {
        var wifiDirect = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => ProbeServer.IsWifiDirect(n.Description))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address.ToString());

        Assert.Empty(ProbeServer.LanAddresses().Intersect(wifiDirect));
    }

    [Fact]
    public async Task ACancelledProbeLiftsTheFingerItHadDown()
    {
        var sink = new FrameSink();
        await using var server = new ProbeServer(0, IPAddress.Loopback);
        using var cts = new CancellationTokenSource();
        sink.OnFirstDown = cts.Cancel; // cancel during the first tap's hold

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProbeRunner(new TouchPlayer(sink), server, TextWriter.Null).RunAsync(ct: cts.Token));

        Assert.Contains(sink.Frames[0], c => c.Tip);
        var lift = Assert.Single(sink.Frames, f => f.Length > 0 && f.All(c => !c.Tip));
        Assert.Equal(sink.Frames[0][0].X, lift[0].X); // lifted where it was
        Assert.Empty(sink.Frames[^1]);
    }

    [Fact]
    public async Task ACancelledScrollLiftsTheFinger()
    {
        var sink = new FrameSink();
        using var cts = new CancellationTokenSource();
        sink.OnFirstDown = () => cts.CancelAfter(50);
        var engine = new TouchGestureEngine(sink);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScrollProbe.PlayAsync(engine, ScrollScenario.All[1], cts.Token));

        Assert.False(engine.IsDown);
        Assert.False(engine.IsScrolling);
        Assert.Contains(sink.Frames, f => f.Length > 0 && f.All(c => !c.Tip));
    }

    private sealed class FrameSink : ITouchSink
    {
        public List<TouchContact[]> Frames { get; } = [];

        public Action? OnFirstDown { get; set; }

        public bool IsSubscribed => true;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
        {
            Frames.Add(contacts.ToArray());
            if (OnFirstDown is { } first && contacts.Span.Length > 0 && contacts.Span[0].Tip)
            {
                OnFirstDown = null;
                first();
            }

            return ValueTask.CompletedTask;
        }
    }
}
