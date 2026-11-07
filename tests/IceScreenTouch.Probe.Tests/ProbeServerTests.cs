using System.Net;
using System.Text;
using IceScreenTouch.Probe;

namespace IceScreenTouch.Probe.Tests;

public sealed class ProbeServerTests
{
    [Fact]
    public Task ServesTheProbePage() => With(async (_, http) =>
    {
        using var response = await http.GetAsync(new Uri("/touch", UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("<title>touch probe</title>", body, StringComparison.Ordinal);
        Assert.Contains("fetch('/e'", body, StringComparison.Ordinal);
    });

    [Fact]
    public Task TheOldAddressOfTheTouchPageRedirects() => With(async (_, http) =>
    {
        using var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = http.BaseAddress };
        using var response = await noRedirect.GetAsync(new Uri("/", UriKind.Relative));
        var page = await http.GetStringAsync(new Uri("/", UriKind.Relative)); // followed

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/touch", response.Headers.Location?.OriginalString);
        Assert.Contains("<title>touch probe</title>", page, StringComparison.Ordinal);
        Assert.Contains("pg:'touch'", page, StringComparison.Ordinal);
    });

    [Fact]
    public Task RecordsPostedPointerEventsWithTheServerTime() => With(async (server, http) =>
    {
        // The page posts with fetch's default text/plain content type.
        const string json = """{"type":"pointerdown","id":7,"ptype":"touch","cx":12.5,"cy":300,"nx":318,"ny":4087,"vw":393,"vh":734,"sw":393,"sh":852,"buttons":1,"seq":3}""";
        using var content = new StringContent(json, Encoding.UTF8, "text/plain");
        using var response = await http.PostAsync(new Uri($"/e?k={server.Key}", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var e = Assert.Single(server.Snapshot());
        Assert.Equal("pointerdown", e.Type);
        Assert.Equal(7, e.Id);
        Assert.Equal(12.5, e.Cx);
        Assert.Equal(4087, e.Ny);
        Assert.Equal(852, e.Sh);
        Assert.Equal(42.5, e.T);
    });

    [Fact]
    public Task HelloIsKeptApartFromTheEvents() => With(async (server, http) =>
    {
        using var content = new StringContent("""{"type":"hello","state":"visible","vw":393,"vh":734}""");
        using var response = await http.PostAsync(new Uri($"/e?k={server.Key}", UriKind.Relative), content);

        Assert.Empty(server.Snapshot());
        Assert.Equal("visible", server.LastHello?.State);
        Assert.Equal(42.5, server.LastHello?.T);
    });

    [Theory]
    [InlineData("/e")]
    [InlineData("/e?k=")]
    [InlineData("/e?k=0123456789abcdef01234567")]
    public Task AnEventWithoutTheRunsKeyIsRefused(string path) => With(async (server, http) =>
    {
        // Another host, or a web page on this PC, must not be able to fake the page's hello and start the probe.
        using var content = new StringContent("""{"type":"hello","pg":"keys","state":"visible","f":true}""");
        using var response = await http.PostAsync(new Uri(path, UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(server.LastHello);
    });

    [Fact]
    public void TheFirstSenderOwnsTheRun()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        var phone = IPAddress.Parse("192.168.1.20");

        Assert.True(server.Admit(phone.MapToIPv6())); // Kestrel may report it IPv4-mapped
        Assert.True(server.Admit(phone));
        Assert.False(server.Admit(IPAddress.Parse("192.168.1.66"))); // someone who sniffed the key
        Assert.False(server.Admit(null));
        Assert.Equal(phone, server.PageAddress);
    }

    [Fact]
    public async Task ThePageUrlsCarryTheKeyAndEachServerHasItsOwn()
    {
        await using var a = new ProbeServer(0, IPAddress.Loopback);
        await using var b = new ProbeServer(0, IPAddress.Loopback);
        await a.StartAsync();

        Assert.Matches("^[0-9a-f]{24}$", a.Key);
        Assert.NotEqual(a.Key, b.Key);
        Assert.All(a.PageUrls(ProbePage.Keys), u => Assert.EndsWith($"/keys?k={a.Key}", u, StringComparison.Ordinal));
    }

    [Fact]
    public void MalformedJsonIsIgnored()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        server.Add("{not json");
        server.Add("null");
        Assert.Empty(server.Snapshot());
        Assert.Null(server.LastHello);
    }

    [Fact]
    public Task RefusesAnOversizedBody() => With(async (server, http) =>
    {
        var padding = new string(' ', ProbeServer.MaxBodyBytes);
        using var content = new StringContent("""{"type":"pointerdown","id":1}""" + padding);
        using var response = await http.PostAsync(new Uri($"/e?k={server.Key}", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(server.Snapshot());
    });

    [Fact]
    public void KeepsAtMostMaxEvents()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        for (var i = 0; i < ProbeServer.MaxEvents + 5; i++)
        {
            server.Add("""{"type":"pointermove","id":1}""");
        }

        server.Add("""{"type":"hello","state":"visible"}""");

        Assert.Equal(ProbeServer.MaxEvents, server.Snapshot().Count);
        Assert.Equal("visible", server.LastHello?.State); // the page's state still updates
    }

    [Fact]
    public void ClearForgetsTheEvents()
    {
        var server = new ProbeServer(0, IPAddress.Loopback);
        server.Add("""{"type":"pointermove","id":1}""");
        Assert.Single(server.Snapshot());
        server.Clear();
        Assert.Empty(server.Snapshot());
    }


    [Fact]
    public Task ServesTheLatencyPageAndTheClock() => With(async (_, http) =>
    {
        var page = await http.GetStringAsync(new Uri("/latency", UriKind.Relative));
        using var response = await http.GetAsync(new Uri("/t", UriKind.Relative));
        var clock = await response.Content.ReadAsStringAsync();

        Assert.Contains("<title>latency probe</title>", page, StringComparison.Ordinal);
        Assert.Contains("fetch('/t'", page, StringComparison.Ordinal);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal("42.5", clock);
    });

    [Fact]
    public void ReadsTheLatencyPagesFields()
    {
        var server = new ProbeServer(0, IPAddress.Loopback, () => 7);
        server.Add("""{"type":"lat","what":"down","ts":12.25,"tp":12.26,"rtt":3.5,"nx":5000,"ny":5000,"seq":4}""");
        server.Add("""{"type":"hello","pg":"latency","state":"visible","rtt":3.5}""");

        var e = Assert.Single(server.Snapshot());
        Assert.Equal(("lat", "down", 12.25, 12.26, 3.5), (e.Type, e.What, e.EventAt, e.PaintAt, e.RttMs));
        Assert.Equal("latency", server.LastHello?.PageId);
    }


    [Fact]
    public Task ServesTheKeyboardPage() => With(async (_, http) =>
    {
        var page = await http.GetStringAsync(new Uri("/keys", UriKind.Relative));

        Assert.Contains("<title>keyboard probe</title>", page, StringComparison.Ordinal);
        Assert.Contains("pg:'keys'", page, StringComparison.Ordinal);
        Assert.Contains("autocorrect=\"off\"", page, StringComparison.Ordinal);
    });

    [Fact]
    public void ReadsTheKeyboardPagesFields()
    {
        var server = new ProbeServer(0, IPAddress.Loopback, () => 7);
        server.Add("""{"type":"keydown","key":"a","code":"KeyA","mods":8,"v":"ab","ss":0,"se":2,"seq":3}""");
        server.Add("""{"type":"input","it":"insertText","ic":true,"v":"ab","ss":2,"se":2}""");
        server.Add("""{"type":"hello","pg":"keys","state":"visible","f":true,"v":""}""");

        var events = server.Snapshot();
        Assert.Equal(("a", "KeyA", 8, "ab", 0, 2), (events[0].Key, events[0].Code, events[0].Mods, events[0].Value, events[0].SelectionStart, events[0].SelectionEnd));
        Assert.Equal(("insertText", true), (events[1].InputType, events[1].Composing));
        Assert.Equal(ProbePage.Keys, ProbeReadiness.PageOf(server.HelloFor(ProbePage.Keys)!));
        Assert.True(server.HelloFor(ProbePage.Keys)!.Focused);
    }

    [Fact]
    public void AStrayHelloFromAnotherTabDoesNotHideThePagesOwn()
    {
        var server = new ProbeServer(0, IPAddress.Loopback, () => 7);
        server.Add("""{"type":"hello","state":"visible"}""");
        Assert.Equal(ProbePage.Touch, ProbeReadiness.PageOf(server.HelloFor(ProbePage.Latency)!)); // only another page so far

        server.Add("""{"type":"hello","pg":"latency","state":"visible","rtt":3}""");
        server.Add("""{"type":"hello","state":"hidden"}"""); // an old touch page in a background tab

        Assert.Equal("latency", server.HelloFor(ProbePage.Latency)?.PageId);
        Assert.Equal("hidden", server.HelloFor(ProbePage.Touch)?.State);
        Assert.Equal(ProbeMissing.None, ProbeReadiness.Check(ProbePage.Latency, true, server.HelloFor(ProbePage.Latency), 8));
    }

    // Starts a server on loopback only (so the Windows firewall does not ask anything) with a fixed clock.
    private static async Task With(Func<ProbeServer, HttpClient, Task> test)
    {
        await using var server = new ProbeServer(0, IPAddress.Loopback, () => 42.5);
        await server.StartAsync();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        await test(server, http);
    }
}
