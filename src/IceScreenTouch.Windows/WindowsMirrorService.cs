using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Ble;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using IceScreenTouch.Video;

namespace IceScreenTouch.Windows;

/// <summary>
/// The session on Windows: BLE HID peripheral, low-level mouse hook, UxPlay (or a running receiver) and the
/// bridge loop. Same wiring as the M3 console (tools/IceScreenTouch.Bridge), but failures become status instead
/// of exit codes, and it can be restarted with new settings.
/// </summary>
internal sealed class WindowsMirrorService(AppLog log) : IMirrorService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Session? _session;

    public event EventHandler<Rotation>? LandscapeRotationChanged;

    public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
            var session = new Session(settings, log, r => LandscapeRotationChanged?.Invoke(this, r));
            _session = session;
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public MirrorStatus GetStatus() => _session?.GetStatus() ?? MirrorStatus.Idle;

    /// <summary>What the probe borrows from the running session, or null when stopped.</summary>
    internal ProbeTarget? GetProbeTarget() => _session?.ProbeTarget;

    /// <summary>The uxplay.exe this app started (0 = none): not a receiver "running by itself" to follow.</summary>
    internal int OwnUxPlayProcessId => _session?.UxPlayProcessId ?? 0;

    public void Home() => _session?.Post(new BridgeCommand(BridgeCommandKind.Home));

    public void ToggleRotation() => _session?.Post(new BridgeCommand(BridgeCommandKind.ToggleRotation));

    public void ChooseTarget(string? deviceId) => _session?.ChooseTarget(deviceId);

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private async Task StopCoreAsync()
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>The session's peripheral (null without Bluetooth), bridge, settings, and a token cancelled when it stops.</summary>
    internal sealed record ProbeTarget(HidPeripheral? Hid, MirrorBridge? Bridge, AppSettings Settings, CancellationToken Stopping);

    /// <summary>One start..stop of everything.</summary>
    private sealed class Session(AppSettings settings, AppLog log, Action<Rotation> rotationChanged) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Channel<MouseInputEvent> _input = Channel.CreateUnbounded<MouseInputEvent>(new UnboundedChannelOptions { SingleReader = true });
        private volatile BleState _ble = BleState.Starting;
        private volatile string? _bleDetail;
        private volatile string? _receiverProblemDetail;
        private ReceiverState? _receiverProblem;
        private HidPeripheral? _hid;
        private IVideoSource? _video;
        private UxPlayVideoSource? _uxplay;

        public int UxPlayProcessId => _uxplay?.ProcessId ?? 0;
        private WindowVideoSource? _attached;
        private MouseHook? _hook;
        private KeyboardInput? _keyboard;
        private volatile MirrorBridge? _bridge;
        private Task? _loop;
        private Task? _targetLoop;
        private CancellationToken _stopping = new(canceled: true);
        private volatile string? _chosenTarget = settings.TargetDevice;
        private volatile TargetChoice _target = TargetChoice.None;
        private volatile IReadOnlyList<HidClient> _devices = [];
        private ScreenPreset? _screen;
        private readonly SemaphoreSlim _targetChanged = new(0);
        private readonly Dictionary<IPAddress, (string? Host, long At)> _hosts = [];

        public ProbeTarget ProbeTarget => new(_hid, _bridge, settings, _stopping);

        public void Post(BridgeCommand command) => _bridge?.Post(command);

        public void ChooseTarget(string? deviceId)
        {
            _chosenTarget = deviceId;
            Wake();
        }

        private void Wake()
        {
            if (_targetChanged.CurrentCount == 0)
            {
                _targetChanged.Release();
            }
        }

        // Picks the one device that gets the input, once a second and whenever the devices or the choice change.
        private async Task FollowTargetAsync(HidPeripheral hid, CancellationToken ct)
        {
            TargetChoice? last = null;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var devices = hid.Clients().OrderBy(c => c.Order).ToList();
                    var peer = devices.Count < 2 ? MirrorPeer.Unknown : await MirroredPeerAsync(ct).ConfigureAwait(false);
                    var choice = TouchTargetSelector.Select(devices, _chosenTarget, peer);
                    hid.SetTarget(choice.Client?.Id);
                    _devices = devices;
                    _target = choice;
                    FollowScreen(choice.Client);
                    if (choice != last)
                    {
                        last = choice;
                        if (choice.Client is { } c)
                        {
                            var known = peer.DeviceName is { } n ? $"\"{n}\"" : peer.HostNames.Count > 0 ? string.Join(", ", peer.HostNames) : "nothing";
                            log.Write($"[hid ] touches and keys go to {c.Name ?? LogPrivacy.LogId(c.Id)} ({choice.Reason}; {devices.Count} connected; the receiver shows {known})");
                        }
                    }

                    using var tick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    tick.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        await _targetChanged.WaitAsync(tick.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // The second is over.
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or SocketException or NetworkInformationException)
                {
                    log.Write($"[hid ] choosing the target device: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
        }

        // The picture is looked for with the shape of the device that gets the input (automatic screen shape: from
        // its model number). A receiver that reports the picture size (UxPlay started by the app) does not need it.
        private void FollowScreen(HidClient? target)
        {
            var screen = settings.ScreenFor(target?.Model);
            if (screen == _screen)
            {
                return;
            }

            _screen = screen;
            if (_attached is { } attached)
            {
                attached.PortraitAspect = screen.PortraitAspect;
            }

            log.Write($"[video] screen shape {screen.Id} {screen.Width}x{screen.Height}{(settings.ScreenPreset.IsAuto ? $" (automatic: {target?.Model ?? "model unknown"})" : string.Empty)}");
        }

        // What the receiver tells about the device it shows: UxPlay prints the device name; for a receiver that
        // does not, the host names (mDNS) of the machines connected to it, leaving out this PC's own addresses.
        private async Task<MirrorPeer> MirroredPeerAsync(CancellationToken ct)
        {
            if (_uxplay is { } uxplay)
            {
                return new MirrorPeer(uxplay.DeviceName, []);
            }

            if (_attached is not { ProcessId: > 0 } attached)
            {
                return MirrorPeer.Unknown;
            }

            var unicast = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .ToList();
            var own = unicast.Select(a => a.Address).ToHashSet();
            var hosts = new List<string>();

            // Only machines on this PC's own subnets: the phone is one of them. The receiver also talks to servers
            // on the internet (updates, licences), which must not get our queries nor be able to name the device
            // the input goes to (security review, 2026-11-07).
            foreach (var address in TcpPeers.RemoteAddresses(attached.ProcessId).Where(a => !own.Contains(a) && unicast.Any(u => Subnet.Contains(u.Address, u.PrefixLength, a))))
            {
                if (await HostNameAsync(address, ct).ConfigureAwait(false) is { } host && !hosts.Contains(host))
                {
                    hosts.Add(host);
                }
            }

            return new MirrorPeer(null, hosts);
        }

        // Cached: an answer for a minute, no answer for 15 s. An iPhone connects from a temporary IPv6 address that
        // it does not answer for; its IPv4 address (same hardware address in the neighbor table) does.
        private async Task<string?> HostNameAsync(IPAddress address, CancellationToken ct)
        {
            var now = Environment.TickCount64;
            if (_hosts.TryGetValue(address, out var cached) && now - cached.At < (cached.Host is null ? 15_000 : 60_000))
            {
                return cached.Host;
            }

            var ask = address.AddressFamily == AddressFamily.InterNetwork
                ? address
                : NeighborTable.SameDevice(address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? address;
            // A device that answered before keeps its name when a later query goes unanswered (Wi-Fi drops one now
            // and then; the choice flipped to "latest" and back, 2026-11-06).
            var host = await MdnsReverseLookup.QueryAsync(ask, TimeSpan.FromMilliseconds(600), ct).ConfigureAwait(false) ?? cached.Host;
            _hosts[address] = (host, Environment.TickCount64);
            if (host is not null && cached.Host != host)
            {
                log.Write($"[video] {settings.AttachProcessName} is connected to {address} ({host})");
            }

            return host;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            log.Write("[app ] starting the session");
            _stopping = _stop.Token;
            var sink = await StartBluetoothAsync(cancellationToken).ConfigureAwait(false);
            _video = StartReceiver();
            if (_hid is { } targetHid)
            {
                targetHid.SubscriptionChanged += (_, _) => Wake();
                _targetLoop = Task.Run(() => FollowTargetAsync(targetHid, _stop.Token), CancellationToken.None);
            }

            try
            {
                _hook = new MouseHook(e => _input.Writer.TryWrite(e));
                _hook.Start();
            }
            catch (Win32Exception ex)
            {
                _hook?.Dispose();
                _hook = null;
                log.Write($"[hook] cannot install the mouse hook ({ex.Message}); clicks will not reach the iPhone");
            }

            // Keys go to the device while the mirror window has the focus (and the probe does not own the phone).
            var focusDesktop = new Win32Desktop();
            _keyboard = KeyboardInput.Start(
                sink as IKeyboardSink,
                () => _bridge is { } b && b.Status is { Suspended: false } st && focusDesktop.IsForeground(st.Window),
                settings.KeyboardInput,
                HidKeyboardLayout.Us, // iOS treats the HID keyboard as US whatever its own layout setting says
                log.Write,
                settings.VerboseLog);
            log.Write(_keyboard.Forwarding
                ? "[keys] keys typed in the mirror window go to the device (as a US keyboard)"
                : $"[keys] keys stay with Windows ({(settings.KeyboardInput ? "no HID keyboard" : "turned off in the settings")})");
            var keyboard = _keyboard;

            var engine = new TouchGestureEngine(sink, timing: settings.Timing)
            {
                LandscapeRotation = settings.Rotation,
                Wheel = settings.Wheel,
            };
            var bridge = new MirrorBridge(
                engine,
                _video,
                new Win32Desktop(keyboard.KeyDown, keyboard.ShortcutPresses),
                _input.Reader,
                new BridgeOptions
                {
                    FollowVideoSize = settings.FollowVideoSize,
                    FallbackVideoSize = settings.ScreenFor(null).Size,
                    RightButtonLongPress = settings.RightButtonLongPress,
                    WheelScroll = settings.WheelScroll,
                    Verbose = settings.VerboseLog,
                },
                log: log.Write);
            bridge.LandscapeRotationChanged += (_, r) =>
            {
                _uxplay?.Mark(string.Create(CultureInfo.InvariantCulture, $"bridge landscape_rot={(int)r}"));
                rotationChanged(r);
            };
            _bridge = bridge;
            _loop = Task.Run(() => bridge.RunAsync(_stop.Token), CancellationToken.None);
            _ = _loop.ContinueWith(
                t =>
                {
                    _receiverProblem = ReceiverState.Error;
                    _receiverProblemDetail = Loc.Format(Strings.FormatBridgeStopped, t.Exception?.GetBaseException().Message);
                    log.Write($"[app ] the bridge loop failed: {t.Exception?.GetBaseException()}");
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            log.Write("[app ] running. Left drag = touch, Ctrl + left drag = pinch, right button = long press, wheel = scroll, Home = home, Ctrl+R = rotation 90 <-> 270, other keys = typed on the device");
        }

        public MirrorStatus GetStatus()
        {
            var bridge = _bridge;
            var bridgeStatus = bridge?.Status ?? BridgeStatus.Initial;
            var ble = _ble;
            if (ble is BleState.Advertising or BleState.Connected && _hid is { } hid)
            {
                ble = hid.IsSubscribed ? BleState.Connected : BleState.Advertising;
            }

            var (receiver, detail) = ReceiverStatus(bridgeStatus);
            var uxplay = _uxplay;
            string? device = null;
            if (uxplay?.DeviceName is { } name)
            {
                device = uxplay.DeviceModel is { } model ? $"{name}, {model}" : name;
            }

            return new MirrorStatus
            {
                Running = true,
                Ble = ble,
                BleDetail = _bleDetail,
                Receiver = receiver,
                ReceiverName = settings.Receiver == ReceiverMode.StartUxPlay ? "UxPlay" : settings.AttachProcessName,
                ReceiverDetail = detail,
                Device = device,
                SizeSource = uxplay?.SizeSource,
                Bridge = bridgeStatus,
                Devices = _devices,
                Target = _target,
            };
        }

        public async ValueTask DisposeAsync()
        {
            log.Write("[app ] stopping the session");
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_targetLoop is { } targetLoop)
            {
                await Step("the input target loop", () => targetLoop).ConfigureAwait(false);
            }

            if (_loop is { } loop)
            {
                try
                {
                    await loop.ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Already logged by the continuation; shutting down regardless.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }

            if (_bridge is { } bridge)
            {
                // Never leave a finger down on the phone.
                await Step("lifting the finger", async () =>
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await bridge.Engine.AbortAsync(timeout.Token).ConfigureAwait(false);
                }).ConfigureAwait(false);

                var s = bridge.Engine.Stats;
                var summary = string.Create(
                    CultureInfo.InvariantCulture,
                    $"[summary] touches={s.Gestures} long={s.LongPresses} scrolls={s.Scrolls} dropped={s.Dropped} max_send={s.MaxSend.TotalMilliseconds:0}ms");
                if (_hid is { } h)
                {
                    summary += string.Create(CultureInfo.InvariantCulture, $" notify sent={h.Stats.Sent} skipped={h.Stats.Skipped} failed={h.Stats.Failed} busy={h.Stats.Busy}");
                }

                log.Write(summary);
            }

            // Each step on its own: one that fails must not leave the hooks, the receiver or the HID service behind
            // (only one process can publish the HID service, so a leftover one blocks the next start).
            if (_hook is { } hook)
            {
                await Step("the mouse hook", () => { hook.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
            }

            if (_keyboard is { } keyboard)
            {
                await Step("the keyboard", () => keyboard.DisposeAsync().AsTask()).ConfigureAwait(false); // releases every key on the device
            }

            if (_attached is { } attached)
            {
                await Step("the receiver window", () => { attached.Dispose(); return Task.CompletedTask; }).ConfigureAwait(false);
            }

            if (_uxplay is { } uxplay)
            {
                await Step("UxPlay", () => uxplay.DisposeAsync().AsTask()).ConfigureAwait(false);
            }

            if (_hid is { } hid)
            {
                await Step("the HID service", () => hid.DisposeAsync().AsTask()).ConfigureAwait(false);
            }

            _stop.Dispose();
            _targetChanged.Dispose();
        }

        // One step of stopping: a failure is logged and the next step still runs.
        private async Task Step(string what, Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
#pragma warning disable CA1031 // Stopping goes on whatever failed; the log says what.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                log.Write($"[app ] stopping {what} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private async Task<ITouchSink> StartBluetoothAsync(CancellationToken cancellationToken)
        {
            if (settings.DryRun)
            {
                _ble = BleState.DryRun;
                log.Write("[hid ] dry run: nothing is sent over Bluetooth");
                return new DryRunSink(settings.VerboseLog ? log.Write : null);
            }

            try
            {
                var info = await BleAdapterInfo.GetDefaultAsync(cancellationToken).ConfigureAwait(false);
                if (!info.CanBeHidPeripheral)
                {
                    _bleDetail = info.Describe();
                    _ble = BleState.Unsupported;
                    log.Write($"[hid ] no Bluetooth LE adapter with peripheral role support ({info.Describe()})");
                    return OfflineSink.Instance;
                }

                var hid = await HidPeripheral.CreateAsync(log.Write, cancellationToken).ConfigureAwait(false);
                _hid = hid;
                hid.StartAdvertising();

                // Right after StartAdvertising, Windows reports Aborted once and Started ~80 ms later (seen in M2).
                var states = hid.GetServiceStates();
                for (var i = 0; i < 30 && !states.All(s => s.IsAdvertising); i++)
                {
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    states = hid.GetServiceStates();
                }

                if (!states.All(s => s.IsAdvertising))
                {
                    _bleDetail = string.Join(", ", states.Select(s => $"{s.Name}={s.Status}"));
                    _ble = BleState.Error;
                    log.Write($"[hid ] the GATT services are not advertising ({_bleDetail}); another process may publish the HID service");
                    _hid = null;
                    await hid.DisposeAsync().ConfigureAwait(false);
                    return OfflineSink.Instance;
                }

                hid.SubscriptionChanged += (_, _) => log.Write($"[hid ] touch subscribers: {hid.SubscribedCount(HidConstants.TouchReportId)}");
                _ble = BleState.Advertising;
                log.Write("[hid ] advertising as a BLE HID peripheral");
                return hid;
            }
            catch (Exception ex) when (ex is BleException or COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                // A Bluetooth stack in trouble may throw from WinRT directly: show it as a Bluetooth error and let
                // the rest of the session start, instead of a session stuck "starting" (code review, 2026-11-07).
                _bleDetail = ex.Message;
                _ble = BleState.Error;
                log.Write($"[hid ] {ex.Message}");
                if (_hid is { } hid)
                {
                    _hid = null;
                    await hid.DisposeAsync().ConfigureAwait(false);
                }

                return OfflineSink.Instance;
            }
        }

        private IVideoSource StartReceiver()
        {
            if (settings.Receiver == ReceiverMode.AttachToProcess)
            {
                // AirServer shapes its own window after a rotation; reshaping it too made it flip (an iPad, 2026-11-05).
                var fit = settings.DetectPicture && settings.FollowVideoSize && !ReceiverQuirks.ReshapesItsOwnWindow(settings.AttachProcessName);
                var toolbar = ReceiverQuirks.BottomToolbar(settings.AttachProcessName);
                log.Write($"[video] following the window of a running {settings.AttachProcessName}.exe (picture detection {(settings.DetectPicture ? "on" : "off")}, fit the window {(fit ? "on" : "off")}, screen {settings.ScreenPreset.Id} {settings.ScreenPreset.Width}x{settings.ScreenPreset.Height}{(toolbar > 0 ? $", bottom toolbar {toolbar} px" : string.Empty)})");
                _attached = new WindowVideoSource(settings.AttachProcessName, new WindowVideoSourceOptions { DetectPicture = settings.DetectPicture, FitWindow = fit, PortraitAspect = settings.ScreenFor(null).PortraitAspect, BottomToolbar = toolbar, TrustMeasuredPicture = ReceiverQuirks.UnevenMargins(settings.AttachProcessName), OnlyWhileConnected = ReceiverQuirks.ShowsStandbyScreen(settings.AttachProcessName) }, log.Write);
                return _attached;
            }

            var exe = settings.UxPlayPath
                ?? UxPlayOptions.FindLocalBuild(AppContext.BaseDirectory);
            if (exe is null || !File.Exists(exe))
            {
                _receiverProblem = ReceiverState.NotFound;
                _receiverProblemDetail = exe;
                log.Write($"[video] uxplay.exe not found ({exe ?? "no local build under poc/poc1-uxplay/UxPlay"}); set its path in the settings");
                return NoVideoSource.Instance;
            }

            try
            {
                // UxPlay's full output (RTSP headers, the phone's name, device ID, MAC, OS build) only with the verbose
                // log: the app log keeps what the app reads from it, and a log attached to a public issue should not
                // carry the phone's identifiers by default (security review, 2026-11-07).
                var logPath = settings.VerboseLog ? log.NewLogFile("uxplay") : null;
                _uxplay = UxPlayVideoSource.Start(
                    new UxPlayOptions
                    {
                        ExePath = exe,
                        Name = settings.AirPlayName,
                        MsysBin = settings.MsysBin ?? UxPlayOptions.DefaultMsysBin,
                        LogPath = logPath,
                    }.WithSink(settings.VideoSink),
                    log.Write,
                    settings.VerboseLog);
                log.Write($"[video] UxPlay started (pid {_uxplay.ProcessId}, {exe}); uxplay log: {logPath ?? "-"}");
                return _uxplay;
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                _receiverProblem = ReceiverState.Error;
                _receiverProblemDetail = ex.Message;
                log.Write($"[video] cannot start uxplay: {ex.Message}");
                return NoVideoSource.Instance;
            }
        }

        private (ReceiverState State, string? Detail) ReceiverStatus(BridgeStatus bridge)
        {
            if (_receiverProblem is { } problem)
            {
                return (problem, _receiverProblemDetail);
            }

            if (_uxplay is { IsRunning: false } exited)
            {
                return (ReceiverState.Exited, exited.ExitCode is { } code ? Loc.Format(Strings.FormatExitCode, code) : null);
            }

            if (_attached is { } attached && bridge.Window == 0 && attached.ProcessId == 0)
            {
                return (ReceiverState.NotFound, null);
            }

            return (bridge.Window != 0 ? ReceiverState.Showing : ReceiverState.Waiting, null);
        }
    }

    /// <summary>Used when Bluetooth is not available: never subscribed, so presses are counted as dropped.</summary>
    private sealed class OfflineSink : ITouchSink
    {
        public static readonly OfflineSink Instance = new();

        public bool IsSubscribed => false;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    /// <summary>Dry run: always "subscribed"; optionally logs each report.</summary>
    private sealed class DryRunSink(Action<string>? print) : ITouchSink
    {
        public bool IsSubscribed => true;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
        {
            if (print is not null)
            {
                var span = contacts.Span;
                var parts = new string[span.Length];
                for (var i = 0; i < span.Length; i++)
                {
                    var c = span[i];
                    // Without the position, as in the verbose log of real touches (it would spell out a passcode).
                    parts[i] = string.Create(CultureInfo.InvariantCulture, $"#{c.Id} {(c.Tip ? "down" : "up  ")}");
                }

                print("[dry ] " + (parts.Length == 0 ? "(no contacts)" : string.Join("  ", parts)));
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>No receiver: never a window.</summary>
    private sealed class NoVideoSource : IVideoSource
    {
        public static readonly NoVideoSource Instance = new();

        public string Name => "none";

        public SizeD? VideoSize => null;

        public nint FindWindow() => 0;
    }
}
