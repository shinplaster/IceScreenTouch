using System.Globalization;
using IceScreenTouch.Ble;
using IceScreenTouch.Core;
using IceScreenTouch.Probe;

// Console tool to verify the BLE HID path on a real iPhone (C# port of poc/poc2-ble-hid).
//   check                     adapter capabilities only; nothing is advertised (default)
//   selftest [seconds]        publish + advertise the HID service, wait, exit (no phone needed)
//   run [--port N]            interactive console with the PoC 2 commands and the probe page
//   probe [--port N] [--timeout S] [--rotation r0] [--save FILE]
//                             wait for the iPhone and the probe page, run the probe once, exit 0 on PASS
//   scroll [--port N] [--timeout S] [--trials N] [--step PERCENT] [--momentum 0,25,50,100]
//                             wait for the iPhone and the scroll page (/scroll), compare the wheel-swipe endings
//   conn [--request throughput|balanced|power] [--hold S]
//                             connection parameters (interval, PHY) of the connected BLE devices; publishes
//                             nothing, so it works next to the app (Windows 11). --request asks Windows for other
//                             parameters and keeps the request for S seconds (default 300), printing changes
//   keys [--port N] [--timeout S] [--layout us|jis]
//                             wait for the iPhone and the keyboard page (/keys) with the cursor in its box, type
//                             into it and check what arrived; exit 0 on PASS
//   --no-keyboard             (selftest / run / probe / scroll) publish the report map without the keyboard
//                             (216 bytes, before 2026-11-06); --no-home-button: without the consumer keys either
//                             (191 bytes, PoC 2). The device must pair again after a change.
var mode = args.Length > 0 && !args[0].StartsWith('-') ? args[0].ToLowerInvariant() : "check";
Options options;
double selfTestSeconds = 3.0;
try
{
    options = ParseOptions(args.Skip(mode == "check" && (args.Length == 0 || args[0].StartsWith('-')) ? 0 : 1).ToArray());
    if (mode == "selftest" && options.Positional is { } s)
    {
        selfTestSeconds = double.Parse(s, CultureInfo.InvariantCulture);
    }
}
catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException or IndexOutOfRangeException)
{
    // A wrong argument (a missing or non-numeric value) gets the usage and 64, as in the Bridge tool.
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return Usage();
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return mode switch
    {
        "check" => await CheckAsync(),
        "conn" => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? await ConnectionReport.RunAsync(options.ConnectionRequest, options.HoldSeconds, cts.Token)
            : NeedsWindows11(),
        "selftest" => await SelfTestAsync(selfTestSeconds, options, cts.Token),
        "run" => await RunAsync(options, "run", cts.Token),
        "probe" or "scroll" or "keys" => await RunAsync(options, mode, cts.Token),
        _ => Usage(),
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine("cancelled");
    return 130;
}
catch (BleException ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Console.Error.WriteLine("Is another process (PoC 2 / PoC 3) publishing the HID service?");
    return 2;
}

static int NeedsWindows11()
{
    Console.Error.WriteLine("ERROR: the connection parameters need Windows 11");
    return 2;
}

static int Usage()
{
    Console.Error.WriteLine("usage: IceScreenTouch.Probe [check | selftest [seconds] | run [--port N] | probe [--port N] [--timeout S] [--rotation r0] [--save FILE] | scroll [--port N] [--timeout S] [--trials N] [--step PERCENT] [--momentum 0,25,50,100] | keys [--port N] [--timeout S] [--layout us|jis]] [--no-keyboard] [--no-home-button]");
    return 64;
}

static async Task<int> CheckAsync()
{
    var info = await BleAdapterInfo.GetDefaultAsync();
    Console.WriteLine(info.Describe());
    try
    {
        var scope = IceScreenTouch.Platform.Windows.FirewallRules.InboundTcp(Environment.ProcessPath!, 8000);
        Console.WriteLine($"firewall: inbound TCP 8000 for {Environment.ProcessPath} -> {scope} (Limited = local subnet only; fix with allow_firewall.ps1)");
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
    {
        Console.WriteLine($"firewall: cannot read the rules ({ex.Message})");
    }

    return !info.Present ? 2 : info.CanBeHidPeripheral ? 0 : 1;
}

static async Task<HidPeripheral?> StartPeripheralAsync(CancellationToken ct, bool homeButton = true, bool keyboard = true)
{
    var info = await BleAdapterInfo.GetDefaultAsync(ct);
    Console.WriteLine(info.Describe());
    if (!info.CanBeHidPeripheral)
    {
        Console.Error.WriteLine("ERROR: no Bluetooth LE adapter with peripheral role support");
        return null;
    }

    var hid = await HidPeripheral.CreateAsync(homeButton, homeButton && keyboard, Log, ct);
    hid.StartAdvertising();
    await Task.Delay(1000, ct);
    foreach (var s in hid.GetServiceStates())
    {
        Console.WriteLine($"service {s.Name} ({s.Uuid}) -> {s.Status}");
    }

    Console.WriteLine($"report map: {HidReports.ReportMapFor(hid.ConsumerControl, hid.Keyboard).Length} bytes{(hid.HasHomeButton ? " (with the home button)" : string.Empty)}{(hid.Keyboard ? " (with the keyboard)" : string.Empty)}");
    return hid;
}

static async Task<int> SelfTestAsync(double seconds, Options options, CancellationToken ct)
{
    var hid = await StartPeripheralAsync(ct, options.HomeButton, options.Keyboard);
    if (hid is null)
    {
        return 2;
    }

    await using (hid)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds), ct);
        var ok = hid.GetServiceStates().All(s => s.IsAdvertising);
        Console.WriteLine(ok ? "SELFTEST OK" : "SELFTEST FAILED");
        return ok ? 0 : 1;
    }
}

