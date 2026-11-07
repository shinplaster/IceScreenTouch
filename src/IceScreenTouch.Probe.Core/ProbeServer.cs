using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace IceScreenTouch.Probe;

/// <summary>Monotonic seconds shared by the sender and the probe server, so latencies can be compared.</summary>
public static class ProbeClock
{
    private static readonly long Start = Stopwatch.GetTimestamp();

    public static double Now() => Stopwatch.GetElapsedTime(Start).TotalSeconds;
}

/// <summary>One event the probe page reported. <see cref="T"/> is the server receive time (<see cref="ProbeClock"/>).</summary>
public sealed record ProbeEvent
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("id")] public long Id { get; init; }
    [JsonPropertyName("ptype")] public string PointerType { get; init; } = string.Empty;
    [JsonPropertyName("cx")] public double Cx { get; init; }
    [JsonPropertyName("cy")] public double Cy { get; init; }
    [JsonPropertyName("nx")] public int Nx { get; init; }
    [JsonPropertyName("ny")] public int Ny { get; init; }
    [JsonPropertyName("vw")] public double Vw { get; init; }
    [JsonPropertyName("vh")] public double Vh { get; init; }
    [JsonPropertyName("sw")] public double Sw { get; init; }
    [JsonPropertyName("sh")] public double Sh { get; init; }
    [JsonPropertyName("buttons")] public int Buttons { get; init; }
    [JsonPropertyName("seq")] public long Seq { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("sy")] public double? Sy { get; init; }
    /// <summary>Which page said hello ("latency"; the older pages send none).</summary>
    [JsonPropertyName("pg")] public string? PageId { get; init; }
    /// <summary>Latency page: the best round trip of its clock sync, in ms.</summary>
    [JsonPropertyName("rtt")] public double? RttMs { get; init; }
    /// <summary>Latency page: "down" or "up".</summary>
    [JsonPropertyName("what")] public string? What { get; init; }
    /// <summary>Latency page: when the pointer event happened, in <see cref="ProbeClock"/> seconds (synced clock).</summary>
    [JsonPropertyName("ts")] public double? EventAt { get; init; }
    /// <summary>Latency page: the first animation frame after the colour change, in <see cref="ProbeClock"/> seconds.</summary>
    [JsonPropertyName("tp")] public double? PaintAt { get; init; }
    /// <summary>Keyboard page: KeyboardEvent.key.</summary>
    [JsonPropertyName("key")] public string? Key { get; init; }
    /// <summary>Keyboard page: KeyboardEvent.code.</summary>
    [JsonPropertyName("code")] public string? Code { get; init; }
    /// <summary>Keyboard page: modifiers held (1 Shift, 2 Ctrl, 4 Alt, 8 Meta = Command).</summary>
    [JsonPropertyName("mods")] public int? Mods { get; init; }
    /// <summary>Keyboard page: the text in the box after the event.</summary>
    [JsonPropertyName("v")] public string? Value { get; init; }
    /// <summary>Keyboard page: selection start in the box.</summary>
    [JsonPropertyName("ss")] public int? SelectionStart { get; init; }
    /// <summary>Keyboard page: selection end in the box.</summary>
    [JsonPropertyName("se")] public int? SelectionEnd { get; init; }
    /// <summary>Keyboard page: InputEvent.inputType.</summary>
    [JsonPropertyName("it")] public string? InputType { get; init; }
    /// <summary>Keyboard page: InputEvent.isComposing (an input method is converting).</summary>
    [JsonPropertyName("ic")] public bool? Composing { get; init; }
    /// <summary>Keyboard page: the box has the cursor (hello).</summary>
    [JsonPropertyName("f")] public bool? Focused { get; init; }
    [JsonPropertyName("t")] public double T { get; init; }

    public bool IsPointer => Type is "pointerdown" or "pointermove" or "pointerup";
}

