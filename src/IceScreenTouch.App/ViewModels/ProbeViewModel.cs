// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections.ObjectModel;
using System.Windows.Input;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;

namespace IceScreenTouch.App.ViewModels;

/// <summary>
/// The probe tab: the page URLs, the firewall rule, the buttons that run the touch probe or the scroll
/// measurement, and the result. UI-thread only; <paramref name="post"/> brings work from other threads to it.
/// </summary>
public sealed class ProbeViewModel : ObservableObject
{
    /// <summary>Most output lines kept in the view.</summary>
    public const int MaxOutputLines = 400;

    private readonly IProbeService _probe;
    private readonly Action<Action> _post;
    private readonly Action _runningChanged;
    private CancellationTokenSource? _cts;
    private MirrorStatus _status = MirrorStatus.Idle;
    private ProbeKind _kind;
    private bool _isRunning;
    private bool _firewallBusy;
    private bool _hasResult;
    private FirewallState _firewall;
    private string _statusText = Strings.Stopped;
    private StatusLevel _statusLevel;
    private string? _qrUrl;
    private IReadOnlyList<string> _runUrls = [];
    private string _firewallText = Strings.StepChecking;
    private StatusLevel _firewallLevel = StatusLevel.Idle;

    /// <summary>Creates the tab. <paramref name="runningChanged"/> is called when a run starts or ends.</summary>
    public ProbeViewModel(IProbeService probe, Action<Action> post, Action? runningChanged = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(post);
        _probe = probe;
        _post = post;
        _runningChanged = runningChanged ?? (() => { });
        StartTouchCommand = new RelayCommand(() => Start(ProbeKind.Touch), () => CanStart);
        StartScrollCommand = new RelayCommand(() => Start(ProbeKind.Scroll), () => CanStart);
        StartLatencyCommand = new RelayCommand(() => Start(ProbeKind.Latency), () => CanStart);
        StartKeysCommand = new RelayCommand(() => Start(ProbeKind.Keys), () => CanStart);
        CancelCommand = new RelayCommand(Cancel, () => IsRunning);
        AllowFirewallCommand = new RelayCommand(AllowFirewall, () => !_firewallBusy && !_probe.FirewallFromPackage && Firewall != FirewallState.Allowed);
        UpdateIdleText();
        CheckFirewall();
    }

    /// <summary>
    /// The address to show as a QR code while a run waits for its page to be opened (the first LAN address), or
    /// null.
    /// </summary>
    public string? QrUrl
    {
        get => _qrUrl;
        private set
        {
            if (Set(ref _qrUrl, value))
            {
                OnPropertyChanged(nameof(ShowQr));
            }
        }
    }

    /// <summary>A QR code is shown.</summary>
    public bool ShowQr => QrUrl is not null;

