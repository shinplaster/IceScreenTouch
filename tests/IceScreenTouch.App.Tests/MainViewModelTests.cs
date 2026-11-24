// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.App.ViewModels;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Tests;

internal sealed class FakeMirror : IMirrorService
{
    public MirrorStatus Status { get; set; } = MirrorStatus.Idle;

    public List<string> Calls { get; } = [];

    public List<AppSettings> Started { get; } = [];

    public event EventHandler<Rotation>? LandscapeRotationChanged;

    public Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Calls.Add("start");
        Started.Add(settings);
        Status = Status with { Running = true };
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        Calls.Add("stop");
        Status = Status with { Running = false };
        return Task.CompletedTask;
    }

    public MirrorStatus GetStatus() => Status;

    public void Home() => Calls.Add("home");

    public void ToggleRotation() => Calls.Add("rotate");

    public void ChooseTarget(string? deviceId) => Calls.Add($"target {deviceId ?? "auto"}");

    public void RaiseRotation(Rotation r) => LandscapeRotationChanged?.Invoke(this, r);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class MainViewModelTests : IDisposable
{
    private static readonly MirrorStatus Ready = new()
    {
        Running = true,
        Ble = BleState.Connected,
        Receiver = ReceiverState.Showing,
        Bridge = BridgeStatus.Initial with { Window = 1, VideoSize = new SizeD(498, 1080), Picture = new RectD(0, 0, 277, 600) },
    };

    private readonly TempFolder _dir = new();
    private readonly FakeMirror _mirror = new();
    private readonly SettingsStore _store;
    private readonly AppLog _log = new(null);
    private readonly List<Action> _posted = [];
    private readonly MainViewModel _vm;

    public MainViewModelTests()
    {
        _store = new SettingsStore(_dir.File("settings.json"));
        _vm = new MainViewModel(_mirror, _store, _log, new AppSettings(), _posted.Add);
    }

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    private void RunPosted()
    {
        foreach (var a in _posted.ToList())
        {
            a();
        }

        _posted.Clear();
    }

    [Fact]
    public void BeforeStartingEverythingIsIdle()
    {
        Assert.Equal("停止中", _vm.BleText);
        Assert.Equal(StatusLevel.Idle, _vm.BleLevel);
        Assert.Equal("—", _vm.PictureText);
        Assert.False(_vm.HomeCommand.CanExecute(null));
        Assert.False(_vm.ToggleRotationCommand.CanExecute(null));
    }

    [Fact]
    public async Task StartRunsTheSessionWithTheSettings()
    {
        await _vm.StartAsync();

        Assert.Equal(["start"], _mirror.Calls);
        Assert.Equal(new AppSettings(), _mirror.Started[0]);
    }

    [Fact]
    public void RefreshShowsTheSessionState()
    {
        _mirror.Status = Ready with { Device = "iPhone, iPhone16,1" };

        _vm.Refresh();

        Assert.Equal("iPhone と接続済み", _vm.BleText);
        Assert.True(_vm.BleOk);
        Assert.Equal("UxPlay: 表示中（iPhone, iPhone16,1）", _vm.ReceiverText);
        Assert.True(_vm.ReceiverOk);
        Assert.Equal("498×1080（縦）", _vm.PictureText);
        Assert.Equal(string.Empty, _vm.RotationText); // no rotation in portrait
        Assert.True(_vm.HomeCommand.CanExecute(null));
        Assert.Equal(("操作できます", StatusLevel.Ok), (_vm.Headline, _vm.HeadlineLevel));
        Assert.True(_vm.HeadlineOk);
        Assert.Contains("ミラー画面をクリック", _vm.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ALandscapePictureShowsTheRotationInUse()
    {
        _mirror.Status = Ready with
        {
            Bridge = Ready.Bridge with { VideoSize = new SizeD(1920, 886), Picture = new RectD(0, 0, 400, 185), LandscapeRotation = Rotation.R270 },
        };

        _vm.Refresh();

        Assert.True(_vm.IsLandscape);
        Assert.Equal("横向き 270°", _vm.RotationText);
        Assert.True(_vm.HasRotationText);
        Assert.Equal("1920×886（横）", _vm.PictureText);
        Assert.Contains("「回転を切り替え」", _vm.Hint, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpsideDownPortraitShowsItsRotation()
    {
        _mirror.Status = Ready;
        _vm.Refresh();
        Assert.False(_vm.HasRotationText);

        _mirror.Status = Ready with { Bridge = Ready.Bridge with { PortraitRotation = Rotation.R180 } };
        _vm.Refresh();

        Assert.False(_vm.IsLandscape);
        Assert.Equal("縦向き 180°（上下逆）", _vm.RotationText);
        Assert.True(_vm.HasRotationText);
    }

    [Fact]
    public void TheTargetRowListsTheDevicesAndSaysWhichGetsTheInput()
    {
        var phone = new HidClient("ble#phone", "example15ぷろ", 1);
        var pad = new HidClient("ble#pad", null, 2);
        _mirror.Status = Ready with { Devices = [phone, pad], Target = new TargetChoice(phone, TargetReason.Mirrored) };

        _vm.Refresh();

        Assert.True(_vm.HasDevices);
        Assert.Equal(["自動", "example15ぷろ", "名前の分からない端末"], _vm.TargetOptions.Select(o => o.Label));
        Assert.Null(_vm.SelectedTarget!.Id);
        Assert.Equal("example15ぷろ に送っています（ミラー画面に映っている端末）。", _vm.TargetText);
        Assert.Empty(_mirror.Calls); // showing the list chose nothing
    }

    [Fact]
    public void ChoosingADeviceTellsTheSessionAndIsSaved()
    {
        var phone = new HidClient("ble#phone", "example15ぷろ", 1);
        var pad = new HidClient("ble#pad", "iPad", 2);
        _mirror.Status = Ready with { Devices = [phone, pad], Target = new TargetChoice(pad, TargetReason.Latest) };
        _vm.Refresh();

        _vm.SelectedTarget = _vm.TargetOptions.Single(o => o.Id == "ble#phone");

        Assert.Equal(["target ble#phone"], _mirror.Calls);
        Assert.Equal("ble#phone", _store.Load(out _).TargetDevice);
        Assert.Equal("example15ぷろ", _store.Load(out _).TargetDeviceName);

        // Gone: still listed (so the choice stays visible), marked as not connected.
        _mirror.Status = Ready with { Devices = [pad], Target = new TargetChoice(pad, TargetReason.Latest) };
        _vm.Refresh();
        Assert.Equal(["自動", "iPad", "example15ぷろ（未接続）"], _vm.TargetOptions.Select(o => o.Label));
        Assert.Equal("ble#phone", _vm.SelectedTarget!.Id);
        Assert.Equal("iPad に送っています（最後につながった端末）。", _vm.TargetText);

        _vm.SelectedTarget = _vm.TargetOptions[0];
        Assert.Equal(["target ble#phone", "target auto"], _mirror.Calls);
        Assert.Null(_store.Load(out _).TargetDevice);
        Assert.Equal(["自動", "iPad"], _vm.TargetOptions.Select(o => o.Label));
    }

    [Fact]
    public void ATargetChangeDoesNotRestartTheSession()
    {
        Assert.False(AppSettings.NeedsRestart(new AppSettings(), new AppSettings { TargetDevice = "ble#phone", TargetDeviceName = "x" }));
        Assert.Null((new AppSettings { TargetDevice = " ", TargetDeviceName = "x" }).Normalize().TargetDeviceName);
    }

    [Fact]
    public void APictureFoundInTheWindowShowsItsSizeOnScreen()
    {
        _mirror.Status = Ready with { Bridge = Ready.Bridge with { VideoSize = null, Picture = new RectD(160, 0, 3121.8, 1440) } };

        _vm.Refresh();

        Assert.True(_vm.IsLandscape);
        Assert.Equal("画面上 3122×1440（横）", _vm.PictureText);
    }

    [Fact]
    public void ButtonsGoToTheSession()
    {
        _mirror.Status = Ready;
        _vm.Refresh();

        _vm.HomeCommand.Execute(null);
        _vm.ToggleRotationCommand.Execute(null);

        Assert.Equal(["home", "rotate"], _mirror.Calls);
    }

    [Fact]
    public async Task StartStopFollowsTheSession()
    {
        Assert.Equal("開始", _vm.StartStopText);

        _vm.StartStopCommand.Execute(null);
        await _vm.StartStopCommand.Last;
        Assert.Equal("停止", _vm.StartStopText);

        _vm.StartStopCommand.Execute(null);
        await _vm.StartStopCommand.Last;
        Assert.Equal("開始", _vm.StartStopText);
        Assert.Equal(["start", "stop"], _mirror.Calls);
    }

    [Fact]
    public void OpenProbeShowsTheProbeTabOnlyWhenThereIsOne()
    {
        Assert.False(_vm.OpenProbeCommand.CanExecute(null)); // this view model has no probe

        _vm.OpenProbeCommand.Execute(null);

        Assert.Equal(MainViewModel.StatusTab, _vm.SelectedTab);
    }

    [Fact]
    public void HomeIsDisabledUntilThePhoneListens()
    {
        _mirror.Status = Ready with { Ble = BleState.Advertising };
        _vm.Refresh();
        Assert.False(_vm.HomeCommand.CanExecute(null));

        _mirror.Status = Ready with { Ble = BleState.DryRun };
        _vm.Refresh();
        Assert.True(_vm.HomeCommand.CanExecute(null));
    }

    [Fact]
    public void ARotationChangeIsSavedOnTheUiThread()
    {
        _mirror.RaiseRotation(Rotation.R270);
        Assert.Equal(90, _vm.Settings.LandscapeRotation); // not before the UI thread runs it

        RunPosted();

        Assert.Equal(270, _vm.Settings.LandscapeRotation);
        Assert.Equal(270, _store.Load(out _).LandscapeRotation);
    }

    [Fact]
    public async Task EditingTheFormMarksItDirtyAndApplyingSavesAndRestarts()
    {
        Assert.False(_vm.ApplySettingsCommand.CanExecute(null));
        await _vm.StartAsync();
        _mirror.Calls.Clear();

        _vm.AirPlayName = "Desk";
        _vm.WheelStepPercent = 14.6;
        _vm.AttachToProcess = true;
        _vm.DetectPicture = false;
        Assert.Null(_vm.Language!.Code);
        _vm.Language = _vm.Languages.Single(l => l.Code == "en");

        Assert.True(_vm.SettingsDirty);
        Assert.False(_vm.StartUxPlay);
        Assert.True(_vm.ApplySettingsCommand.CanExecute(null));

        _vm.ApplySettingsCommand.Execute(null);
        await _vm.ApplySettingsCommand.Last;

        Assert.False(_vm.SettingsDirty);
        Assert.Equal(["stop", "start"], _mirror.Calls);
        var applied = _mirror.Started[^1];
        Assert.Equal("Desk", applied.AirPlayName);
        Assert.Equal(15, applied.WheelStepPercent);
        Assert.Equal(ReceiverMode.AttachToProcess, applied.Receiver);
        Assert.False(applied.DetectPicture);
        Assert.Equal("en", applied.Language);
        Assert.Equal(applied, _store.Load(out _));
    }

    [Fact]
    public async Task ApplyingWhileStoppedSavesAndStaysStopped()
    {
        // Stopped by the user (say, for the console probe): applying must not start the session again.
        _vm.AirPlayName = "Desk";
        _vm.ApplySettingsCommand.Execute(null);
        await _vm.ApplySettingsCommand.Last;

        Assert.Empty(_mirror.Calls);
        Assert.Equal("Desk", _store.Load(out _).AirPlayName);
        Assert.Contains(_log.Recent(), l => l.Contains("next start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChoosingAReceiverKeepsTheOtherUnappliedEditsOfTheForm()
    {
        // The receiver fields follow the choice, but what the user typed elsewhere on the Settings tab stays, and the
        // form is still dirty because of it (review, 2026-11-07).
        var finder = new FakeFinder();
        finder.Candidates.Add(new ReceiverCandidate("AirServer", ReceiverCandidateKind.Attach, "AirServer", @"C:\AirServer\AirServer.exe", true));
        var posted = new List<Action>();
        var gate = new Lock();
        void Post(Action a)
        {
            lock (gate)
            {
                posted.Add(a);
            }
        }

        var vm = new MainViewModel(_mirror, _store, _log, new AppSettings(), Post, receivers: finder);
        for (var i = 0; i < 500 && vm.Setup.Receivers.Count == 0; i++)
        {
            Action[] batch;
            lock (gate)
            {
                batch = [.. posted];
                posted.Clear();
            }

            foreach (var a in batch)
            {
                a();
            }

            await Task.Delay(10);
        }

        Assert.Single(vm.Setup.Receivers);
        Assert.True(vm.StartUxPlay);
        vm.AirPlayName = "Desk";
        Assert.True(vm.SettingsDirty);

        vm.Setup.Receivers[0].UseCommand.Execute(null);

        Assert.Equal("AirServer", vm.Settings.AttachProcessName);
        Assert.Equal(ReceiverMode.AttachToProcess, vm.Settings.Receiver);
        Assert.Equal("AirServer", vm.AttachProcessName);
        Assert.True(vm.AttachToProcess);
        Assert.False(vm.StartUxPlay);
        Assert.Equal("Desk", vm.AirPlayName); // the pending edit stays
        Assert.True(vm.SettingsDirty);
        Assert.NotEqual("Desk", vm.Settings.AirPlayName); // and is not applied

        // Reverting now brings back the settings in effect, which already hold the receiver.
        vm.RevertSettingsCommand.Execute(null);
        Assert.Equal(new AppSettings().AirPlayName, vm.AirPlayName);
        Assert.Equal("AirServer", vm.AttachProcessName);
        Assert.False(vm.SettingsDirty);
    }

    [Fact]
    public async Task ChangingOnlyTheLooksSavesWithoutARestart()
    {
        var applied = 0;
        _vm.SettingsApplied += (_, _) => applied++;
        Assert.Null(_vm.Theme!.Code);

        _vm.Theme = _vm.Themes.Single(t => t.Code == AppTheme.Dark);
        _vm.CloseToTray = false;
        Assert.True(_vm.SettingsDirty);
        _vm.ApplySettingsCommand.Execute(null);
        await _vm.ApplySettingsCommand.Last;

        Assert.Equal(1, applied);
        Assert.Empty(_mirror.Calls); // Bluetooth and the receiver keep running
        Assert.Equal("dark", _store.Load(out _).Theme);
        Assert.False(_vm.SettingsDirty);
    }

    [Fact]
    public void OnlySessionSettingsNeedARestart()
    {
        var s = new AppSettings();

        Assert.False(AppSettings.NeedsRestart(s, s with { Theme = "dark", Language = "en", CloseToTray = false, Window = new WindowPlacement(1, 2) }));
        Assert.True(AppSettings.NeedsRestart(s, s with { WheelStepPercent = 9 }));
        Assert.True(AppSettings.NeedsRestart(s, s with { DryRun = true }));
        Assert.True(AppSettings.NeedsRestart(s, s with { KeyboardInput = false }));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("dark", "dark")]
    [InlineData(" Light ", "light")]
    [InlineData("blue", null)]
    public void ThemeIsNormalized(string? stored, string? expected)
    {
        Assert.Equal(expected, new AppSettings { Theme = stored }.Normalize().Theme);
    }

    [Fact]
    public void EditingBackToTheSavedValueIsNotDirty()
    {
        _vm.AirPlayName = "Desk";
        _vm.AirPlayName = "IceScreen Touch";

        Assert.False(_vm.SettingsDirty);
    }

    [Fact]
    public void RevertThrowsAwayTheFormChanges()
    {
        _vm.LongPressMilliseconds = 1234;
        Assert.Equal(1250, _vm.LongPressMilliseconds); // the slider snaps to 50 ms

        _vm.RevertSettingsCommand.Execute(null);

        Assert.Equal(600, _vm.LongPressMilliseconds);
        Assert.False(_vm.SettingsDirty);
    }

    [Fact]
    public void TheWindowPositionIsSaved()
    {
        _vm.RememberWindow(-1500, 80);

        Assert.Equal(new WindowPlacement(-1500, 80), _store.Load(out _).Window);
    }

    [Fact]
    public void LogLinesAreShownAndBounded()
    {
        for (var i = 0; i < AppLog.RecentLines + 3; i++)
        {
            _vm.AppendLog("line " + i);
        }

        Assert.Equal(AppLog.RecentLines, _vm.LogLines.Count);
        Assert.Equal("line 3", _vm.LogLines[0]);
    }

    [Fact]
    public void AFailingRestartIsLoggedNotThrown()
    {
        var vm = new MainViewModel(new ThrowingMirror(), _store, _log, new AppSettings(), _posted.Add);

        vm.RestartCommand.Execute(null);

        Assert.True(vm.RestartCommand.Last.IsCompletedSuccessfully);
        Assert.Contains(_log.Recent(), l => l.Contains("boom", StringComparison.Ordinal));
    }

    public static TheoryData<MirrorStatus, string> NextSteps => new()
    {
        { MirrorStatus.Idle, "停止中です" },
        { Ready with { Ble = BleState.Unsupported, Receiver = ReceiverState.NotFound }, "BLE ペリフェラル" },
        { Ready with { Ble = BleState.Error, Receiver = ReceiverState.NotFound }, "HID サービスを公開できません" },
        { Ready with { Ble = BleState.Advertising, Receiver = ReceiverState.NotFound }, "受信ソフトを入れて起動してください" },
        { Ready with { Ble = BleState.Advertising, Receiver = ReceiverState.Exited }, "受信ソフトが止まりました" },
        { Ready with { Ble = BleState.Advertising, Receiver = ReceiverState.Waiting }, "設定 > Bluetooth" },
        { Ready with { Receiver = ReceiverState.Waiting }, "「画面ミラーリング」で「IceScreen Touch」" },
        { Ready, "ズーム" },
    };

    [Theory]
    [MemberData(nameof(NextSteps))]
    public void TheNextStepNamesTheMostBlockingProblemFirst(MirrorStatus status, string expected)
    {
        Assert.Contains(expected, MainViewModel.DescribeNextStep(status, new AppSettings()), StringComparison.Ordinal);
    }

    public static TheoryData<MirrorStatus, string, StatusLevel> Headlines => new()
    {
        { MirrorStatus.Idle, "停止中", StatusLevel.Idle },
        { MirrorStatus.Idle with { Ble = BleState.Starting }, "起動しています…", StatusLevel.Waiting },
        { Ready with { Bridge = Ready.Bridge with { Suspended = true } }, "計測中", StatusLevel.Waiting },
        { Ready with { Ble = BleState.Error, Receiver = ReceiverState.Waiting }, "対応が必要です", StatusLevel.Error },
        { Ready with { Ble = BleState.Advertising, Receiver = ReceiverState.NotFound }, "対応が必要です", StatusLevel.Error },
        { Ready with { Ble = BleState.Advertising, Receiver = ReceiverState.Waiting }, "iPhone の接続を待っています", StatusLevel.Waiting },
        { Ready with { Receiver = ReceiverState.Waiting }, "画面ミラーリングを待っています", StatusLevel.Waiting },
        { Ready with { Ble = BleState.DryRun }, "テスト用（Bluetooth を使わない）", StatusLevel.Ok },
        { Ready, "操作できます", StatusLevel.Ok },
    };

    [Theory]
    [MemberData(nameof(Headlines))]
    public void TheHeadlineSaysTheMostBlockingStateFirst(MirrorStatus status, string text, StatusLevel level)
    {
        Assert.Equal((text, level), MainViewModel.DescribeHeadline(status));
    }

    [Fact]
    public void WithoutAReceiverSetUpTheStatusAsksToInstallOneInsteadOfAnError()
    {
        // The defaults on a PC without UxPlay (every new Store install): a request to install a receiver, not "Needs attention".
        var status = Ready with { Ble = BleState.Advertising, Receiver = ReceiverState.NotFound };

        Assert.Equal(("AirPlay の受信ソフトを入れてください", StatusLevel.Waiting), MainViewModel.DescribeHeadline(status, new AppSettings()));
        var hint = MainViewModel.DescribeNextStep(status, new AppSettings());
        Assert.Contains("ユーザーガイド", hint, StringComparison.Ordinal);
        Assert.DoesNotContain("uxplay", hint, StringComparison.OrdinalIgnoreCase);

        // A path that was entered and is missing stays an error, and Bluetooth problems still come first.
        var entered = new AppSettings { UxPlayPath = @"C:\gone\uxplay.exe" };
        Assert.Equal(("対応が必要です", StatusLevel.Error), MainViewModel.DescribeHeadline(status, entered));
        Assert.Contains("uxplay.exe が見つかりません", MainViewModel.DescribeNextStep(status, entered), StringComparison.Ordinal);
        Assert.Equal(("対応が必要です", StatusLevel.Error), MainViewModel.DescribeHeadline(status with { Ble = BleState.Unsupported }, new AppSettings()));
    }

    [Fact]
    public async Task AReceiverFoundLaterIsPickedWhileNoneIsSetUp()
    {
        // The first run found nothing (the settings file exists since); the user installs a receiver afterwards.
        var finder = new FakeFinder();
        finder.Candidates.Add(new ReceiverCandidate("AirServer", ReceiverCandidateKind.Attach, "AirServer", @"C:\AirServer\AirServer.exe", false));
        var posted = new List<Action>();
        var gate = new Lock();
        void Post(Action a)
        {
            lock (gate)
            {
                posted.Add(a);
            }
        }

        _mirror.Status = Ready with { Receiver = ReceiverState.NotFound, Running = false };
        var vm = new MainViewModel(_mirror, _store, _log, new AppSettings(), Post, receivers: finder);
        await vm.StartAsync();
        for (var i = 0; i < 500 && _mirror.Calls.Count(c => c == "start") < 2; i++)
        {
            Action[] batch;
            lock (gate)
            {
                batch = [.. posted];
                posted.Clear();
            }

            foreach (var a in batch)
            {
                a();
            }

            vm.Refresh();
            await Task.Delay(10);
        }

        Assert.Equal(ReceiverMode.AttachToProcess, vm.Settings.Receiver);
        Assert.Equal("AirServer", vm.Settings.AttachProcessName);
        Assert.Equal("AirServer", _store.Load(out _).AttachProcessName);
        Assert.Contains(_log.Recent(), l => l.Contains("no receiver set up yet; using AirServer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoReceiverIsPickedOverTheOneTheUserApplied()
    {
        var finder = new FakeFinder();
        finder.Candidates.Add(new ReceiverCandidate("AirServer", ReceiverCandidateKind.Attach, "AirServer", @"C:\AirServer\AirServer.exe", true));
        var posted = new List<Action>();
        var vm = new MainViewModel(_mirror, _store, _log, new AppSettings { Receiver = ReceiverMode.AttachToProcess, AttachProcessName = "AirServer" }, posted.Add, receivers: finder);

        // The user switches to "Start UxPlay" without a path on a PC without UxPlay.
        vm.StartUxPlay = true;
        vm.ApplySettingsCommand.Execute(null);
        await vm.ApplySettingsCommand.Last;
        _mirror.Status = Ready with { Receiver = ReceiverState.NotFound };
        for (var i = 0; i < 100 && vm.Setup.Receivers.Count == 0; i++)
        {
            foreach (var a in posted.ToList())
            {
                a();
            }

            posted.Clear();
            await Task.Delay(10);
        }

        vm.Refresh();

        Assert.Single(vm.Setup.Receivers);
        Assert.Equal(ReceiverMode.StartUxPlay, vm.Settings.Receiver);
        Assert.Equal("AirPlay の受信ソフトを入れてください", vm.Headline);
    }

    [Fact]
    public void AFollowedReceiverThatIsNotRunningIsAskedToBeStartedNotAnError()
    {
        // AirServer installed and chosen (or picked by itself) but not started: yellow guidance, the next step is "Start" in the list.
        var status = Ready with { Ble = BleState.Advertising, Receiver = ReceiverState.NotFound, ReceiverName = "AirServer" };
        var settings = new AppSettings { Receiver = ReceiverMode.AttachToProcess, AttachProcessName = "AirServer" };

        Assert.Equal(("AirPlay の受信ソフトを起動してください", StatusLevel.Waiting), MainViewModel.DescribeHeadline(status, settings));
        Assert.Contains("AirServer が動いていません", MainViewModel.DescribeNextStep(status, settings), StringComparison.Ordinal);
        Assert.Contains("下の一覧から起動", MainViewModel.DescribeNextStep(status, settings), StringComparison.Ordinal);
        Assert.Equal(StatusLevel.Waiting, MainViewModel.DescribeReceiver(status, settings).Level);

        // Real errors stay red: Bluetooth first, a receiver that the app started and that exited, an entered uxplay.exe that is not there.
        Assert.Equal(StatusLevel.Error, MainViewModel.DescribeHeadline(status with { Ble = BleState.Error }, settings).Level);
        Assert.Equal(("対応が必要です", StatusLevel.Error), MainViewModel.DescribeHeadline(status with { Receiver = ReceiverState.Exited }, new AppSettings()));
        var entered = new AppSettings { UxPlayPath = @"C:\gone\uxplay.exe" };
        Assert.Equal(StatusLevel.Error, MainViewModel.DescribeHeadline(status, entered).Level);
        Assert.Equal(StatusLevel.Error, MainViewModel.DescribeReceiver(status, entered).Level);

        // Without the settings (older callers) it stays as it was.
        Assert.Equal(StatusLevel.Error, MainViewModel.DescribeHeadline(status).Level);
    }

    [Fact]
    public void TheStatusTabShowsTheStartStepForAChosenReceiverThatIsNotRunning()
    {
        var vm = new MainViewModel(_mirror, _store, _log, new AppSettings { Receiver = ReceiverMode.AttachToProcess, AttachProcessName = "AirServer" }, _posted.Add);
        _mirror.Status = Ready with { Receiver = ReceiverState.NotFound, ReceiverName = "AirServer" };

        vm.Refresh();

        Assert.Equal("AirPlay の受信ソフトを起動してください", vm.Headline);
        Assert.True(vm.HeadlineWaiting);
        Assert.Equal(StatusLevel.Waiting, vm.Setup.Steps[3].Level);
        Assert.True(vm.ReceiverWaiting);
    }

    [Fact]
    public void AMissingAttachedReceiverIsNamed()
    {
        var hint = MainViewModel.DescribeNextStep(
            Ready with { Receiver = ReceiverState.NotFound, ReceiverName = "5KPlayer" },
            new AppSettings { Receiver = ReceiverMode.AttachToProcess, AttachProcessName = "Airplay" });

        Assert.Contains("5KPlayer が動いていません", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceWithoutAFoundWindowIsSaidTheSameInTheHeadlineTheReceiverLineAndTheHint()
    {
        var status = Ready with { Receiver = ReceiverState.Waiting, ReceiverName = "5KPlayer", DeviceWithoutWindow = true, Bridge = BridgeStatus.Initial };
        var settings = new AppSettings { Receiver = ReceiverMode.AttachToProcess, AttachProcessName = "Airplay" };

        var headline = MainViewModel.DescribeHeadline(status);
        var receiver = MainViewModel.DescribeReceiver(status);
        var hint = MainViewModel.DescribeNextStep(status, settings);

        Assert.Equal(StatusLevel.Waiting, headline.Level);
        Assert.Contains("見つかりません", headline.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("待って", headline.Text, StringComparison.Ordinal);
        Assert.Contains("5KPlayer", receiver.Text, StringComparison.Ordinal);
        Assert.Contains("見つかりません", receiver.Text, StringComparison.Ordinal);
        Assert.Contains("機器がつながっていますが", hint, StringComparison.Ordinal);

        // Without a connection it is the usual wait for the iPhone.
        var waiting = status with { DeviceWithoutWindow = false };
        Assert.Contains("待って", MainViewModel.DescribeHeadline(waiting).Text, StringComparison.Ordinal);
        Assert.Contains("5KPlayer の項目", MainViewModel.DescribeNextStep(waiting, settings), StringComparison.Ordinal);
    }

    private sealed class ThrowingMirror : IMirrorService
    {
        public event EventHandler<Rotation>? LandscapeRotationChanged
        {
            add { }
            remove { }
        }

        public Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new InvalidOperationException("boom");

        public Task StopAsync() => Task.CompletedTask;

        public MirrorStatus GetStatus() => MirrorStatus.Idle;

        public void Home()
        {
        }

        public void ToggleRotation()
        {
        }

        public void ChooseTarget(string? deviceId)
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
