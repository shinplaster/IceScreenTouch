using System.Collections.ObjectModel;
using System.Windows.Input;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;

namespace IceScreenTouch.App.ViewModels;

/// <summary>One step of the first-time setup and where it stands.</summary>
/// <param name="Title">What the step is.</param>
/// <param name="Level">Done (<see cref="StatusLevel.Ok"/>), to do, failed, or not known (<see cref="StatusLevel.Idle"/>).</param>
/// <param name="Detail">What to do, or what was found.</param>
/// <param name="Number">Place in the list, from 1.</param>
/// <param name="OffersProbe">The step is checked with the touch probe (it shows a way to the probe tab).</param>
public sealed record SetupStep(string Title, StatusLevel Level, string Detail, int Number = 0, bool OffersProbe = false)
{
    /// <summary>The number as shown.</summary>
    public string NumberText => Number.ToString(System.Globalization.CultureInfo.CurrentCulture) + ".";

    /// <summary>Not done yet: the detail says what to do and gets a line of its own.</summary>
    public bool IsOpen => Level != StatusLevel.Ok;

    /// <summary>For the view's style classes.</summary>
    public bool IsOk => Level == StatusLevel.Ok;

    /// <inheritdoc cref="IsOk"/>
    public bool IsWaiting => Level == StatusLevel.Waiting;

    /// <inheritdoc cref="IsOk"/>
    public bool IsError => Level == StatusLevel.Error;
}

/// <summary>A receiver the user can pick in the status tab.</summary>
public sealed class ReceiverChoice
{
    internal ReceiverChoice(ReceiverCandidate candidate, bool isCurrent, ICommand use, ICommand launch)
    {
        Candidate = candidate;
        IsCurrent = isCurrent;
        UseCommand = use;
        LaunchCommand = launch;
    }

    /// <summary>What was found.</summary>
    public ReceiverCandidate Candidate { get; }

    /// <summary>Name for the user.</summary>
    public string Name => Candidate.Name;

    /// <summary>Running, installed, or where the program is.</summary>
    public string Detail => Candidate switch
    {
        { Kind: ReceiverCandidateKind.StartUxPlay } c => c.ExePath ?? string.Empty,
        { Running: true } => Strings.ReceiverRunning,
        _ => Strings.ReceiverInstalled,
    };

    /// <summary>The settings use it now.</summary>
    public bool IsCurrent { get; }

    /// <summary>"In use" or "Use".</summary>
    public string UseText => IsCurrent ? Strings.ReceiverInUse : Strings.ReceiverUse;

    /// <summary>It can be started from here (installed, not running, followed rather than started by the app).</summary>
    public bool CanLaunch => Candidate is { Kind: ReceiverCandidateKind.Attach, Running: false, ExePath: not null };

    /// <summary>Switch the settings to this receiver and restart the session.</summary>
    public ICommand UseCommand { get; }

    /// <summary>Start the receiver.</summary>
    public ICommand LaunchCommand { get; }
}

/// <summary>
/// The setup part of the status tab: the steps from a new PC to a working mirror (Bluetooth adapter, pairing,
/// Zoom, receiver, screen mirroring) with what to do next, and the AirPlay receivers found on this PC. UI-thread only;
/// <paramref name="post"/> brings work from other threads to it.
/// </summary>
public sealed class SetupViewModel : ObservableObject
{
    private const long SearchIntervalMs = 5000;

    private readonly IReceiverFinder? _finder;
    private readonly Action<Action> _post;
    private readonly Func<ReceiverCandidate, Task> _use;
    private readonly string _pcName;
    private IReadOnlyList<ReceiverCandidate> _candidates = [];
    private AppSettings _settings = new();
    private bool _searching;
    private bool _allDone;
    private string _progressText = string.Empty;
    private bool _receiversExpanded;
    private bool? _receiverOk;
    private long _searchedAt;