    /// <summary>What is happening, or the result.</summary>
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }

    /// <summary>Colour of <see cref="StatusText"/>.</summary>
    public StatusLevel StatusLevel
    {
        get => _statusLevel;
        private set
        {
            if (Set(ref _statusLevel, value))
            {
                OnPropertyChanged(nameof(StatusOk));
                OnPropertyChanged(nameof(StatusWaiting));
                OnPropertyChanged(nameof(StatusError));
            }
        }
    }

    /// <summary>For the view's style classes.</summary>
    public bool StatusOk => StatusLevel == StatusLevel.Ok;

    /// <inheritdoc cref="StatusOk"/>
    public bool StatusWaiting => StatusLevel == StatusLevel.Waiting;

    /// <inheritdoc cref="StatusOk"/>
    public bool StatusError => StatusLevel == StatusLevel.Error;

    /// <summary>The firewall rule as last checked.</summary>
    public FirewallState Firewall
    {
        get => _firewall;
        private set
        {
            if (Set(ref _firewall, value))
            {
                ((RelayCommand)AllowFirewallCommand).Refresh();
            }
        }
    }

    /// <summary>
    /// Whether the "set up the firewall" button is offered: never in the packaged (Store) build, whose rule comes
    /// from the package.
    /// </summary>
    public bool ShowFirewallButton => !_probe.FirewallFromPackage;

    /// <summary>Firewall line.</summary>
    public string FirewallText { get => _firewallText; private set => Set(ref _firewallText, value); }

    /// <summary>Colour of <see cref="FirewallText"/>.</summary>
    public StatusLevel FirewallLevel
    {
        get => _firewallLevel;
        private set
        {
            if (Set(ref _firewallLevel, value))
            {
                OnPropertyChanged(nameof(FirewallOk));
                OnPropertyChanged(nameof(FirewallWaiting));
            }
        }
    }

    /// <inheritdoc cref="StatusOk"/>
    public bool FirewallOk => FirewallLevel == StatusLevel.Ok;

    /// <inheritdoc cref="StatusOk"/>
    public bool FirewallWaiting => FirewallLevel == StatusLevel.Waiting;

    /// <summary>A probe is running (the mouse bridge may be paused).</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                RefreshCommands();
                _runningChanged();
            }
        }
    }

    /// <summary>A probe can start: the session runs with Bluetooth, and no probe is running.</summary>
    public bool CanStart => !IsRunning && _status.Running && _status.Ble is BleState.Advertising or BleState.Connected;

    /// <summary>A touch probe passed since the app started (so Zoom is on and touches arrive).</summary>
    public bool TouchVerified { get; private set; }

    /// <summary>The pass/fail items of the last run.</summary>
    public ObservableCollection<ProbeCheckItem> Checks { get; } = [];

    /// <summary>The detailed output of the last run.</summary>
    public ObservableCollection<string> Output { get; } = [];

    /// <summary>Run the touch probe.</summary>
    public ICommand StartTouchCommand { get; }

    /// <summary>Run the scroll measurement.</summary>
    public ICommand StartScrollCommand { get; }

    /// <summary>Run the latency measurement.</summary>
    public ICommand StartLatencyCommand { get; }

    /// <summary>Run the keyboard probe.</summary>
    public ICommand StartKeysCommand { get; }

    /// <summary>Stop the run.</summary>
    public ICommand CancelCommand { get; }

    /// <summary>Create the firewall rule (administrator).</summary>
    public ICommand AllowFirewallCommand { get; }

    /// <summary>The last run (for tests).</summary>
    public Task Last { get; private set; } = Task.CompletedTask;

    /// <summary>Takes the session state (called by the status timer).</summary>
    public void Refresh(MirrorStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _status = status;
        RefreshCommands();
        if (!IsRunning && !_hasResult)
        {
            UpdateIdleText();
        }
    }

    /// <summary>Text for a "waiting" update.</summary>
    public static string DescribeWaiting(ProbeKind kind, ProbeWaitingFor waiting, FirewallState firewall, bool firewallFromPackage = false)
    {
        var page = kind switch
        {
            ProbeKind.Scroll => Strings.PageScroll,
            ProbeKind.Latency => Strings.PageLatency,
            ProbeKind.Keys => Strings.PageKeys,
            _ => Strings.PageTouch,
        };
        if (waiting.HasFlag(ProbeWaitingFor.Bluetooth))
        {
            return Strings.WaitBluetooth;
        }

        if (waiting.HasFlag(ProbeWaitingFor.WrongPage))
        {
            return Loc.Format(Strings.FormatWaitWrongPage, page);
        }

        if (waiting.HasFlag(ProbeWaitingFor.PageNotOpened))
        {
            var hint = firewall is FirewallState.Missing or FirewallState.Unknown
                ? (firewallFromPackage ? Strings.WaitFirewallHintPackaged : Strings.WaitFirewallHint)
                : Strings.WaitNetworkHint;
            return Loc.Format(Strings.FormatWaitPageNotOpened, page, hint);
        }

        if (waiting.HasFlag(ProbeWaitingFor.PageNotInFront))
        {
            return Loc.Format(Strings.FormatWaitPageNotInFront, page);
        }

        if (waiting.HasFlag(ProbeWaitingFor.MirrorHidden))
        {
            return Strings.WaitMirrorHidden;
        }

        if (waiting.HasFlag(ProbeWaitingFor.FieldNotFocused))
        {
            return Strings.WaitFieldNotFocused;
        }

        return Strings.WaitSoon;
    }

    /// <summary>Cancels a running probe (also used when the app exits).</summary>
    public void Cancel() => _cts?.Cancel();

    private void Start(ProbeKind kind)
    {
        if (!CanStart)
        {
            return;
        }

        Last = RunAsync(kind);
    }

    private async Task RunAsync(ProbeKind kind)
    {
        _kind = kind;
        _runUrls = [];
        QrUrl = null;
        Checks.Clear();
        Output.Clear();
        using var cts = new CancellationTokenSource();
        _cts = cts;
        IsRunning = true;
        Show(kind == ProbeKind.Touch ? Strings.ProbePreparingTouch : Strings.ProbePreparingMeasure, StatusLevel.Waiting);

        // Progress<T> would capture the synchronization context; post explicitly so tests need none.
        var progress = new CallbackProgress<ProbeUpdate>(u => _post(() => OnUpdate(u)));
        ProbeReport report;
        try
        {
            // Inside the try: listing the network interfaces can throw, and outside it the failure went to the
            // unobserved task and the button seemed to do nothing (review, 2026-11-07).
            _runUrls = _probe.PageUrls(kind);
            report = await _probe.RunAsync(kind, progress, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            report = new ProbeReport(ProbePhase.Cancelled, Strings.ProbeCancelled, []);
        }
#pragma warning disable CA1031 // Show any failure instead of crashing the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            report = new ProbeReport(ProbePhase.Error, Loc.Format(Strings.FormatError, ex.Message), []);
        }
        finally
        {
            _cts = null;
        }

        // Updates posted before the report must not overwrite it.
        _post(() =>
        {
            QrUrl = null;
            foreach (var c in report.Checks)
            {
                Checks.Add(c);
            }

            Show(report.Summary, report.Phase switch
            {
                ProbePhase.Passed => StatusLevel.Ok,
                ProbePhase.Cancelled => StatusLevel.Idle,
                _ => StatusLevel.Error,
            });
            _hasResult = true;
            if (kind == ProbeKind.Touch && report.Phase == ProbePhase.Passed)
            {
                TouchVerified = true;
            }

            IsRunning = false;
        });
    }

    private void OnUpdate(ProbeUpdate u)
    {
        // The server's own URLs (with the run's key) replace the ones guessed at the start, even when there is none
        // (no network): a URL without the key would only be refused.
        if (u.Urls is { } urls)
        {
            _runUrls = urls;
        }

        if (u.Line is { } line)
        {
            Output.Add(line);
            while (Output.Count > MaxOutputLines)
            {
                Output.RemoveAt(0);
            }
        }

        if (!IsRunning)
        {
            return;
        }

        // The QR code is for opening the page: shown until the page is open in front.
        const ProbeWaitingFor PageMissing = ProbeWaitingFor.PageNotOpened | ProbeWaitingFor.WrongPage | ProbeWaitingFor.PageNotInFront;
        QrUrl = u.Phase is ProbePhase.Waiting or ProbePhase.Starting && (u.WaitingFor & PageMissing) != 0 && _runUrls.Count > 0 ? _runUrls[0] : null;

        var running = _kind switch
        {
            ProbeKind.Scroll => Strings.ProbeRunningScroll,
            ProbeKind.Latency => Strings.ProbeRunningLatency,
            ProbeKind.Keys => Strings.ProbeRunningKeys,
            _ => Strings.ProbeRunningTouch,
        };
        switch (u.Phase)
        {
            case ProbePhase.Starting:
                Show(Strings.ProbePreparingPage, StatusLevel.Waiting);
                break;
            case ProbePhase.Waiting:
                Show(DescribeWaiting(_kind, u.WaitingFor, Firewall, _probe.FirewallFromPackage), StatusLevel.Waiting);
                break;
            case ProbePhase.Countdown:
                Show(Loc.Format(Strings.FormatProbeCountdown, u.SecondsLeft), StatusLevel.Waiting);
                break;
            case ProbePhase.Running:
                Show(running, StatusLevel.Waiting);
                break;
        }
    }

    private void UpdateIdleText()
    {
        if (!_status.Running)
        {
            Show(Strings.ProbeIdleStopped, StatusLevel.Idle);
        }
        else if (_status.Ble is not (BleState.Advertising or BleState.Connected))
        {
            Show(_status.Ble == BleState.DryRun ? Strings.ProbeIdleDryRun : Strings.ProbeIdleNoBle, StatusLevel.Idle);
        }
        else
        {
            // The page server runs only during a measurement: the button comes first.
            Show(Strings.ProbeIdleReady, StatusLevel.Idle);
        }
    }

    private void Show(string text, StatusLevel level)
    {
        StatusText = text;
        StatusLevel = level;
    }

    private void RefreshCommands()
    {
        ((RelayCommand)StartTouchCommand).Refresh();
        ((RelayCommand)StartScrollCommand).Refresh();
        ((RelayCommand)StartLatencyCommand).Refresh();
        ((RelayCommand)StartKeysCommand).Refresh();
        ((RelayCommand)CancelCommand).Refresh();
    }

    private void CheckFirewall()
    {
        _ = Task.Run(async () =>
        {
            FirewallState state;
            try
            {
                state = await _probe.CheckFirewallAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // An unreadable firewall is shown as "unknown".
            catch (Exception)
#pragma warning restore CA1031
            {
                state = FirewallState.Unknown;
            }

            _post(() => SetFirewall(state));
        });
    }

    private void SetFirewall(FirewallState state)
    {
        Firewall = state;
        (FirewallText, FirewallLevel) = state switch
        {
            FirewallState.Allowed => (Loc.Format(Strings.FormatFirewallAllowed, _probe.Port), StatusLevel.Ok),
            FirewallState.AllowedByPackage => (Loc.Format(Strings.FormatFirewallAllowedByPackage, _probe.Port), StatusLevel.Ok),
            FirewallState.Missing => (Loc.Format(_probe.FirewallFromPackage ? Strings.FormatFirewallMissingPackaged : Strings.FormatFirewallMissing, _probe.Port), StatusLevel.Waiting),
            FirewallState.TooBroad => (Loc.Format(Strings.FormatFirewallTooBroad, _probe.Port), StatusLevel.Waiting),
            _ => (Strings.FirewallUnknown, StatusLevel.Idle),
        };
    }

    private void AllowFirewall()
    {
        _firewallBusy = true;
        ((RelayCommand)AllowFirewallCommand).Refresh();
        FirewallText = Strings.FirewallWaitingUac;
        _ = Task.Run(async () =>
        {
            bool ok;
            try
            {
                ok = await _probe.AllowFirewallAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Reported in the firewall line.
            catch (Exception)
#pragma warning restore CA1031
            {
                ok = false;
            }

            FirewallState state;
            try
            {
                state = await _probe.CheckFirewallAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception)
#pragma warning restore CA1031
            {
                state = FirewallState.Unknown;
            }

            _post(() =>
            {
                _firewallBusy = false;
                SetFirewall(state);
                if (!ok)
                {
                    FirewallText = Loc.Format(Strings.FormatFirewallFailed, FirewallText);
                }

                ((RelayCommand)AllowFirewallCommand).Refresh();
            });
        });
    }

    /// <summary>IProgress that calls back on the reporting thread.</summary>
    private sealed class CallbackProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
