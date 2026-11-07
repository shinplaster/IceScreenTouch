using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;
using IceScreenTouch.Probe;

namespace IceScreenTouch.Windows;

/// <summary>
/// The probe inside the app: serves the Safari page, waits for the iPhone and the page, pauses the mouse bridge,
/// and plays the probe gestures through the session's own HID peripheral (only one process can publish the
/// HID service, so the probe cannot have a peripheral of its own here).
/// </summary>
internal sealed class WindowsProbeService(WindowsMirrorService mirror, AppLog log) : IProbeService
{
    /// <summary>The firewall rule the app creates for itself (the console probe tool has its own).</summary>
    public const string FirewallRuleName = "IceScreen Touch probe page (app)";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly TimeSpan SuspendTimeout = TimeSpan.FromSeconds(3);

    // The probe's check names (English, also in the log) and their text for the user.
    private static readonly Dictionary<string, string> CheckNames = new(StringComparer.Ordinal)
    {
        ["taps matched"] = nameof(Strings.CheckTaps),
        ["1:1 mapping"] = nameof(Strings.CheckMapping),
        ["orientation"] = nameof(Strings.CheckOrientation),
        ["swipe moves"] = nameof(Strings.CheckSwipe),
        ["swipe lift"] = nameof(Strings.CheckSwipeLift),
        ["pinch two fingers"] = nameof(Strings.CheckPinch),
        ["pinch lift"] = nameof(Strings.CheckPinchLift),
        [KeyboardProbe.ArriveCheck] = nameof(Strings.CheckKeysArrive),
        [KeyboardProbe.LettersCheck] = nameof(Strings.CheckLetters),
        [KeyboardProbe.SymbolsCheck] = nameof(Strings.CheckSymbols),
        [KeyboardProbe.EditingCheck] = nameof(Strings.CheckEditing),
        [KeyboardProbe.SelectAllCheck] = nameof(Strings.CheckSelectAll),
        [KeyboardProbe.PasteCheck] = nameof(Strings.CheckPaste),
    };

    private int _running;

    /// <summary>The probe page port (the firewall script allows this one).</summary>
    public const int DefaultPort = 8000;

    public int Port => DefaultPort;

    /// <summary>The Store (MSIX) build gets its firewall rule from the package manifest.</summary>
    public bool FirewallFromPackage => PackageIdentity.IsPackaged;

    public IReadOnlyList<string> PageUrls(ProbeKind kind) =>
        [.. ProbeServer.LanAddresses().Select(ip => string.Create(Inv, $"http://{ip}:{Port}{ProbeReadiness.Path(PageOf(kind))}"))];

    public async Task<ProbeReport> RunAsync(ProbeKind kind, IProgress<ProbeUpdate> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return Error(Strings.ProbeAlreadyRunning);
        }

