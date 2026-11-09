using System.Collections.ObjectModel;
using System.Globalization;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.ViewModels;

/// <summary>How a status line is shown.</summary>
public enum StatusLevel
{
    /// <summary>Not running.</summary>
    Idle,

    /// <summary>Working as it should.</summary>
    Ok,

    /// <summary>Waiting for the user or the phone.</summary>
    Waiting,

    /// <summary>Needs attention.</summary>
    Error,
}

/// <summary>An entry of the target device list on the status tab.</summary>
/// <param name="Id">The device's Bluetooth ID; null = automatic.</param>
/// <param name="Label">What the list shows.</param>
public sealed record TargetOption(string? Id, string Label)
{
    /// <inheritdoc/>
    public override string ToString() => Label;
}

/// <summary>
/// The status window: what the session is doing, what the user should do next, the setup steps, the buttons,
/// the settings form and the log. UI-thread only; <paramref name="post"/> brings work from other threads to it.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly IMirrorService _mirror;
    private readonly SettingsStore _store;
    private readonly AppLog _log;
    private readonly Action<Action> _post;
    private readonly LicenseService _license;
    private AppSettings _settings;
    private MirrorStatus _status = MirrorStatus.Idle;
    private bool _busy;

    // Store license: a start that the license refused (or a session it stopped) resumes when the license allows it again.
    private bool _blockedByLicense;
    private bool _licensePending;
    private LicenseState? _licenseShown;
    private long _trialMinutes = long.MinValue;
    private string _trialText = string.Empty;
    private bool _isTrial;
    private bool _isExpired;
    private string _purchaseMessage = string.Empty;

    private string _bleText = string.Empty;
    private StatusLevel _bleLevel;
    private string _receiverText = string.Empty;
    private StatusLevel _receiverLevel;
    private string _pictureText = "—";
    private string _rotationText = string.Empty;
    private bool _isLandscape;
    private string _hint = string.Empty;
    private string _headline = string.Empty;
    private StatusLevel _headlineLevel;
    private bool _running;
    private string _statsText = string.Empty;
    private bool _canHome;
    private bool _hasDevices;
    private string _targetText = string.Empty;
    private TargetOption? _selectedTarget;
    private bool _updatingTargets;

    // The settings form (applied with ApplySettings).
    private string _airPlayName = string.Empty;
    private bool _startUxPlay;
    private string _uxPlayPath = string.Empty;
    private string _msysBin = string.Empty;
    private string _attachProcessName = string.Empty;
    private bool _detectPicture;
    private ScreenPreset _screen = ScreenPreset.Default;
    private bool _followVideoSize;
    private bool _rightButtonLongPress;
    private double _longPressMilliseconds;
    private bool _wheelScroll;
    private double _wheelStepPercent;
    private double _wheelMomentumPercent;
    private bool _keyboardInput;
    private bool _verboseLog;
    private bool _closeToTray;
    private bool _dryRun;
    private LanguageChoice? _language;
    private ThemeChoice? _theme;
    private bool _settingsDirty;

    /// <summary>The tabs, in the order of the view.</summary>
    public const int StatusTab = 0;

    /// <inheritdoc cref="StatusTab"/>
    public const int SettingsTab = 1;

    /// <inheritdoc cref="StatusTab"/>
    public const int ProbeTab = 2;

    /// <inheritdoc cref="StatusTab"/>
    public const int LogTab = 3;

    /// <inheritdoc cref="StatusTab"/>
    public const int AboutTab = 4;

    private int _selectedTab;

    /// <summary>
    /// Creates the view model with the loaded <paramref name="settings"/>. Without <paramref name="probe"/> there
    /// is no probe tab; without <paramref name="receivers"/> the setup tab lists no receivers; without
    /// <paramref name="license"/> there is no license check (like a build of your own).
    /// </summary>
    public MainViewModel(IMirrorService mirror, SettingsStore store, AppLog log, AppSettings settings, Action<Action> post, IProbeService? probe = null, IReceiverFinder? receivers = null, LicenseService? license = null)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        _mirror = mirror;
        _store = store;
        _log = log;
        _settings = settings;
        _post = post;
        _license = license ?? new LicenseService(null, log: log);

        HomeCommand = new RelayCommand(_mirror.Home, () => CanHome);
        ToggleRotationCommand = new RelayCommand(_mirror.ToggleRotation, () => _status.Running);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => !_busy && !ProbeRunning && _license.Current.AllowsSession);
        StartStopCommand = new AsyncRelayCommand(() => _status.Running ? StopAsync() : StartAsync(), () => !_busy && !ProbeRunning && (_status.Running || _license.Current.AllowsSession));
        BuyCommand = new AsyncRelayCommand(BuyAsync);
        ApplySettingsCommand = new AsyncRelayCommand(ApplySettingsAsync, () => !_busy && !ProbeRunning && SettingsDirty);
        if (probe is not null)
        {
            // A restart would pull Bluetooth from under a running probe.
            Probe = new ProbeViewModel(probe, post, () =>
            {
                RestartCommand.Refresh();
                StartStopCommand.Refresh();
                ApplySettingsCommand.Refresh();
            });
        }
        RevertSettingsCommand = new RelayCommand(LoadForm, () => SettingsDirty);
        Setup = new SetupViewModel(receivers, post, UseReceiverAsync);
        OpenProbeCommand = new RelayCommand(() => SelectedTab = ProbeTab, () => HasProbe);

        foreach (var line in log.Recent())
        {
            LogLines.Add(line);
        }

        _mirror.LandscapeRotationChanged += (_, r) => _post(() => OnRotationChanged(r));
        _license.Changed += (_, _) => _post(() => LicenseChange = OnLicenseChangedAsync());
        LoadForm();
        Refresh();
    }

    /// <summary>The settings in effect.</summary>
    public AppSettings Settings => _settings;

    /// <summary>The probe tab, or null when the platform has no probe.</summary>
    public ProbeViewModel? Probe { get; }

    /// <summary>The probe tab exists.</summary>
    public bool HasProbe => Probe is not null;

    /// <summary>The setup tab.</summary>
    public SetupViewModel Setup { get; }

    /// <summary>The About tab.</summary>
    public AboutViewModel About { get; } = new();

    /// <summary>The last handling of a license change (for tests).</summary>
    internal Task LicenseChange { get; private set; } = Task.CompletedTask;

    /// <summary>Buy the app in the Microsoft Store.</summary>
    public AsyncRelayCommand BuyCommand { get; }

    /// <summary>The trial runs: the status tab shows the time left and the Buy button.</summary>
    public bool IsTrial { get => _isTrial; private set => Set(ref _isTrial, value); }

    /// <summary>The trial ended and the app was not bought: the session does not run.</summary>
    public bool IsExpired { get => _isExpired; private set => Set(ref _isExpired, value); }

    /// <summary>
    /// What the last Buy click came to when it needs a word: the store could not be opened, or the purchase is not in the
    /// license yet. Empty otherwise (also after the user closed the store's dialog).
    /// </summary>
    public string PurchaseMessage
    {
        get => _purchaseMessage;
        private set
        {
            if (Set(ref _purchaseMessage, value))
            {
                OnPropertyChanged(nameof(HasPurchaseMessage));
            }
        }
    }

    /// <summary>There is a <see cref="PurchaseMessage"/> to show.</summary>
    public bool HasPurchaseMessage => _purchaseMessage.Length > 0;

    /// <summary>"Trial: 23 hours left (until Nov 9, 14:00)" (empty outside the trial).</summary>
    public string TrialText { get => _trialText; private set => Set(ref _trialText, value); }

    /// <summary>The tab shown (<see cref="StatusTab"/>, <see cref="SettingsTab"/>, ...).</summary>
    public int SelectedTab { get => _selectedTab; set => Set(ref _selectedTab, value); }

    private bool ProbeRunning => Probe?.IsRunning ?? false;

    /// <summary>The state of the whole session in a few words, at the top of the status tab and in the tray.</summary>
    public string Headline { get => _headline; private set => Set(ref _headline, value); }

    /// <summary>Colour of the headline.</summary>
    public StatusLevel HeadlineLevel
    {
        get => _headlineLevel;
        private set
        {
            if (Set(ref _headlineLevel, value))
            {
                OnPropertyChanged(nameof(HeadlineOk));
                OnPropertyChanged(nameof(HeadlineWaiting));
                OnPropertyChanged(nameof(HeadlineError));
            }
        }
    }

    /// <inheritdoc cref="BleOk"/>
    public bool HeadlineOk => HeadlineLevel == StatusLevel.Ok;

    /// <inheritdoc cref="BleOk"/>
    public bool HeadlineWaiting => HeadlineLevel == StatusLevel.Waiting;

    /// <inheritdoc cref="BleOk"/>
    public bool HeadlineError => HeadlineLevel == StatusLevel.Error;

    /// <summary>The session is running (the start/stop button stops it).</summary>
    public bool IsRunning
    {
        get => _running;
        private set
        {
            if (Set(ref _running, value))
            {
                OnPropertyChanged(nameof(StartStopText));
                OnPropertyChanged(nameof(StartStopTip));
            }
        }
    }

    /// <summary>"Stop" while running, else "Start".</summary>
    public string StartStopText => IsRunning ? Strings.ButtonStop : Strings.ButtonStart;

    /// <summary>Tooltip of the start/stop button.</summary>
    public string StartStopTip => IsRunning ? Strings.TipStop : Strings.TipStart;

    /// <summary>Bluetooth status line.</summary>
    public string BleText { get => _bleText; private set => Set(ref _bleText, value); }

    /// <summary>Colour of the Bluetooth status.</summary>
    public StatusLevel BleLevel
    {
        get => _bleLevel;
        private set
        {
            if (Set(ref _bleLevel, value))
            {
                OnPropertyChanged(nameof(BleOk));
                OnPropertyChanged(nameof(BleWaiting));
                OnPropertyChanged(nameof(BleError));
            }
        }
    }

    /// <summary>For the view's style classes.</summary>
    public bool BleOk => BleLevel == StatusLevel.Ok;

    /// <inheritdoc cref="BleOk"/>
    public bool BleWaiting => BleLevel == StatusLevel.Waiting;

    /// <inheritdoc cref="BleOk"/>
    public bool BleError => BleLevel == StatusLevel.Error;

    /// <summary>Receiver status line.</summary>
    public string ReceiverText { get => _receiverText; private set => Set(ref _receiverText, value); }

    /// <summary>Colour of the receiver status.</summary>
    public StatusLevel ReceiverLevel
    {
        get => _receiverLevel;
        private set
        {
            if (Set(ref _receiverLevel, value))
            {
                OnPropertyChanged(nameof(ReceiverOk));
                OnPropertyChanged(nameof(ReceiverWaiting));
                OnPropertyChanged(nameof(ReceiverError));
            }
        }
    }

    /// <inheritdoc cref="BleOk"/>
    public bool ReceiverOk => ReceiverLevel == StatusLevel.Ok;

    /// <inheritdoc cref="BleOk"/>
    public bool ReceiverWaiting => ReceiverLevel == StatusLevel.Waiting;

    /// <inheritdoc cref="BleOk"/>
    public bool ReceiverError => ReceiverLevel == StatusLevel.Error;

    /// <summary>Picture size and orientation.</summary>
    public string PictureText { get => _pictureText; private set => Set(ref _pictureText, value); }

    /// <summary>Rotation in effect for a landscape picture, or the upside-down portrait (empty for a normal portrait).</summary>
    public string RotationText
    {
        get => _rotationText;
        private set
        {
            if (Set(ref _rotationText, value))
            {
                OnPropertyChanged(nameof(HasRotationText));
            }
        }
    }

    /// <summary>The rotation line is shown (landscape, or an upside-down portrait).</summary>
    public bool HasRotationText => _rotationText.Length > 0;

    /// <summary>The picture is landscape.</summary>
    public bool IsLandscape { get => _isLandscape; private set => Set(ref _isLandscape, value); }

    /// <summary>What the user should do next.</summary>
    public string Hint { get => _hint; private set => Set(ref _hint, value); }

    /// <summary>Counters.</summary>
    public string StatsText { get => _statsText; private set => Set(ref _statsText, value); }

    /// <summary>The home button works (the phone is listening).</summary>
    public bool CanHome
    {
        get => _canHome;
        private set
        {
            if (Set(ref _canHome, value))
            {
                ((RelayCommand)HomeCommand).Refresh();
            }
        }
    }

    /// <summary>A device is connected over Bluetooth (the target row is shown).</summary>
    public bool HasDevices { get => _hasDevices; private set => Set(ref _hasDevices, value); }

    /// <summary>"Automatic", the connected devices, and the chosen one when it is not connected.</summary>
    public ObservableCollection<TargetOption> TargetOptions { get; } = [];

    /// <summary>The device the user chose (or automatic); choosing one takes effect at once and is saved.</summary>
    public TargetOption? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (!Set(ref _selectedTarget, value) || _updatingTargets || value is null || value.Id == _settings.TargetDevice)
            {
                return;
            }

            var name = _status.Devices.FirstOrDefault(d => d.Id == value.Id)?.Name;
            _settings = _settings with { TargetDevice = value.Id, TargetDeviceName = value.Id is null ? null : name };
            Save();
            _mirror.ChooseTarget(value.Id);
            _log.Write($"[app ] input target set to {(value.Id is null ? "automatic" : name ?? LogPrivacy.LogId(value.Id))}");
            Refresh();
        }
    }

    /// <summary>Which device gets the input now, and why.</summary>
    public string TargetText { get => _targetText; private set => Set(ref _targetText, value); }

    /// <summary>The log folder, or null for a memory-only log.</summary>
    public string? LogFolder => _log.Directory;

    /// <summary>Latest log lines, oldest first.</summary>
    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>Home gesture.</summary>
    public System.Windows.Input.ICommand HomeCommand { get; }

    /// <summary>Landscape rotation 90 &lt;-&gt; 270.</summary>
    public System.Windows.Input.ICommand ToggleRotationCommand { get; }

    /// <summary>Stop and start the session.</summary>
    public AsyncRelayCommand RestartCommand { get; }

    /// <summary>Start a stopped session, or stop a running one.</summary>
    public AsyncRelayCommand StartStopCommand { get; }

    /// <summary>Show the probe tab (to check that Zoom is on).</summary>
    public System.Windows.Input.ICommand OpenProbeCommand { get; }

    /// <summary>Save the form and restart.</summary>
    public AsyncRelayCommand ApplySettingsCommand { get; }

    /// <summary>Throw away the form changes.</summary>
    public System.Windows.Input.ICommand RevertSettingsCommand { get; }

    /// <summary>Settings form: AirPlay name.</summary>
    public string AirPlayName { get => _airPlayName; set => SetForm(ref _airPlayName, value); }

    /// <summary>Settings form: start uxplay (else follow a running receiver).</summary>
    public bool StartUxPlay
    {
        get => _startUxPlay;
        set
        {
            if (SetForm(ref _startUxPlay, value))
            {
                OnPropertyChanged(nameof(AttachToProcess));
            }
        }
    }

    /// <summary>Settings form: follow a running receiver.</summary>
    public bool AttachToProcess { get => !StartUxPlay; set => StartUxPlay = !value; }

    /// <summary>Settings form: uxplay.exe (empty = automatic).</summary>
    public string UxPlayPath { get => _uxPlayPath; set => SetForm(ref _uxPlayPath, value); }

    /// <summary>Settings form: MSYS2 bin folder (empty = default).</summary>
    public string MsysBin { get => _msysBin; set => SetForm(ref _msysBin, value); }

    /// <summary>Settings form: process to follow.</summary>
    public string AttachProcessName { get => _attachProcessName; set => SetForm(ref _attachProcessName, value); }

    /// <summary>Settings form: find the picture in the receiver window.</summary>
    public bool DetectPicture { get => _detectPicture; set => SetForm(ref _detectPicture, value); }

    /// <summary>Settings form: the device screen presets.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Bound from XAML as an instance property")]
    public IReadOnlyList<ScreenPreset> ScreenPresets => ScreenPreset.All;

    /// <summary>Settings form: the device screen shape.</summary>
    public ScreenPreset Screen { get => _screen; set => SetForm(ref _screen, value ?? ScreenPreset.Default); }

    /// <summary>Settings form: resize the window to the picture.</summary>
    public bool FollowVideoSize { get => _followVideoSize; set => SetForm(ref _followVideoSize, value); }

    /// <summary>Settings form: right button = long press.</summary>
    public bool RightButtonLongPress { get => _rightButtonLongPress; set => SetForm(ref _rightButtonLongPress, value); }

    /// <summary>Settings form: long press hold (ms).</summary>
    public double LongPressMilliseconds
    {
        get => _longPressMilliseconds;
        set => SetForm(ref _longPressMilliseconds, Math.Round(value / 50) * 50);
    }

    /// <summary>Settings form: wheel = scroll.</summary>
    public bool WheelScroll { get => _wheelScroll; set => SetForm(ref _wheelScroll, value); }

    /// <summary>Settings form: finger travel per notch (% of the picture).</summary>
    public double WheelStepPercent { get => _wheelStepPercent; set => SetForm(ref _wheelStepPercent, Math.Round(value)); }

    /// <summary>Settings form: momentum when the wheel stops (%).</summary>
    public double WheelMomentumPercent { get => _wheelMomentumPercent; set => SetForm(ref _wheelMomentumPercent, Math.Round(value / 5) * 5); }

    /// <summary>Settings form: keys typed in the mirror window go to the device.</summary>
    public bool KeyboardInput { get => _keyboardInput; set => SetForm(ref _keyboardInput, value); }

    /// <summary>Settings form: log every touch.</summary>
    public bool VerboseLog { get => _verboseLog; set => SetForm(ref _verboseLog, value); }

    /// <summary>Settings form: closing the window keeps the app in the notification area.</summary>
    public bool CloseToTray { get => _closeToTray; set => SetForm(ref _closeToTray, value); }

    /// <summary>Settings form: no Bluetooth (testing).</summary>
    public bool DryRun { get => _dryRun; set => SetForm(ref _dryRun, value); }

    /// <summary>Settings form: the languages, "automatic" first.</summary>
    public IReadOnlyList<LanguageChoice> Languages { get; } = AppLanguage.Choices;

    /// <summary>Settings form: language of the user interface (used from the next start).</summary>
    public LanguageChoice? Language { get => _language; set => SetForm(ref _language, value ?? Languages[0]); }

    /// <summary>Settings form: the light/dark choices, "automatic" first.</summary>
    public IReadOnlyList<ThemeChoice> Themes { get; } = AppTheme.Choices;

    /// <summary>Settings form: light or dark (applied when saved).</summary>
    public ThemeChoice? Theme { get => _theme; set => SetForm(ref _theme, value ?? Themes[0]); }

    /// <summary>Saved settings took effect (the app applies the looks, such as light or dark).</summary>
    public event EventHandler? SettingsApplied;

    /// <summary>The form differs from the settings in effect.</summary>
    public bool SettingsDirty
    {
        get => _settingsDirty;
        private set
        {
            if (Set(ref _settingsDirty, value))
            {
                ApplySettingsCommand.Refresh();
                ((RelayCommand)RevertSettingsCommand).Refresh();
            }
        }
    }

    /// <summary>Starts the session with the settings in effect.</summary>
    public async Task StartAsync()
    {
        await RunBusyAsync(StartSessionAsync).ConfigureAwait(true);
    }

    /// <summary>Stops the session.</summary>
    public async Task StopAsync()
    {
        await RunBusyAsync(_mirror.StopAsync).ConfigureAwait(true);
    }

    /// <summary>Reads the session state (called by a timer).</summary>
    public void Refresh()
    {
        var s = _mirror.GetStatus();
        _status = s;
        (BleText, BleLevel) = DescribeBle(s);
        (ReceiverText, ReceiverLevel) = DescribeReceiver(s);
        var b = s.Bridge;
        IsLandscape = b.IsLandscape;
        PictureText = (b.VideoSize, b.Picture) switch
        {
            ({ } v, _) => Loc.Format(Strings.FormatPictureSize, v.Width, v.Height, v.IsLandscape ? Strings.Landscape : Strings.Portrait),
            (null, { } p) => Loc.Format(Strings.FormatPictureOnScreen, p.Width, p.Height, p.IsLandscape ? Strings.Landscape : Strings.Portrait),
            _ => "—",
        };
        var deg = (int)(s.Running ? b.LandscapeRotation : _settings.Rotation);
        RotationText = IsLandscape
            ? Loc.Format(Strings.FormatRotationLandscape, deg)
            : s.Running && b.PortraitRotation == Rotation.R180 ? Strings.RotationPortraitUpsideDown : string.Empty;
        IsRunning = s.Running;
        UpdateLicense();
        // The Store build waits for its first license read before it starts: say so instead of "Stopped".
        var checking = _license.IsChecking && !s.Running;
        (Headline, HeadlineLevel) = IsExpired ? (Strings.TrialEndedTitle, StatusLevel.Error)
            : checking ? (Strings.LicenseChecking, StatusLevel.Waiting)
            : DescribeHeadline(s);
        StatsText = Loc.Format(Strings.FormatStats, b.Gestures, b.LongPresses, b.Scrolls, b.Dropped);
        CanHome = s.Ble is BleState.Connected or BleState.DryRun;
        ((RelayCommand)ToggleRotationCommand).Refresh();
        Hint = IsExpired ? Strings.TrialEndedBody : checking ? Strings.LicenseCheckingHint : DescribeNextStep(s, _settings);
        RefreshTargets(s);
        Probe?.Refresh(s);
        Setup.Refresh(s, _settings, Probe?.TouchVerified ?? false);
    }

    /// <summary>The target line: which device gets the input and why (empty without one).</summary>
    public static string DescribeTarget(TargetChoice target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Client is not { } c)
        {
            return string.Empty;
        }

        var name = Label(c);
        return target.Reason switch
        {
            TargetReason.Chosen => Loc.Format(Strings.FormatTargetChosen, name),
            TargetReason.Mirrored => Loc.Format(Strings.FormatTargetMirrored, name),
            _ => Loc.Format(Strings.FormatTargetLatest, name),
        };
    }

    // "iPad (iPad13,1)": the name, and the model once the device told it.
    private static string Label(HidClient c) => c.Model is { } model ? $"{c.Name ?? Strings.TargetUnnamed} ({model})" : c.Name ?? Strings.TargetUnnamed;

    // The list is rebuilt only when it changes: rebuilding it resets the selection of the open list.
    private void RefreshTargets(MirrorStatus s)
    {
        List<TargetOption> options = [new(null, Strings.TargetAuto), .. s.Devices.Select(d => new TargetOption(d.Id, Label(d)))];
        if (_settings.TargetDevice is { } chosen && !s.Devices.Any(d => d.Id == chosen))
        {
            options.Add(new TargetOption(chosen, Loc.Format(Strings.FormatTargetNotConnected, _settings.TargetDeviceName ?? Strings.TargetUnnamed)));
        }

        _updatingTargets = true;
        try
        {
            if (!options.SequenceEqual(TargetOptions))
            {
                TargetOptions.Clear();
                foreach (var option in options)
                {
                    TargetOptions.Add(option);
                }
            }

            SelectedTarget = TargetOptions.FirstOrDefault(o => o.Id == _settings.TargetDevice) ?? TargetOptions[0];
        }
        finally
        {
            _updatingTargets = false;
        }

        HasDevices = s.Running && s.Devices.Count > 0;
        TargetText = DescribeTarget(s.Target);
    }

    /// <summary>Writes a line to the app log (the file and, through its event, the view).</summary>
    public void WriteLog(string line) => _log.Write(line);

    /// <summary>Adds a log line to the view (UI thread).</summary>
    public void AppendLog(string line)
    {
        LogLines.Add(line);
        while (LogLines.Count > AppLog.RecentLines)
        {
            LogLines.RemoveAt(0);
        }
    }

    /// <summary>Remembers where the window is (saved with the settings).</summary>
    public void RememberWindow(int x, int y)
    {
        var placement = new WindowPlacement(x, y);
        if (_settings.Window != placement)
        {
            _settings = _settings with { Window = placement };
            Save();
        }
    }

    /// <summary>Bluetooth line: text and colour.</summary>
    public static (string Text, StatusLevel Level) DescribeBle(MirrorStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Ble switch
        {
            BleState.Starting => (Strings.BleStarting, StatusLevel.Waiting),
            BleState.Unsupported => (Loc.Format(Strings.FormatBleUnsupported, s.BleDetail), StatusLevel.Error),
            BleState.Error => (Loc.Format(Strings.FormatError, s.BleDetail), StatusLevel.Error),
            BleState.Advertising => (Strings.BleAdvertising, StatusLevel.Waiting),
            BleState.Connected => (Strings.BleConnected, StatusLevel.Ok),
            BleState.DryRun => (Strings.BleDryRun, StatusLevel.Ok),
            _ => (Strings.Stopped, StatusLevel.Idle),
        };
    }

    /// <summary>The whole session in a few words, most blocking problem first (like <see cref="DescribeNextStep"/>).</summary>
    public static (string Text, StatusLevel Level) DescribeHeadline(MirrorStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!s.Running)
        {
            return s.Ble == BleState.Starting ? (Strings.HeadlineStarting, StatusLevel.Waiting) : (Strings.Stopped, StatusLevel.Idle);
        }

        if (s.Bridge.Suspended)
        {
            return (Strings.HeadlineSuspended, StatusLevel.Waiting);
        }

        return (s.Ble, s.Receiver) switch
        {
            (BleState.Starting, _) => (Strings.HeadlineStarting, StatusLevel.Waiting),
            (BleState.Unsupported or BleState.Error, _) => (Strings.HeadlineProblem, StatusLevel.Error),
            (_, ReceiverState.NotFound or ReceiverState.Exited or ReceiverState.Error) => (Strings.HeadlineProblem, StatusLevel.Error),
            (BleState.Advertising, _) => (Strings.HeadlineWaitingPhone, StatusLevel.Waiting),
            (_, ReceiverState.Waiting) when s.DeviceWithoutWindow => (Strings.HeadlineDeviceWithoutWindow, StatusLevel.Waiting),
            (_, ReceiverState.Waiting or ReceiverState.Off) => (Strings.HeadlineWaitingMirroring, StatusLevel.Waiting),
            (BleState.DryRun, _) => (Strings.BleDryRun, StatusLevel.Ok),
            _ => (Strings.HeadlineReady, StatusLevel.Ok),
        };
    }

    /// <summary>Receiver line: text and colour.</summary>
    public static (string Text, StatusLevel Level) DescribeReceiver(MirrorStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var name = s.ReceiverName;
        return s.Receiver switch
        {
            ReceiverState.Waiting when s.DeviceWithoutWindow => (Loc.Format(Strings.FormatReceiverDeviceWithoutWindow, name), StatusLevel.Waiting),
            ReceiverState.Waiting => (Loc.Format(Strings.FormatReceiverWaiting, name), StatusLevel.Waiting),
            ReceiverState.Showing => (s.Device is { } d ? Loc.Format(Strings.FormatReceiverShowingDevice, name, d) : Loc.Format(Strings.FormatReceiverShowing, name), StatusLevel.Ok),
            ReceiverState.NotFound => (Loc.Format(Strings.FormatReceiverNotFound, name), StatusLevel.Error),
            ReceiverState.Exited => (Loc.Format(Strings.FormatReceiverExited, name, s.ReceiverDetail), StatusLevel.Error),
            ReceiverState.Error => (Loc.Format(Strings.FormatReceiverError, name, s.ReceiverDetail), StatusLevel.Error),
            _ => (Strings.Stopped, StatusLevel.Idle),
        };
    }

    /// <summary>The one thing the user should do next, most blocking problem first.</summary>
    public static string DescribeNextStep(MirrorStatus s, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(settings);
        if (!s.Running)
        {
            return s.Ble == BleState.Starting ? Strings.HintStarting : Strings.HintStopped;
        }

        if (s.Bridge.Suspended)
        {
            return Strings.HintSuspended;
        }

        return (s.Ble, s.Receiver) switch
        {
            (BleState.Starting, _) => Strings.HintBleStarting,
            (BleState.Unsupported, _) => Strings.HintBleUnsupported,
            (BleState.Error, _) => Strings.HintBleError,
            (_, ReceiverState.NotFound) when settings.Receiver == ReceiverMode.StartUxPlay => Strings.HintUxPlayNotFound,
            (_, ReceiverState.NotFound) => Loc.Format(Strings.FormatHintProcessNotRunning, s.ReceiverName),
            (_, ReceiverState.Exited or ReceiverState.Error) => Strings.HintReceiverStopped,
            (BleState.Advertising, _) => Strings.HintPair,
            (_, ReceiverState.Waiting) when s.DeviceWithoutWindow => Strings.StepMirroringNoWindow,
            (_, ReceiverState.Waiting) when settings.Receiver == ReceiverMode.StartUxPlay =>
                Loc.Format(Strings.FormatHintMirrorUxPlay, settings.AirPlayName),
            (_, ReceiverState.Waiting) => Loc.Format(Strings.FormatHintMirrorAttach, s.ReceiverName),
            _ when s.Bridge.IsLandscape => Strings.HintReadyLandscape,
            _ => Strings.HintReady,
        };
    }

    private async Task RestartAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _mirror.StopAsync().ConfigureAwait(true);
            await StartSessionAsync().ConfigureAwait(true);
        }).ConfigureAwait(true);
    }

    // The one place a session starts (Start, Restart, auto start, a purchase that lifts the block): refused while the
    // trial has ended. The refusal is remembered so that a purchase starts the session without a restart.
    private Task StartSessionAsync()
    {
        if (!_license.Current.AllowsSession)
        {
            _blockedByLicense = true;
            _log.Write("[app ] not started: the trial has ended");
            return Task.CompletedTask;
        }

        _blockedByLicense = false;
        return _mirror.StartAsync(_settings);
    }

    // Reads the license into the status tab and the About tab (cheap: runs from Refresh). The trial line changes once
    // a minute, so it is only rebuilt when the minutes left change.
    private void UpdateLicense()
    {
        var state = _license.Current;
        var tz = _license.LocalTimeZone;
        if (state != _licenseShown)
        {
            _licenseShown = state;
            _trialMinutes = long.MinValue;
            About.ShowLicense(state, tz);
            IsTrial = state.Kind == LicenseKind.Trial;
            IsExpired = state.Kind == LicenseKind.Expired;
            if (state.Kind == LicenseKind.Purchased)
            {
                PurchaseMessage = string.Empty;
            }

            RestartCommand.Refresh();
            StartStopCommand.Refresh();
        }

        if (state is { Kind: LicenseKind.Trial, Expiration: { } end })
        {
            var minutes = (long)Math.Floor((end - _license.UtcNow).TotalMinutes);
            if (minutes != _trialMinutes)
            {
                _trialMinutes = minutes;
                TrialText = LicenseLines.TrialRemaining(end, _license.UtcNow, tz, CultureInfo.CurrentUICulture);
            }
        }
        else
        {
            TrialText = string.Empty;
        }
    }

    // The license changed (the event, the timer or a purchase): stop a running session when it ended, start the
    // one that the license had blocked when it is allowed again.
    private async Task OnLicenseChangedAsync()
    {
        UpdateLicense();
        if (_busy)
        {
            // Start or stop in progress: look again when it is done.
            _licensePending = true;
            return;
        }

        if (!_license.Current.AllowsSession)
        {
            if (_mirror.GetStatus().Running)
            {
                _log.Write("[app ] the trial has ended; stopping the session");
                _blockedByLicense = true;
                Probe?.Cancel();
                await StopAsync().ConfigureAwait(true);
            }
        }
        else if (_blockedByLicense)
        {
            _log.Write("[app ] the license allows the session again; starting");
            await StartAsync().ConfigureAwait(true);
        }

        Refresh();
    }

    private async Task BuyAsync()
    {
        // Called on the UI thread (the command), so the store's dialog is requested from it. A purchase that went
        // through shows up as a license change (Changed -> OnLicenseChangedAsync); this only words what else happened.
        PurchaseMessage = string.Empty;
        var outcome = await _license.BuyAsync().ConfigureAwait(true);
        PurchaseMessage = DescribePurchase(outcome, _license.Current);
        Refresh();
    }

    /// <summary>
    /// The word for a finished purchase request (empty = nothing to say). The user closing the dialog is theirs to
    /// decide; a bought app whose license does not say so yet is being checked; every failure gets one short message.
    /// </summary>
    public static string DescribePurchase(PurchaseOutcome outcome, LicenseState license)
    {
        ArgumentNullException.ThrowIfNull(license);
        return outcome switch
        {
            PurchaseOutcome.NotPurchased => string.Empty,
            PurchaseOutcome.Succeeded or PurchaseOutcome.AlreadyPurchased =>
                license.Kind == LicenseKind.Purchased ? string.Empty : Strings.PurchaseChecking,
            _ => Strings.PurchaseFailed,
        };
    }

    private async Task UseReceiverAsync(ReceiverCandidate candidate)
    {
        if (_busy || ProbeRunning)
        {
            _log.Write($"[app ] {candidate.Name} not chosen: {(ProbeRunning ? "a probe is running" : "the session is starting or stopping")}; try again when it is done");
            return;
        }

        _settings = SetupViewModel.Apply(_settings, candidate);
        Save();

        // Only the fields Apply changed: LoadForm would also throw away what the user typed on the Settings tab and has
        // not applied yet (review, 2026-11-07).
        LoadReceiverForm();
        if (!_mirror.GetStatus().Running)
        {
            _log.Write($"[app ] receiver set to {candidate.Name} ({candidate.Kind}, {candidate.ExePath ?? candidate.ProcessName}); it is used at the next start");
            return;
        }

        _log.Write($"[app ] receiver set to {candidate.Name} ({candidate.Kind}, {candidate.ExePath ?? candidate.ProcessName}); restarting");
        await RestartAsync().ConfigureAwait(true);
    }

    private async Task ApplySettingsAsync()
    {
        var before = _settings;
        _settings = FormToSettings();
        Save();
        LoadForm();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
        if (!AppSettings.NeedsRestart(before, _settings))
        {
            _log.Write("[app ] settings saved");
            return;
        }

        if (!_mirror.GetStatus().Running)
        {
            // The user stopped it (perhaps to let the console probe publish the HID service): keep it stopped.
            _log.Write("[app ] settings saved; they take effect at the next start");
            return;
        }

        _log.Write("[app ] settings saved; restarting");
        await RestartAsync().ConfigureAwait(true);
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        _busy = true;
        RestartCommand.Refresh();
        StartStopCommand.Refresh();
        ApplySettingsCommand.Refresh();
        try
        {
            await action().ConfigureAwait(true);
        }
#pragma warning disable CA1031 // Report any failure in the log instead of crashing the UI.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log.Write($"[app ] {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _busy = false;
            RestartCommand.Refresh();
            StartStopCommand.Refresh();
            ApplySettingsCommand.Refresh();
            Refresh();
            if (_licensePending)
            {
                _licensePending = false;
                _post(() => LicenseChange = OnLicenseChangedAsync());
            }
        }
    }

    private void OnRotationChanged(Rotation rotation)
    {
        if ((int)rotation != _settings.LandscapeRotation)
        {
            _settings = _settings with { LandscapeRotation = (int)rotation };
            Save();
        }

        Refresh();
    }

    private void Save()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Write($"[app ] cannot save the settings to {_store.Path}: {ex.Message}");
        }
    }

    private AppSettings FormToSettings() => (_settings with
    {
        AirPlayName = AirPlayName,
        Receiver = StartUxPlay ? ReceiverMode.StartUxPlay : ReceiverMode.AttachToProcess,
        UxPlayPath = UxPlayPath,
        MsysBin = MsysBin,
        AttachProcessName = AttachProcessName,
        DetectPicture = DetectPicture,
        Screen = Screen.Id,
        FollowVideoSize = FollowVideoSize,
        RightButtonLongPress = RightButtonLongPress,
        LongPressMilliseconds = (int)LongPressMilliseconds,
        WheelScroll = WheelScroll,
        WheelStepPercent = (int)WheelStepPercent,
        WheelMomentumPercent = (int)WheelMomentumPercent,
        KeyboardInput = KeyboardInput,
        VerboseLog = VerboseLog,
        CloseToTray = CloseToTray,
        DryRun = DryRun,
        Language = Language?.Code,
        Theme = Theme?.Code,
    }).Normalize();

    private void LoadForm()
    {
        var s = _settings;
        _airPlayName = s.AirPlayName;
        _startUxPlay = s.Receiver == ReceiverMode.StartUxPlay;
        _uxPlayPath = s.UxPlayPath ?? string.Empty;
        _msysBin = s.MsysBin ?? string.Empty;
        _attachProcessName = s.AttachProcessName;
        _detectPicture = s.DetectPicture;
        _screen = s.ScreenPreset;
        _followVideoSize = s.FollowVideoSize;
        _rightButtonLongPress = s.RightButtonLongPress;
        _longPressMilliseconds = s.LongPressMilliseconds;
        _wheelScroll = s.WheelScroll;
        _wheelStepPercent = s.WheelStepPercent;
        _wheelMomentumPercent = s.WheelMomentumPercent;
        _keyboardInput = s.KeyboardInput;
        _verboseLog = s.VerboseLog;
        _closeToTray = s.CloseToTray;
        _dryRun = s.DryRun;
        _language = Languages.FirstOrDefault(l => l.Code == s.Language) ?? Languages[0];
        _theme = Themes.FirstOrDefault(t => t.Code == s.Theme) ?? Themes[0];
        foreach (var name in (string[])[nameof(AirPlayName), nameof(StartUxPlay), nameof(AttachToProcess), nameof(UxPlayPath), nameof(MsysBin),
            nameof(AttachProcessName), nameof(DetectPicture), nameof(Screen), nameof(FollowVideoSize), nameof(RightButtonLongPress), nameof(LongPressMilliseconds), nameof(WheelScroll),
            nameof(WheelStepPercent), nameof(WheelMomentumPercent), nameof(KeyboardInput), nameof(VerboseLog), nameof(CloseToTray), nameof(DryRun), nameof(Language), nameof(Theme)])
        {
            OnPropertyChanged(name);
        }

        SettingsDirty = false;
    }

    // The receiver fields of the form (what SetupViewModel.Apply changes), reloaded from the settings in effect; the
    // rest of the form keeps its pending edits, and SettingsDirty is worked out again against the new settings.
    private void LoadReceiverForm()
    {
        var s = _settings;
        _startUxPlay = s.Receiver == ReceiverMode.StartUxPlay;
        _uxPlayPath = s.UxPlayPath ?? string.Empty;
        _attachProcessName = s.AttachProcessName;
        foreach (var name in (string[])[nameof(StartUxPlay), nameof(AttachToProcess), nameof(UxPlayPath), nameof(AttachProcessName)])
        {
            OnPropertyChanged(name);
        }

        SettingsDirty = FormToSettings() != _settings.Normalize();
    }

    private bool SetForm<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!Set(ref field, value, name))
        {
            return false;
        }

        SettingsDirty = FormToSettings() != _settings.Normalize();
        return true;
    }
}