[JsonSerializable(typeof(ProbeEvent))]
[JsonSerializable(typeof(List<ProbeEvent>))]
[JsonSourceGenerationOptions(WriteIndented = false, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class ProbeJson : JsonSerializerContext;

/// <summary>
/// Tiny HTTP server that serves the touch-probe page and collects the events it POSTs back.
/// Port of poc/poc2-ble-hid/probe_server.py. Open http://&lt;PC-IP&gt;:&lt;port&gt;/touch (or another page) in iPhone Safari.
/// </summary>
public sealed class ProbeServer : IAsyncDisposable
{
    /// <summary>
    /// The largest request body accepted. A page event is well under 1 KB; anything on the subnet can reach the
    /// port while a probe waits, so a sender must not be able to make the app hold megabytes per request.
    /// </summary>
    public const int MaxBodyBytes = 16 * 1024;

    /// <summary>
    /// How many events are kept until <see cref="Clear"/> (later ones are dropped). A probe run records a few
    /// thousand at most; the cap bounds what a flood from the subnet can cost.
    /// </summary>
    public const int MaxEvents = 100_000;

    private readonly IPAddress _bind;
    private readonly int _requestedPort;
    private readonly Func<double> _clock;
    private readonly List<ProbeEvent> _events = [];
    private readonly Lock _lock = new();
    private WebApplication? _app;
    private ProbeEvent? _lastHello;
    private readonly Dictionary<ProbePage, ProbeEvent> _helloByPage = [];
    private IPAddress? _pageAddress;
    private double? _lastScrollY;
    private double _lastScrollAt = double.NegativeInfinity;

    public ProbeServer(int port, IPAddress? bind = null, Func<double>? clock = null)
    {
        _requestedPort = port;
        _bind = bind ?? IPAddress.Any;
        _clock = clock ?? ProbeClock.Now;
        Key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));
    }

    /// <summary>
    /// The secret of this run: the page URLs carry it (<c>?k=</c>) and the pages post their events with it. An event
    /// without it is refused, so neither another host on the subnet nor a web page in a browser on this PC can fake
    /// the page's "hello" and start the probe's touches and keys while the phone shows another app (security
    /// review, 2026-11-07).
    /// </summary>
    public string Key { get; }

    /// <summary>The address the first event with the key came from (the phone), or null before that.</summary>
    public IPAddress? PageAddress
    {
        get
        {
            lock (_lock)
            {
                return _pageAddress;
            }
        }
    }

    /// <summary>
    /// Whether an event with the key from <paramref name="from"/> is taken: the first one ties the run to its
    /// address, later ones must come from there. The key travels in a plain HTTP URL, so someone listening on an
    /// open Wi-Fi could learn it; from another host it is still of no use (security review, 2026-11-07).
    /// </summary>
    public bool Admit(IPAddress? from)
    {
        if (from is null)
        {
            return false;
        }

        if (from.IsIPv4MappedToIPv6)
        {
            from = from.MapToIPv4();
        }

        lock (_lock)
        {
            _pageAddress ??= from;
            return _pageAddress.Equals(from);
        }
    }

    /// <summary>The port actually listened on (useful when 0 was requested).</summary>
    public int Port { get; private set; }

    /// <summary>
    /// The hello to judge <paramref name="page"/> by: its own last hello if it ever sent one, else the last hello
    /// of any page (which then says that another page is open). A stray hello from another Safari tab does not
    /// hide the page's own.
    /// </summary>
    public ProbeEvent? HelloFor(ProbePage page)
    {
        lock (_lock)
        {
            return _helloByPage.TryGetValue(page, out var own) ? own : _lastHello;
        }
    }

    /// <summary>The last "hello" the page sent (it says whether the page is visible).</summary>
    public ProbeEvent? LastHello
    {
        get
        {
            lock (_lock)
            {
                return _lastHello;
            }
        }
    }

    /// <summary>The latest scroll position the scroll page (/scroll) reported, if any.</summary>
    public double? LastScrollY
    {
        get
        {
            lock (_lock)
            {
                return _lastScrollY;
            }
        }
    }

    /// <summary>When the scroll page last reported that it scrolled (<see cref="ProbeClock"/>).</summary>
    public double LastScrollAt
    {
        get
        {
            lock (_lock)
            {
                return _lastScrollAt;
            }
        }
    }

    /// <summary>The page HTML (embedded resource).</summary>
    public static string Page { get; } = LoadPage("page.html");

    /// <summary>The scroll page HTML (embedded resource), served at /scroll.</summary>
    public static string ScrollPage { get; } = LoadPage("scroll.html");

    /// <summary>The latency page HTML (embedded resource), served at /latency.</summary>
    public static string LatencyPage { get; } = LoadPage("latency.html");

    /// <summary>The keyboard page HTML (embedded resource), served at /keys.</summary>
    public static string KeysPage { get; } = LoadPage("keys.html");

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(_bind, _requestedPort);
            o.Limits.MaxRequestBodySize = MaxBodyBytes;
        });
        var app = builder.Build();

        // RequestDelegate handlers only: the Delegate overloads bind parameters by reflection, which the
        // trimmed single-file build does not allow.
        app.MapGet("/touch", ServePage(Page));

        // The touch page used to be at "/": an old tab or bookmark lands on /touch.
        app.MapGet("/", ctx =>
        {
            ctx.Response.Redirect("/touch" + ctx.Request.QueryString);
            return Task.CompletedTask;
        });
        app.MapGet("/scroll", ServePage(ScrollPage));
        app.MapGet("/latency", ServePage(LatencyPage));
        app.MapGet("/keys", ServePage(KeysPage));

        // The server clock (ProbeClock seconds) for the latency page's clock sync.
        app.MapGet("/t", ctx =>
        {
            var now = _clock();
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.ContentType = "text/plain";
            return ctx.Response.WriteAsync(now.ToString("R", CultureInfo.InvariantCulture), ctx.RequestAborted);
        });
        app.MapPost("/e", async ctx =>
        {
            if (!string.Equals(ctx.Request.Query["k"], Key, StringComparison.Ordinal) || !Admit(ctx.Connection.RemoteIpAddress))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            using var reader = new StreamReader(ctx.Request.Body);
            string body;
            try
            {
                body = await reader.ReadToEndAsync(ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (BadHttpRequestException ex)
            {
                ctx.Response.StatusCode = ex.StatusCode; // 413 above MaxBodyBytes
                return;
            }

            Add(body);
            ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        });

        try
        {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The port is taken (or the start was cancelled): release the host, which DisposeAsync would not see.
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _app = app;
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address.Replace("0.0.0.0", "localhost", StringComparison.Ordinal).Replace("[::]", "localhost", StringComparison.Ordinal)).Port;
    }

    /// <summary>Records one JSON event as if the page had POSTed it. Malformed JSON is ignored.</summary>
    public void Add(string json)
    {
        ProbeEvent? ev;
        try
        {
            ev = JsonSerializer.Deserialize(json, ProbeJson.Default.ProbeEvent);
        }
        catch (JsonException)
        {
            return;
        }

        if (ev is null)
        {
            return;
        }

        ev = ev with { T = _clock() };
        lock (_lock)
        {
            if (ev.Sy is { } sy)
            {
                _lastScrollY = sy;
            }

            if (ev.Type == "scroll")
            {
                _lastScrollAt = ev.T;
            }

            if (ev.Type == "hello")
            {
                _lastHello = ev;
                _helloByPage[ProbeReadiness.PageOf(ev)] = ev;
            }
            else if (_events.Count < MaxEvents)
            {
                _events.Add(ev);
            }
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _events.Clear();
        }
    }

    public IReadOnlyList<ProbeEvent> Snapshot()
    {
        lock (_lock)
        {
            return [.. _events];
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }

    /// <summary>The URLs to open in iPhone Safari for <paramref name="page"/> (one per LAN address; call after <see cref="StartAsync"/>).</summary>
    public IReadOnlyList<string> PageUrls(ProbePage page) =>
        [.. LanAddresses().Select(ip => string.Create(CultureInfo.InvariantCulture, $"http://{ip}:{Port}{ProbeReadiness.Path(page)}?k={Key}"))];

    /// <summary>
    /// IPv4 addresses of the network interfaces that are up (the default route's first), without the Wi-Fi Direct
    /// adapters (see <see cref="IsWifiDirect"/>).
    /// </summary>
    public static IReadOnlyList<string> LanAddresses()
    {
        var withGateway = new List<string>();
        var others = new List<string>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || IsWifiDirect(nic.Description))
            {
                continue;
            }

            var props = nic.GetIPProperties();
            var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            foreach (var a in props.UnicastAddresses)
            {
                if (a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                {
                    (hasGateway ? withGateway : others).Add(a.Address.ToString());
                }
            }
        }

        return [.. withGateway, .. others];
    }

    /// <summary>
    /// A Windows Wi-Fi Direct adapter ("Microsoft Wi-Fi Direct Virtual Adapter"). Windows brings one up with the
    /// sharing address 192.168.137.1 when a program such as a Miracast receiver (AirServer) starts a Wi-Fi Direct
    /// group; the iPhone on the LAN cannot reach it. The mobile hotspot uses the same kind of adapter, so a phone
    /// joined to the PC's hotspot gets no URL either.
    /// </summary>
    public static bool IsWifiDirect(string? description) =>
        description?.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ?? false;

    private static RequestDelegate ServePage(string html) => ctx =>
    {
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.WriteAsync(html, ctx.RequestAborted);
    };

    private static string LoadPage(string name)
    {
        using var stream = typeof(ProbeServer).Assembly.GetManifestResourceStream("IceScreenTouch.Probe." + name)
            ?? throw new InvalidOperationException("embedded probe page is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