        try
        {
            return await RunCoreAsync(kind, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            log.Write("[probe] cancelled");
            return new ProbeReport(ProbePhase.Cancelled, Strings.ProbeCancelled, []);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or COMException or TimeoutException)
        {
            log.Write($"[probe] failed: {ex}");
            return Error(Loc.Format(Strings.FormatError, ex.Message));
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public Task<FirewallState> CheckFirewallAsync(CancellationToken cancellationToken = default) =>
        Task.Run(
            () =>
            {
                var exe = Environment.ProcessPath;
                if (exe is null)
                {
                    return FirewallState.Unknown;
                }

                try
                {
                    var scope = FirewallRules.InboundTcp(exe, Port);
                    var judgement = FirewallRules.Judge(scope, FirewallFromPackage);
                    log.Write($"[probe] firewall: inbound TCP {Port} for {Path.GetFileName(exe)} -> {scope}{(FirewallFromPackage ? " (packaged)" : string.Empty)} -> {judgement}");
                    return judgement switch
                    {
                        FirewallJudgement.Allowed => FirewallState.Allowed,
                        FirewallJudgement.TooBroad => FirewallState.TooBroad,
                        FirewallJudgement.AllowedByPackage => FirewallState.AllowedByPackage,
                        _ => FirewallState.Missing,
                    };
                }
                catch (Exception ex) when (ex is InvalidOperationException or COMException or UnauthorizedAccessException or InvalidCastException)
                {
                    log.Write($"[probe] cannot read the firewall rules: {ex.Message}");
                    return FirewallState.Unknown;
                }
            },
            cancellationToken);

    public async Task<bool> AllowFirewallAsync(CancellationToken cancellationToken = default)
    {
        var exe = Environment.ProcessPath;
        if (exe is null || FirewallFromPackage)
        {
            // The packaged build never changes the firewall: its rule comes from the manifest (the button is hidden too).
            return false;
        }

        var transcript = Path.Combine(Path.GetTempPath(), $"IceScreenTouch-firewall-{Guid.NewGuid():N}.log");
        var arguments = FirewallRules.EncodedCommandArguments(
            LoadScript(),
            new Dictionary<string, string>
            {
                ["Exe"] = exe,
                ["Port"] = Port.ToString(Inv),
                ["Name"] = FirewallRuleName,
                ["LogPath"] = transcript,
            });
        log.Write($"[probe] asking for administrator rights to allow inbound TCP {Port} for {exe} from the local subnet");
        try
        {
            // The full path: by name alone, ShellExecute looks in the working folder first (often Downloads, where
            // the zip was opened), so a planted powershell.exe would run elevated.
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            using var process = Process.Start(new ProcessStartInfo(powershell, arguments)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (File.Exists(transcript))
            {
                var lines = await File.ReadAllLinesAsync(transcript, cancellationToken).ConfigureAwait(false);
                foreach (var line in FirewallRules.TranscriptBody(lines))
                {
                    log.Write("[fw  ] " + line);
                }
            }

            log.Write($"[probe] firewall script exit code {process.ExitCode}");
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            log.Write("[probe] the administrator prompt was declined");
            return false;
        }
        catch (Win32Exception ex)
        {
            log.Write($"[probe] cannot run the firewall script: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(transcript);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static ProbePage PageOf(ProbeKind kind) => kind switch
    {
        ProbeKind.Scroll => ProbePage.Scroll,
        ProbeKind.Latency => ProbePage.Latency,
        ProbeKind.Keys => ProbePage.Keys,
        _ => ProbePage.Touch,
    };

    private static ProbeReport Error(string message) => new(ProbePhase.Error, message, []);

    private static ProbeWaitingFor Map(ProbeMissing missing) =>
        (missing.HasFlag(ProbeMissing.Subscription) ? ProbeWaitingFor.Bluetooth : 0)
        | (missing.HasFlag(ProbeMissing.PageNotOpened) ? ProbeWaitingFor.PageNotOpened : 0)
        | (missing.HasFlag(ProbeMissing.PageNotInFront) ? ProbeWaitingFor.PageNotInFront : 0)
        | (missing.HasFlag(ProbeMissing.WrongPage) ? ProbeWaitingFor.WrongPage : 0)
        | (missing.HasFlag(ProbeMissing.FieldNotFocused) ? ProbeWaitingFor.FieldNotFocused : 0);

    private static string LoadScript()
    {
        using var stream = typeof(WindowsProbeService).Assembly.GetManifestResourceStream("IceScreenTouch.Windows.allow_firewall.ps1")
            ?? throw new InvalidOperationException("embedded firewall script is missing");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private async Task<ProbeReport> RunCoreAsync(ProbeKind kind, IProgress<ProbeUpdate> progress, CancellationToken cancellationToken)
    {
        if (mirror.GetProbeTarget() is not { } target)
        {
            return Error(Strings.ProbeIdleStopped);
        }

        if (target.Hid is not { } hid || target.Bridge is not { } bridge)
        {
            return Error(target.Settings.DryRun ? Strings.ProbeIdleDryRun : Strings.ProbeIdleNoBle);
        }

        if (kind == ProbeKind.Keys && !hid.HasKeyboard)
        {
            return Error(Strings.ProbeNoKeyboard);
        }

        // Stop when the user cancels or when the session stops (restart, exit).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, target.Stopping);
        var ct = linked.Token;
        progress.Report(new ProbeUpdate { Phase = ProbePhase.Starting });

        var page = PageOf(kind);
        await using var server = new ProbeServer(Port);
        try
        {
            await server.StartAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            log.Write($"[probe] cannot serve the page on TCP {Port}: {ex.Message}");
            return Error(Loc.Format(Strings.FormatProbePortBusy, Port, ex.Message));
        }

        var urls = server.PageUrls(page);
        var device = hid.Target is { } id ? hid.Clients().FirstOrDefault(c => c.Id == id)?.Name : null;
        log.Write($"[probe] {kind} probe: open {string.Join(" or ", urls)} in Safari on {(device is null ? "the device" : $"\"{device}\"")}");
        progress.Report(new ProbeUpdate { Phase = ProbePhase.Waiting, Urls = urls, WaitingFor = Map(Readiness()) });

        // The latency probe watches the middle of the mirror picture on screen; the spot is taken while the
        // bridge still follows the window (it stops updating the picture while suspended).
        using var sampler = kind == ProbeKind.Latency ? new ScreenSampler() : null;
        var desktop = new Win32Desktop();
        (int X, int Y)? spot = null;
        ProbeWaitingFor? last = null;
        while (true)
        {
            var missing = Readiness();
            var waiting = Map(missing);
            if (sampler is not null)
            {
                spot = MirrorSpot(bridge.Status, desktop, sampler);
                if (spot is null)
                {
                    waiting |= ProbeWaitingFor.MirrorHidden;
                }
            }

            if (waiting == ProbeWaitingFor.None)
            {
                break;
            }

            if (waiting != last)
            {
                var mirrorText = waiting.HasFlag(ProbeWaitingFor.MirrorHidden) ? "; the middle of the mirror picture is not black on screen" : string.Empty;
                log.Write($"[probe] waiting: {ProbeReadiness.Describe(missing, page)}{mirrorText}");
                progress.Report(new ProbeUpdate { Phase = ProbePhase.Waiting, WaitingFor = waiting });
                last = waiting;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        var hello = server.HelloFor(page)!;
        log.Write(string.Create(Inv, $"[probe] ready: page {hello.Vw}x{hello.Vh} viewport, {hello.Sw}x{hello.Sh} screen; pausing the mouse bridge"));
        // SuspendAsync counts the request at once: from here on every way out must resume the bridge, also a
        // timeout or a cancel while waiting for it to pause.
        var suspended = bridge.SuspendAsync(ct);
        try
        {
            await suspended.WaitAsync(SuspendTimeout, ct).ConfigureAwait(false);
            for (var s = 3; s > 0; s--)
            {
                progress.Report(new ProbeUpdate { Phase = ProbePhase.Countdown, SecondsLeft = s });
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }

            progress.Report(new ProbeUpdate { Phase = ProbePhase.Running });
            using var output = new LineWriter(line =>
            {
                log.Write("[probe] " + line);
                progress.Report(new ProbeUpdate { Phase = ProbePhase.Running, Line = line });
            });

            return kind switch
            {
                ProbeKind.Scroll => await ScrollAsync(new PageGuardSink(hid, server, page, output), server, output, target, ct).ConfigureAwait(false),
                ProbeKind.Latency => await LatencyAsync(new PageGuardSink(hid, server, page, output), server, new SpotSampler(sampler!, spot!.Value), output, ct).ConfigureAwait(false),
                ProbeKind.Keys => await KeysAsync(hid, server, output, HidKeyboardLayout.Us, ct).ConfigureAwait(false),
                _ => await TouchAsync(new PageGuardSink(hid, server, page, output), server, output, ct).ConfigureAwait(false),
            };
        }
        finally
        {
            bridge.Resume();
            log.Write("[probe] the mouse bridge is back");
        }

        ProbeMissing Readiness() => ProbeReadiness.Check(page, hid.IsSubscribed, server.HelloFor(page), ProbeClock.Now());
    }

    /// <summary>The middle of the mirror picture on screen, if the mirror window is there, on top at that point and black there.</summary>
    private static (int X, int Y)? MirrorSpot(BridgeStatus status, Win32Desktop desktop, ScreenSampler sampler)
    {
        if (status.Window == 0 || status.Picture is not { HasArea: true } picture)
        {
            return null;
        }

        var center = new PointD(picture.Left + (picture.Width / 2), picture.Top + (picture.Height / 2));
        if (!desktop.IsVisibleAt(status.Window, center))
        {
            return null;
        }

        var spot = ((int)Math.Round(center.X), (int)Math.Round(center.Y));
        return sampler.MeanLuma(spot.Item1, spot.Item2) is { } luma && luma < LatencyProbe.Threshold ? spot : null;
    }

    private static async Task<ProbeReport> LatencyAsync(ITouchSink hid, ProbeServer server, IPictureSampler sampler, LineWriter output, CancellationToken ct)
    {
        const int Trials = 10;
        var result = await Task.Run(() => new LatencyProbe(hid, server, sampler, output).RunAsync(Trials, ct), ct).ConfigureAwait(false);
        var downs = result.Downs.ToList();
        var ups = result.Ups.ToList();
        string Range(IEnumerable<double?> values)
        {
            var list = values.Where(v => v is not null).Select(v => v!.Value).ToList();
            return list.Count == 0
                ? "—"
                : Loc.Format(Strings.FormatLatencyRange, LatencyResult.Median(values)!.Value, list.Min(), list.Max(), list.Count);
        }

        var seenDowns = downs.Count(t => t.SeenAt is not null);
        var checks = new List<ProbeCheckItem>
        {
            new(Strings.LatencyPress, seenDowns > 0, Range(downs.Select(t => t.TotalMs))),
            new(Strings.LatencyTouchPart, downs.Any(t => t.TouchMs is not null), Loc.Format(Strings.FormatLatencyTouchDetail, Range(downs.Select(t => t.TouchMs)))),
            new(Strings.LatencyPicturePart, downs.Any(t => t.PictureMs is not null), Loc.Format(Strings.FormatLatencyPictureDetail, Range(downs.Select(t => t.PictureMs)))),
            new(Strings.LatencyRelease, ups.Any(t => t.SeenAt is not null), Range(ups.Select(t => t.TotalMs))),
            new(Strings.LatencyResolution, true, Loc.Format(Strings.FormatLatencyResolution, result.SampleMs, result.RttMs)),
        };
        if (seenDowns < Trials / 2)
        {
            return new ProbeReport(ProbePhase.Failed, Loc.Format(Strings.FormatLatencyTooFew, Trials, seenDowns), checks);
        }

        var total = LatencyResult.Median(downs.Select(t => t.TotalMs));
        var touch = LatencyResult.Median(downs.Select(t => t.TouchMs));
        var picture = LatencyResult.Median(downs.Select(t => t.PictureMs));
        var split = touch is { } a && picture is { } b ? Loc.Format(Strings.FormatLatencySplit, a, b) : string.Empty;
        return new ProbeReport(ProbePhase.Passed, Loc.Format(Strings.FormatLatencyPassed, total, split), checks);
    }

    /// <summary>The screen sampler pinned to one spot.</summary>
    private sealed class SpotSampler(ScreenSampler sampler, (int X, int Y) spot) : IPictureSampler
    {
        public double? MeanLuma() => sampler.MeanLuma(spot.X, spot.Y);
    }

    private static async Task<ProbeReport> TouchAsync(ITouchSink hid, ProbeServer server, LineWriter output, CancellationToken ct)
    {
        var result = await new ProbeRunner(new TouchPlayer(hid), server, output).RunAsync("r0", null, ct).ConfigureAwait(false);
        if (result is null)
        {
            return new ProbeReport(ProbePhase.Failed, Strings.TouchFailedNone, []);
        }

        var checks = result.Checks.Select(c => new ProbeCheckItem(CheckName(c.Name), c.Ok, c.Detail)).ToList();
        var latencies = result.Taps.Taps.Where(t => t.LatencyMs is not null).Select(t => t.LatencyMs!.Value).Order().ToList();
        var latency = latencies.Count > 0 ? Loc.Format(Strings.FormatTouchLatency, latencies[latencies.Count / 2]) : string.Empty;
        if (result.Passed)
        {
            return new ProbeReport(ProbePhase.Passed, Loc.Format(Strings.FormatTouchPassed, latency), checks);
        }

        var failed = checks.Count(c => !c.Ok);
        var orientationOnly = failed == 1 && result.Checks.Single(c => !c.Ok).Name == "orientation";
        return new ProbeReport(
            ProbePhase.Failed,
            orientationOnly
                ? Loc.Format(Strings.FormatTouchFailedOrientation, latency)
                : Loc.Format(Strings.FormatTouchFailed, failed, latency),
            checks);
    }

    private static async Task<ProbeReport> KeysAsync(IKeyboardSink hid, ProbeServer server, LineWriter output, HidKeyboardLayout layout, CancellationToken ct)
    {
        var result = await new KeyboardProbe(hid, server, output).RunAsync(layout, ct).ConfigureAwait(false);
        var checks = result.Checks.Select(c => new ProbeCheckItem(CheckName(c.Name), c.Ok, c.Detail)).ToList();
        if (result.NothingArrived)
        {
            return new ProbeReport(ProbePhase.Failed, Strings.KeysFailedNone, checks);
        }

        var latency = result.LatencyMs is { } ms ? Loc.Format(Strings.FormatKeysLatency, ms) : string.Empty;
        if (result.Passed)
        {
            return new ProbeReport(ProbePhase.Passed, Loc.Format(Strings.FormatKeysPassed, latency), checks);
        }

        var hint = result.JapaneseInput
            ? Strings.KeysJapaneseHint
            : result.NavigationKeysTaken
            ? Strings.KeysNavigationTakenHint
            : result.LikelyLayout is not null
                ? Strings.KeysLayoutHint // keys are always sent as US: the device must be set to a US (QWERTY) layout
                : latency;
        return new ProbeReport(ProbePhase.Failed, Loc.Format(Strings.FormatKeysFailed, checks.Count(c => !c.Ok), hint), checks);
    }

    /// <summary>The user's text for a probe check (its English name when there is none).</summary>
    internal static string CheckName(string name) =>
        CheckNames.TryGetValue(name, out var resource) ? Strings.ResourceManager.GetString(resource, Strings.Culture) ?? name : name;

    private static async Task<ProbeReport> ScrollAsync(ITouchSink hid, ProbeServer server, LineWriter output, WindowsMirrorService.ProbeTarget target, CancellationToken ct)
    {
        var wheel = target.Settings.Wheel;
        var rows = await new ScrollProbe(hid, server, output).RunAsync(3, wheel.StepPerNotch, [wheel.Momentum], ct).ConfigureAwait(false);
        var checks = rows.Select(r =>
        {
            var name = r.Scenario.StartsWith("1 ", StringComparison.Ordinal) ? Strings.ScrollOneNotch : Strings.ScrollFiveFast;
            var each = string.Join(" / ", r.Trials.Select(t => t.Ratio.ToString("0.00", CultureInfo.CurrentCulture)));
            var tail = r.MaxTail is { } t ? Loc.Format(Strings.FormatScrollTail, t.TotalMilliseconds) : string.Empty;
            return new ProbeCheckItem(name, !r.MissingTouches && !double.IsNaN(r.MeanRatio), Loc.Format(Strings.FormatScrollRatio, r.MeanRatio, each, tail));
        }).ToList();
        var setting = Loc.Format(Strings.FormatScrollSetting, wheel.StepPerNotch * 100, wheel.Momentum * 100);
        return checks.All(c => c.Ok)
            ? new ProbeReport(ProbePhase.Passed, Loc.Format(Strings.FormatScrollPassed, setting), checks)
            : new ProbeReport(ProbePhase.Failed, Loc.Format(Strings.FormatScrollFailed, setting), checks);
    }

    /// <summary>A TextWriter that hands over whole lines (the probe classes print to a TextWriter).</summary>
    private sealed class LineWriter(Action<string> onLine) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            if (value == '\n')
            {
                Flush();
            }
            else if (value != '\r')
            {
                _line.Append(value);
            }
        }

        public override void Write(string? value)
        {
            foreach (var c in value ?? string.Empty)
            {
                Write(c);
            }
        }

        public override void WriteLine(string? value)
        {
            Write(value);
            Write('\n');
        }

        public override void Flush()
        {
            var line = _line.ToString();
            _line.Clear();
            onLine(line);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _line.Length > 0)
            {
                Flush();
            }

            base.Dispose(disposing);
        }
    }
}
