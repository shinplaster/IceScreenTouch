using System.Globalization;
using System.Threading.Channels;
using IceScreenTouch.Ble;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using IceScreenTouch.Video;

namespace IceScreenTouch.Bridge;

/// <summary>Wires the BLE peripheral, the mouse hook, the video source and the bridge loop (port of amain in PoC 3).</summary>
internal static class BridgeApp
{
    private const string ConsoleHelp = """
        console commands:
          rot [0|90|180|270]   show / set the rotation used while the picture is landscape (same as Ctrl+R)
          status               print the status line now
          mark <label>         write a labelled marker into the uxplay log (needs --uxplay-log or --collect)
          help | quit
        """;

    private static readonly Lock ConsoleLock = new();

    public static void Log(string message)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
    }

    public static async Task<int> RunAsync(BridgeSettings settings, CancellationToken ct)
    {
        // Sink: the BLE HID peripheral, or a recorder.
        HidPeripheral? hid = null;
        DryRunSink? dry = null;
        if (settings.DryRun)
        {
            dry = new DryRunSink(settings.Verbose ? Log : null);
            Log("HID: dry-run (nothing is sent over Bluetooth)");
        }
        else
        {
            hid = await StartPeripheralAsync(ct).ConfigureAwait(false);
            if (hid is null)
            {
                return 2;
            }
        }

        await using var hidScope = hid;
        ITouchSink sink = (ITouchSink?)hid ?? dry!;

        // Video source: start uxplay, or follow a running process.
        UxPlayVideoSource? uxplay = null;
        WindowVideoSource? attached = null;
        IVideoSource video;
        if (settings.Attach is { } attach)
        {
            attached = new WindowVideoSource(attach, new WindowVideoSourceOptions { BottomToolbar = ReceiverQuirks.BottomToolbar(attach), TrustMeasuredPicture = ReceiverQuirks.UnevenMargins(attach), OnlyWhileConnected = ReceiverQuirks.ShowsStandbyScreen(attach) }, Log);
            video = attached;
            Log($"UxPlay not started; following the window of a running {attach}.exe");
        }
        else
        {
            var exe = settings.UxPlayExe
                ?? UxPlayOptions.FindLocalBuild(AppContext.BaseDirectory);
            if (exe is null || !File.Exists(exe))
            {
                Console.Error.WriteLine($"ERROR: uxplay.exe not found ({exe ?? "no local build under poc/poc1-uxplay/UxPlay"}); build it first (poc/poc1-uxplay/README.md) or pass --uxplay-exe");
                return 2;
            }

            var logPath = settings.UxPlayLog ?? (settings.Collect
                ? Path.Combine(Environment.CurrentDirectory, "logs", $"uxplay-{DateTime.Now:yyyyMMdd-HHmmss}.log")
                : null);
            if (logPath is not null)
            {
                Log($"writing the uxplay log to {logPath}");
            }

            uxplay = UxPlayVideoSource.Start(
                new UxPlayOptions
                {
                    ExePath = exe,
                    Name = settings.Name,
                    ExtraArguments = settings.UxPlayArgs,
                    MsysBin = settings.MsysBin ?? UxPlayOptions.DefaultMsysBin,
                    LogPath = logPath,
                }.WithSink(settings.Sink),
                Log,
                settings.Verbose);
            video = uxplay;
            Log($"UxPlay started (pid {uxplay.ProcessId}, {Path.GetFileName(Path.GetDirectoryName(exe))}). On the iPhone: Control Center > Screen Mirroring > {settings.Name}");
        }

        await using var uxplayScope = uxplay;
        using var attachedScope = attached;

        // Button events from the hook thread to the bridge loop.
        var buttons = Channel.CreateUnbounded<MouseInputEvent>(new UnboundedChannelOptions { SingleReader = true });
        using var hook = new MouseHook(e => buttons.Writer.TryWrite(e));
        hook.Start();

        // Keys typed in the mirror window go to the device (a US hardware keyboard layout is assumed).
        MirrorBridge? bridgeRef = null;
        var focusDesktop = new Win32Desktop();
        await using var keyboard = KeyboardInput.Start(
            hid,
            () => bridgeRef is { } b && b.Status is { Suspended: false } st && focusDesktop.IsForeground(st.Window),
            forward: true,
            HidKeyboardLayout.Us,
            Log,
            settings.Verbose);

        var engine = new TouchGestureEngine(sink) { LandscapeRotation = settings.LandscapeRotation };
        var bridge = new MirrorBridge(
            engine,
            video,
            new Win32Desktop(keyboard.KeyDown, keyboard.ShortcutPresses),
            buttons.Reader,
            new BridgeOptions { FollowVideoSize = settings.Follow, Verbose = settings.Verbose },
            log: Log);
        bridgeRef = bridge;
        if (uxplay is not null)
        {
            bridge.LandscapeRotationChanged += (_, r) => uxplay.Mark(string.Create(CultureInfo.InvariantCulture, $"bridge landscape_rot={(int)r}"));
        }

        Log("Zoom must be ON on the iPhone (Settings > Accessibility > Zoom).");
        Log("Alt + click is never a touch. This console tool does not keep presses from receivers: one that moves its window on a drag will move it.");
        Log("Left drag = touch, Ctrl + left drag = pinch, right button = long press, wheel = scroll, Home = home, Ctrl+R = landscape rotation 90 <-> 270, other keys = typed on the device.");
        Log("Type `help` here for console commands. Ctrl+C to quit.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var bridgeTask = Task.Run(() => bridge.RunAsync(stop.Token), CancellationToken.None);
        var statusTask = StatusLoopAsync(bridge, uxplay, video, hid, stop.Token);
        var consoleTask = ConsoleLoopAsync(bridge, uxplay, hid, video, stop);

        var exitCode = 0;
        try
        {
            await Task.WhenAny(bridgeTask, consoleTask, Task.Delay(Timeout.Infinite, stop.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C or --duration.
        }

        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await bridgeTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"ERROR: the bridge loop failed: {ex}");
            exitCode = 1;
        }

        await IgnoreCancellation(statusTask).ConfigureAwait(false);
        await LiftAsync(engine).ConfigureAwait(false);

        var stats = engine.Stats;
        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"[summary] gestures={stats.Gestures} dropped={stats.Dropped} max_send={stats.MaxSend.TotalMilliseconds:0}ms");
        if (hid is not null)
        {
            summary += string.Create(CultureInfo.InvariantCulture, $" notify sent={hid.Stats.Sent} skipped={hid.Stats.Skipped} failed={hid.Stats.Failed} busy={hid.Stats.Busy}");
        }
        else if (dry is not null)
        {
            summary += string.Create(CultureInfo.InvariantCulture, $" dry-run reports={dry.Sent}");
        }

        Log(summary);
        return exitCode;
    }

    internal static string StatusLine(MirrorBridge bridge, UxPlayVideoSource? uxplay, IVideoSource video, HidPeripheral? hid)
    {
        var parts = new List<string>();
        if (uxplay is not null)
        {
            parts.Add($"uxplay={(uxplay.IsRunning ? "up" : "DOWN")}");
            parts.Add($"iphone={uxplay.DeviceModel ?? "-"}");
        }
        else
        {
            parts.Add($"source={video.Name}");
        }

        var size = video.VideoSize;
        parts.Add(size is { } s
            ? string.Create(CultureInfo.InvariantCulture, $"video={s.Width:0}x{s.Height:0}({uxplay?.SizeSource ?? "?"})")
            : "video=-");
        if ((bridge.Picture ?? default).IsLandscape || size is { IsLandscape: true })
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"landscape_rot={(int)bridge.Engine.LandscapeRotation}"));
        }

        parts.Add($"window={(bridge.Window != 0 ? "yes" : "no")}");
        parts.Add(hid is not null ? string.Create(CultureInfo.InvariantCulture, $"hid_subscribed={hid.SubscribedCount(HidConstants.TouchReportId)}") : "hid=dry-run");
        return string.Join("  ", parts);
    }

    private static async Task<HidPeripheral?> StartPeripheralAsync(CancellationToken ct)
    {
        var info = await BleAdapterInfo.GetDefaultAsync(ct).ConfigureAwait(false);
        if (!info.CanBeHidPeripheral)
        {
            Console.Error.WriteLine($"ERROR: no Bluetooth LE adapter with peripheral role support ({info.Describe()})");
            return null;
        }

        var hid = await HidPeripheral.CreateAsync(Log, ct).ConfigureAwait(false);
        hid.StartAdvertising();

        // Right after StartAdvertising, Windows reports Aborted once and Started ~80 ms later (seen in M2).
        await Task.Delay(1000, ct).ConfigureAwait(false);
        var states = hid.GetServiceStates();
        if (!states.All(s => s.IsAdvertising))
        {
            Console.Error.WriteLine("ERROR: the GATT services are not advertising (" +
                string.Join(", ", states.Select(s => $"{s.Name}={s.Status}")) +
                "). Another process (PoC 2 / PoC 3 / IceScreenTouch.Probe) may still publish the HID service; quit it first.");
            await hid.DisposeAsync().ConfigureAwait(false);
            return null;
        }

        hid.SubscriptionChanged += (_, _) => Log($"[hid ] touch subscribers: {hid.SubscribedCount(HidConstants.TouchReportId)}");
        Log("HID: advertising as a BLE HID peripheral (pair in Settings > Bluetooth if this PC is new to the iPhone)");
        return hid;
    }

    private static async Task StatusLoopAsync(MirrorBridge bridge, UxPlayVideoSource? uxplay, IVideoSource video, HidPeripheral? hid, CancellationToken ct)
    {
        var last = string.Empty;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            var line = StatusLine(bridge, uxplay, video, hid);
            if (line != last)
            {
                // Only when something other than the counters changed.
                Log("[status] " + line);
                last = line;
            }
        }
    }

    private static async Task ConsoleLoopAsync(MirrorBridge bridge, UxPlayVideoSource? uxplay, HidPeripheral? hid, IVideoSource video, CancellationTokenSource stop)
    {
        while (!stop.IsCancellationRequested)
        {
            // ReadLine cannot be cancelled; the thread simply ends with the process.
            var line = await Task.Run(Console.ReadLine).ConfigureAwait(false);
            if (line is null)
            {
                // No console input (e.g. started with --duration from a script): keep running.
                await Task.Delay(Timeout.Infinite, stop.Token).ConfigureAwait(false);
                return;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0].ToLowerInvariant())
            {
                case "quit" or "exit":
                    return;
                case "help":
                    Console.WriteLine(ConsoleHelp);
                    break;
                case "status":
                    Log("[status] " + StatusLine(bridge, uxplay, video, hid));
                    break;
                case "rot" when parts.Length == 1:
                    Log(string.Create(CultureInfo.InvariantCulture, $"[rot ] landscape rotation = {(int)bridge.Engine.LandscapeRotation} deg (choices: 0, 90, 180, 270)"));
                    break;
                case "rot":
                    try
                    {
                        var r = BridgeCommandLine.ParseRotation(parts[1]);
                        // Runs on the bridge loop, which logs it and marks the uxplay log.
                        bridge.Post(new BridgeCommand(BridgeCommandKind.SetRotation, r));
                    }
                    catch (ArgumentException ex)
                    {
                        Log(ex.Message);
                    }

                    break;
                case "mark":
                    var label = parts.Length > 1 ? string.Join(' ', parts[1..]) : "mark";
                    Log(uxplay?.Mark(label) == true ? $"[mark] {label}" : "no uxplay log is being written; start with --collect (or --uxplay-log FILE)");
                    break;
                default:
                    Log("unknown command; type `help`");
                    break;
            }
        }
    }

    private static async Task LiftAsync(TouchGestureEngine engine)
    {
        try
        {
            // Never leave a finger down on the phone.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await engine.AbortAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task IgnoreCancellation(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