static async Task<int> RunAsync(Options options, string mode, CancellationToken ct)
{
    var hid = await StartPeripheralAsync(ct, options.HomeButton, options.Keyboard);
    if (hid is null)
    {
        return 2;
    }

    await using (hid)
    {
        hid.SubscriptionChanged += (_, _) => Log(
            $"subscription changed: mouse={hid.SubscribedCount(HidConstants.MouseReportId)} touch={hid.SubscribedCount(HidConstants.TouchReportId)} consumer={hid.SubscribedCount(HidConstants.ConsumerReportId)} keyboard={hid.SubscribedCount(HidConstants.KeyboardReportId)}");

        await using var web = new ProbeServer(options.Port);
        var webOk = true;
        try
        {
            await web.StartAsync(ct);
            // The URLs carry the run's key (?k=): the pages post their events with it, and events without it are refused.
            foreach (var url in web.PageUrls(ProbePage.Touch))
            {
                Console.WriteLine($"probe page: open {url} in iPhone Safari (/scroll, /latency, /keys with the same ?k= for the other probes)");
            }
        }
        catch (IOException ex)
        {
            webOk = false;
            Console.WriteLine($"warning: probe web server not started: {ex.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("On the iPhone: Settings > Bluetooth > pair with this PC, and for the touch path");
        Console.WriteLine("enable Settings > Accessibility > Zoom. For the mouse path enable AssistiveTouch.");

        if (mode == "probe")
        {
            try
            {
                return webOk ? await ProbeOnceAsync(hid, web, options, ct) : 2;
            }
            catch (ProbePageLeftException ex)
            {
                // A failed run (1), not a cancel (130): the page left the front (review, 2026-11-07).
                Console.WriteLine($"TOUCH PROBE FAILED: {ex.Message}");
                return 1;
            }
        }

        if (mode == "scroll")
        {
            try
            {
                return webOk ? await ScrollOnceAsync(hid, web, options, ct) : 2;
            }
            catch (ProbePageLeftException ex)
            {
                Console.WriteLine($"SCROLL PROBE FAILED: {ex.Message}");
                return 1;
            }
        }

        if (mode == "keys")
        {
            try
            {
                return webOk ? await KeysOnceAsync(hid, web, options, ct) : 2;
            }
            catch (ProbePageLeftException ex)
            {
                // A failed run (1), not a cancel (130): the page left the front.
                Console.WriteLine($"KEYBOARD PROBE FAILED: {ex.Message}");
                return 1;
            }
        }

        Console.WriteLine("Waiting for the iPhone to subscribe; type 'help' for commands.");
        Console.WriteLine();
        var commands = new Commands(hid, webOk ? web : null, Console.Out);
        while (!ct.IsCancellationRequested)
        {
            var line = await Task.Run(Console.ReadLine, ct);
            if (line is null)
            {
                break;
            }

            try
            {
                if (!await commands.RunAsync(line, ct))
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException)
            {
                // Keep the prompt alive on bad input.
                Console.WriteLine($"error: {ex.Message}");
            }
            catch (ProbePageLeftException ex)
            {
                // The page left the front during a probe: that probe failed, the session goes on.
                Console.WriteLine($"stopped: {ex.Message}");
            }
        }

        return 0;
    }
}

static async Task<int> ProbeOnceAsync(HidPeripheral hid, ProbeServer web, Options options, CancellationToken ct)
{
    Console.WriteLine("Waiting for the iPhone to subscribe and for the probe page to be open in the foreground...");
    if (!await WaitReadyAsync(hid, web, ProbePage.Touch, options, ct))
    {
        return 3;
    }

    Console.WriteLine($"ready: touch subscribed, page open ({web.LastHello!.Vw}x{web.LastHello.Vh} viewport, {web.LastHello.Sw}x{web.LastHello.Sh} screen). Starting in 3 s - keep the page in front.");
    await Task.Delay(3000, ct);
    var result = await new ProbeRunner(new TouchPlayer(new PageGuardSink(hid, web, ProbePage.Touch, Console.Out)), web, Console.Out).RunAsync(options.Rotation, options.SavePath, ct);
    Console.WriteLine();
    Commands.PrintStatus(hid, Console.Out);
    return result is { Passed: true } ? 0 : 1;
}

static async Task<int> ScrollOnceAsync(HidPeripheral hid, ProbeServer web, Options options, CancellationToken ct)
{
    Console.WriteLine("Open /scroll (not /) in iPhone Safari. Waiting for the iPhone to subscribe and for the scroll page to be in front...");
    if (!await WaitReadyAsync(hid, web, ProbePage.Scroll, options, ct))
    {
        return 3;
    }

    Console.WriteLine("ready. Starting in 3 s - keep the page in front and do not touch the phone.");
    await Task.Delay(3000, ct);
    using var timer = IceScreenTouch.Platform.Windows.ProcessSetup.HighResolutionTimer(); // 4 ms ticks
    await new ScrollProbe(new PageGuardSink(hid, web, ProbePage.Scroll, Console.Out), web, Console.Out).RunAsync(options.Trials, options.StepPercent / 100.0, options.Momentums, ct);
    return 0;
}

static async Task<int> KeysOnceAsync(HidPeripheral hid, ProbeServer web, Options options, CancellationToken ct)
{
    if (!hid.Keyboard)
    {
        Console.Error.WriteLine("ERROR: the keyboard is not published (--no-keyboard)");
        return 2;
    }

    Console.WriteLine("Open /keys (not /) in iPhone Safari and tap its box. Waiting for the iPhone and the page...");
    if (!await WaitReadyAsync(hid, web, ProbePage.Keys, options, ct))
    {
        return 3;
    }

    Console.WriteLine($"ready. Typing in 3 s for a device set to {options.Layout} - keep the page in front and do not touch the phone.");
    await Task.Delay(3000, ct);
    var result = await new KeyboardProbe(hid, web, Console.Out).RunAsync(options.Layout, ct);
    return result.Passed ? 0 : 1;
}

static async Task<bool> WaitReadyAsync(HidPeripheral hid, ProbeServer web, ProbePage page, Options options, CancellationToken ct)
{
    var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(options.TimeoutSeconds);
    var lastHint = DateTime.UtcNow;
    ProbeMissing missing;
    while ((missing = ProbeReadiness.Check(page, hid.IsSubscribed, web.HelloFor(page), ProbeClock.Now())) != ProbeMissing.None)
    {
        if (DateTime.UtcNow > deadline)
        {
            Console.WriteLine("TIMEOUT: " + ProbeReadiness.Describe(missing, page));
            return false;
        }

        if (DateTime.UtcNow - lastHint > TimeSpan.FromSeconds(15))
        {
            Console.WriteLine("  still waiting: " + ProbeReadiness.Describe(missing, page));
            lastHint = DateTime.UtcNow;
        }

        await Task.Delay(250, ct);
    }

    return true;
}

static void Log(string message) =>
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

static Options ParseOptions(string[] rest)
{
    var o = new Options();
    for (var i = 0; i < rest.Length; i++)
    {
        string Next() => i + 1 < rest.Length ? rest[++i] : throw new ArgumentException($"{rest[i]} needs a value");
        switch (rest[i])
        {
            case "--port":
                o = o with { Port = int.Parse(Next(), CultureInfo.InvariantCulture) };
                break;
            case "--timeout":
                o = o with { TimeoutSeconds = double.Parse(Next(), CultureInfo.InvariantCulture) };
                break;
            case "--rotation":
                o = o with { Rotation = Next() };
                break;
            case "--momentum":
                o = o with { Momentums = [.. Next().Split(',').Select(v => double.Parse(v, CultureInfo.InvariantCulture) / 100.0)] };
                break;
            case "--trials":
                o = o with { Trials = int.Parse(Next(), CultureInfo.InvariantCulture) };
                break;
            case "--step":
                o = o with { StepPercent = double.Parse(Next(), CultureInfo.InvariantCulture) };
                break;
            case "--no-home-button":
                o = o with { HomeButton = false };
                break;
            case "--layout":
                o = o with { Layout = string.Equals(Next(), "jis", StringComparison.OrdinalIgnoreCase) ? HidKeyboardLayout.Jis : HidKeyboardLayout.Us };
                break;
            case "--request":
                o = o with { ConnectionRequest = Next().ToLowerInvariant() };
                break;
            case "--hold":
                o = o with { HoldSeconds = int.Parse(Next(), CultureInfo.InvariantCulture) };
                break;
            case "--no-keyboard":
                o = o with { Keyboard = false };
                break;
            case "--save":
                o = o with { SavePath = Next() };
                break;
            default:
                o = o with { Positional = rest[i] };
                break;
        }
    }

    return o;
}

internal sealed record Options
{
    public int Port { get; init; } = 8000;

    public double TimeoutSeconds { get; init; } = 600;

    public string Rotation { get; init; } = "r0";

    public string? SavePath { get; init; }

    public int Trials { get; init; } = 3;

    public double StepPercent { get; init; } = 10; // larger than the app default, so the measurement stands clear of iOS's touch slop

    public IReadOnlyList<double> Momentums { get; init; } = [];

    public string? Positional { get; init; }

    // --no-home-button: publish the PoC 2 report map without the consumer keys (devices must pair again).
    public bool HomeButton { get; init; } = true;

    // --no-keyboard: publish the report map without the keyboard (report ID 4), as before 2026-11-06.
    public bool Keyboard { get; init; } = true;

    // --layout us|jis: the device's hardware keyboard layout (keys mode).
    public HidKeyboardLayout Layout { get; init; } = HidKeyboardLayout.Us;

    // conn --request throughput|balanced|power, --hold S: ask Windows for other connection parameters for S seconds.
    public string? ConnectionRequest { get; init; }

    public int HoldSeconds { get; init; } = 300;
}