    /// <summary>Creates the steps and the list. <paramref name="use"/> switches the settings to a receiver and restarts.</summary>
    public SetupViewModel(IReceiverFinder? finder, Action<Action> post, Func<ReceiverCandidate, Task> use, string? pcName = null)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(use);
        _finder = finder;
        _post = post;
        _use = use;
        _pcName = string.IsNullOrWhiteSpace(pcName) ? Environment.MachineName : pcName;
        SearchCommand = new RelayCommand(Search, () => _finder is not null && !_searching);
        Search();
    }

    /// <summary>The steps.</summary>
    public ObservableCollection<SetupStep> Steps { get; } = [];

    /// <summary>The receivers found.</summary>
    public ObservableCollection<ReceiverChoice> Receivers { get; } = [];

    /// <summary>The platform can look for receivers.</summary>
    public bool CanSearch => _finder is not null;

    /// <summary>Nothing was found.</summary>
    public bool NoReceivers => _finder is not null && !_searching && Receivers.Count == 0;

    /// <summary>Every step that can be checked is done.</summary>
    public bool AllDone { get => _allDone; private set => Set(ref _allDone, value); }

    /// <summary>How many steps are done, as shown next to the heading.</summary>
    public string ProgressText { get => _progressText; private set => Set(ref _progressText, value); }

    /// <summary>
    /// The receiver list is open. It starts closed and opens by itself when the receiver step comes to need the
    /// user; otherwise the user's choice stays.
    /// </summary>
    public bool ReceiversExpanded { get => _receiversExpanded; set => Set(ref _receiversExpanded, value); }

    /// <summary>Look for receivers again.</summary>
    public ICommand SearchCommand { get; }

    /// <summary>Takes the session state (called by the status timer).</summary>
    public void Refresh(MirrorStatus status, AppSettings settings, bool touchVerified)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(settings);
        var settingsChanged = settings != _settings;
        _settings = settings;
        var steps = BuildSteps(status, settings, touchVerified, _pcName);
        if (!steps.SequenceEqual(Steps))
        {
            Steps.Clear();
            foreach (var s in steps)
            {
                Steps.Add(s);
            }
        }

        AllDone = steps.All(s => s.Level == StatusLevel.Ok);
        ProgressText = Loc.Format(Strings.FormatSetupProgress, steps.Count(s => s.Level == StatusLevel.Ok), steps.Count);

        // Open the list when the receiver needs the user; a session that is not running says nothing about it.
        if (status.Running && status.Ble != BleState.Starting)
        {
            var receiverOk = status.Receiver is ReceiverState.Waiting or ReceiverState.Showing;
            if (receiverOk != _receiverOk && !receiverOk)
            {
                ReceiversExpanded = true;
            }

            _receiverOk = receiverOk;
        }
        if (settingsChanged)
        {
            ShowCandidates();
        }

        // Receivers start and stop: look again now and then.
        if (Environment.TickCount64 - _searchedAt > SearchIntervalMs)
        {
            Search();
        }
    }

    /// <summary>The steps for a session state, in order. Zoom cannot be checked; a passed touch probe proves it.</summary>
    public static IReadOnlyList<SetupStep> BuildSteps(MirrorStatus s, AppSettings settings, bool touchVerified, string pcName)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(settings);
        var adapterTitle = Strings.StepAdapter;
        var adapter = s.Ble switch
        {
            BleState.Advertising or BleState.Connected => new SetupStep(adapterTitle, StatusLevel.Ok, Strings.StepAdapterOk),
            BleState.Unsupported => new SetupStep(adapterTitle, StatusLevel.Error, Loc.Format(Strings.FormatStepAdapterUnsupported, s.BleDetail)),
            BleState.Error => new SetupStep(adapterTitle, StatusLevel.Error, Loc.Format(Strings.FormatStepAdapterError, s.BleDetail)),
            BleState.DryRun => new SetupStep(adapterTitle, StatusLevel.Waiting, Strings.StepAdapterDryRun),
            BleState.Starting => new SetupStep(adapterTitle, StatusLevel.Waiting, Strings.StepChecking),
            _ => new SetupStep(adapterTitle, StatusLevel.Idle, Strings.StepAdapterStopped),
        };
        var pairingTitle = Strings.StepPairing;
        var pairing = s.Ble switch
        {
            BleState.Connected => new SetupStep(pairingTitle, StatusLevel.Ok, Strings.StepPairingOk),
            BleState.Advertising => new SetupStep(pairingTitle, StatusLevel.Waiting, Loc.Format(Strings.FormatStepPairingWaiting, pcName)),
            _ => new SetupStep(pairingTitle, StatusLevel.Idle, Strings.StepPairingIdle),
        };
        var zoom = touchVerified
            ? new SetupStep(Strings.StepZoom, StatusLevel.Ok, Strings.StepZoomOk)
            : new SetupStep(Strings.StepZoom, StatusLevel.Idle, Strings.StepZoomTodo, OffersProbe: true);
        var name = s.ReceiverName;
        var receiverTitle = Strings.StepReceiver;
        var receiver = s.Receiver switch
        {
            ReceiverState.Waiting or ReceiverState.Showing => new SetupStep(receiverTitle, StatusLevel.Ok, Loc.Format(Strings.FormatStepReceiverOk, name)),
            ReceiverState.NotFound when settings.Receiver == ReceiverMode.StartUxPlay =>
                new SetupStep(receiverTitle, StatusLevel.Error, Strings.StepReceiverUxPlayNotFound),
            ReceiverState.NotFound =>
                new SetupStep(receiverTitle, StatusLevel.Error, Loc.Format(Strings.FormatStepReceiverNotRunning, name)),
            ReceiverState.Exited or ReceiverState.Error =>
                new SetupStep(receiverTitle, StatusLevel.Error, Loc.Format(Strings.FormatStepReceiverStopped, name, s.ReceiverDetail)),
            _ => new SetupStep(receiverTitle, StatusLevel.Idle, Strings.StepStopped),
        };
        var item = settings.Receiver == ReceiverMode.StartUxPlay
            ? Loc.Format(Strings.FormatMirrorItemUxPlay, settings.AirPlayName)
            : Loc.Format(Strings.FormatMirrorItemAttach, name);
        var mirroringTitle = Strings.StepMirroring;
        var mirroring = s.Receiver switch
        {
            ReceiverState.Showing => new SetupStep(mirroringTitle, StatusLevel.Ok,
                s.Device is { } d ? Loc.Format(Strings.FormatStepMirroringOkDevice, d) : Strings.StepMirroringOk),
            ReceiverState.Waiting when s.DeviceWithoutWindow => new SetupStep(mirroringTitle, StatusLevel.Waiting, Strings.StepMirroringNoWindow),
            ReceiverState.Waiting => new SetupStep(mirroringTitle, StatusLevel.Waiting, Loc.Format(Strings.FormatStepMirroringWaiting, item)),
            _ => new SetupStep(mirroringTitle, StatusLevel.Idle, Strings.StepMirroringIdle),
        };
        return [.. ((SetupStep[])[adapter, pairing, zoom, receiver, mirroring]).Select((step, i) => step with { Number = i + 1 })];
    }

    /// <summary>
    /// The receiver to start with on a new PC: one that runs now, else the local uxplay build, else an installed
    /// one. Null when nothing was found (the settings stay as they are).
    /// </summary>
    public static ReceiverCandidate? ChooseFirst(IReadOnlyList<ReceiverCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.FirstOrDefault(c => c.Running && c.Kind == ReceiverCandidateKind.Attach)
            ?? candidates.FirstOrDefault(c => c.Kind == ReceiverCandidateKind.StartUxPlay)
            ?? (candidates.Count > 0 ? candidates[0] : null);
    }

    /// <summary>The settings that use <paramref name="candidate"/>.</summary>
    public static AppSettings Apply(AppSettings settings, ReceiverCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Kind == ReceiverCandidateKind.StartUxPlay
            ? settings with { Receiver = ReceiverMode.StartUxPlay, UxPlayPath = candidate.ExePath }
            : settings with { Receiver = ReceiverMode.AttachToProcess, AttachProcessName = candidate.ProcessName };
    }

    /// <summary>Whether <paramref name="settings"/> use <paramref name="candidate"/>.</summary>
    public static bool Uses(AppSettings settings, ReceiverCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.Kind == ReceiverCandidateKind.StartUxPlay
            ? settings.Receiver == ReceiverMode.StartUxPlay
                && (settings.UxPlayPath is null || string.Equals(settings.UxPlayPath, candidate.ExePath, StringComparison.OrdinalIgnoreCase))
            : settings.Receiver == ReceiverMode.AttachToProcess
                && string.Equals(settings.AttachProcessName, candidate.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private void Search()
    {
        if (_finder is not { } finder || _searching)
        {
            return;
        }

        _searching = true;
        _searchedAt = Environment.TickCount64;
        ((RelayCommand)SearchCommand).Refresh();
        OnPropertyChanged(nameof(NoReceivers));
        _ = Task.Run(() =>
        {
            IReadOnlyList<ReceiverCandidate> found;
            try
            {
                found = finder.Find();
            }
#pragma warning disable CA1031 // Nothing found is shown as such.
            catch (Exception)
#pragma warning restore CA1031
            {
                found = [];
            }

            _post(() =>
            {
                _candidates = found;
                _searching = false;
                ((RelayCommand)SearchCommand).Refresh();
                ShowCandidates();
            });
        });
    }

    private void ShowCandidates()
    {
        Receivers.Clear();
        foreach (var c in _candidates)
        {
            var current = Uses(_settings, c);
            Receivers.Add(new ReceiverChoice(
                c,
                current,
                new RelayCommand(() => _ = _use(c), () => !current),
                new RelayCommand(() => Launch(c))));
        }

        OnPropertyChanged(nameof(NoReceivers));
    }

    private void Launch(ReceiverCandidate candidate)
    {
        if (_finder is not { } finder)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            if (finder.Launch(candidate))
            {
                // Give it a moment to show up before looking again.
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }

            _post(Search);
        });
    }
}
