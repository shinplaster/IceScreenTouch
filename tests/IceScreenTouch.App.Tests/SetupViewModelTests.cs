// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.App.ViewModels;

namespace IceScreenTouch.App.Tests;

internal sealed class FakeFinder : IReceiverFinder
{
    public List<ReceiverCandidate> Candidates { get; } = [];

    public List<ReceiverCandidate> Launched { get; } = [];

    public IReadOnlyList<ReceiverCandidate> Find() => [.. Candidates];

    public bool Launch(ReceiverCandidate candidate)
    {
        Launched.Add(candidate);
        return false;
    }
}

public sealed class SetupViewModelTests : IDisposable
{
    private static readonly ReceiverCandidate AirServerRunning = new("AirServer", ReceiverCandidateKind.Attach, "AirServer", @"C:\Program Files\App Dynamic\AirServer\AirServer.exe", true);
    private static readonly ReceiverCandidate AirServerInstalled = AirServerRunning with { Running = false };
    private static readonly ReceiverCandidate LocalUxPlay = new("UxPlay", ReceiverCandidateKind.StartUxPlay, "uxplay", @"C:\src\poc1-uxplay\UxPlay\build-isr\uxplay.exe", false);

    private static readonly MirrorStatus Working = new()
    {
        Running = true,
        Ble = BleState.Connected,
        Receiver = ReceiverState.Showing,
        ReceiverName = "AirServer",
        Device = "iPhone (iPhone16,1)",
    };

    private readonly TempFolder _dir = new();
    private readonly FakeMirror _mirror = new();
    private readonly FakeFinder _finder = new();
    private readonly AppLog _log = new(null);
    private readonly List<Action> _posted = [];
    private readonly Lock _lock = new();

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    private static IReadOnlyList<SetupStep> Steps(MirrorStatus s, AppSettings? settings = null, bool verified = false) =>
        SetupViewModel.BuildSteps(s, settings ?? new AppSettings(), verified, "MYPC");

    [Fact]
    public void AWorkingSessionWithAVerifiedTouchIsAllDone()
    {
        var steps = Steps(Working, verified: true);

        Assert.Equal(["Bluetooth アダプタ", "iPhone と接続（Bluetooth）", "iPhone のズーム", "受信ソフト（AirPlay）", "画面ミラーリング"], steps.Select(s => s.Title));
        Assert.All(steps, s => Assert.Equal(StatusLevel.Ok, s.Level));
        Assert.Equal(["1.", "2.", "3.", "4.", "5."], steps.Select(s => s.NumberText));
        Assert.DoesNotContain(steps, s => s.IsOpen);
        Assert.Contains("iPhone16,1", steps[4].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheProgressCountsTheDoneSteps()
    {
        var vm = new SetupViewModel(null, Post, _ => Task.CompletedTask, "MYPC");

        vm.Refresh(Working, new AppSettings(), touchVerified: false);

        Assert.Equal("5 項目中 4 項目 済み", vm.ProgressText);
        Assert.True(vm.Steps[2].IsOpen);
        Assert.True(vm.Steps[2].OffersProbe);

        vm.Refresh(Working, new AppSettings(), touchVerified: true);
        Assert.Equal("5 項目中 5 項目 済み", vm.ProgressText);
        Assert.DoesNotContain(vm.Steps, s => s.OffersProbe);
    }

    [Fact]
    public void TheReceiverListOpensWhenTheReceiverNeedsTheUser()
    {
        var vm = new SetupViewModel(null, Post, _ => Task.CompletedTask, "MYPC");
        var settings = new AppSettings();

        vm.Refresh(Working, settings, false);
        Assert.False(vm.ReceiversExpanded); // a working receiver keeps the list closed

        vm.Refresh(Working with { Receiver = ReceiverState.NotFound }, settings, false);
        Assert.True(vm.ReceiversExpanded);

        vm.ReceiversExpanded = false; // the user closes it: it stays closed while nothing changes
        vm.Refresh(Working with { Receiver = ReceiverState.NotFound }, settings, false);
        Assert.False(vm.ReceiversExpanded);

        vm.Refresh(MirrorStatus.Idle, settings, false); // a stopped session says nothing about the receiver
        Assert.False(vm.ReceiversExpanded);

        vm.Refresh(Working, settings, false);
        vm.Refresh(Working with { Receiver = ReceiverState.Exited }, settings, false);
        Assert.True(vm.ReceiversExpanded);
    }

    [Fact]
    public void ZoomCannotBeCheckedUntilATouchProbePasses()
    {
        var zoom = Steps(Working)[2];

        Assert.Equal(StatusLevel.Idle, zoom.Level);
        Assert.Contains("アクセシビリティ > ズーム", zoom.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AdvertisingAsksToPickThisPcInBluetoothSettings()
    {
        var pairing = Steps(Working with { Ble = BleState.Advertising })[1];

        Assert.Equal(StatusLevel.Waiting, pairing.Level);
        Assert.Contains("「MYPC」", pairing.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAdapterThatCannotBeAPeripheralIsAnError()
    {
        var steps = Steps(Working with { Ble = BleState.Unsupported, BleDetail = "peripheral role supported = False" });

        Assert.Equal(StatusLevel.Error, steps[0].Level);
        Assert.Equal(StatusLevel.Idle, steps[1].Level);
    }

    [Theory]
    [InlineData(ReceiverMode.StartUxPlay, "uxplay.exe が見つかりません")]
    [InlineData(ReceiverMode.AttachToProcess, "AirServer が動いていません")]
    public void AMissingReceiverPointsAtTheList(ReceiverMode mode, string expected)
    {
        var settings = new AppSettings { Receiver = mode, AttachProcessName = "AirServer" };
        var steps = Steps(Working with { Receiver = ReceiverState.NotFound }, settings);

        Assert.Equal(StatusLevel.Error, steps[3].Level);
        Assert.Contains(expected, steps[3].Detail, StringComparison.Ordinal);
        Assert.Equal(StatusLevel.Idle, steps[4].Level);
    }

    [Fact]
    public void WaitingForMirroringNamesTheItemToPick()
    {
        var uxplay = Steps(Working with { Receiver = ReceiverState.Waiting }, new AppSettings { AirPlayName = "Desk" })[4];
        var attached = Steps(Working with { Receiver = ReceiverState.Waiting }, new AppSettings { Receiver = ReceiverMode.AttachToProcess })[4];

        Assert.Equal(StatusLevel.Waiting, uxplay.Level);
        Assert.Contains("「Desk」", uxplay.Detail, StringComparison.Ordinal);
        Assert.Contains("AirServer の項目", attached.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceConnectedWithoutAFoundWindowSaysSoInsteadOfAskingToSelectTheReceiver()
    {
        var settings = new AppSettings { Receiver = ReceiverMode.AttachToProcess };
        var step = Steps(Working with { Receiver = ReceiverState.Waiting, DeviceWithoutWindow = true }, settings)[4];

        Assert.Equal(StatusLevel.Waiting, step.Level);
        Assert.Contains("機器がつながっていますが", step.Detail, StringComparison.Ordinal);
        Assert.Contains("最小化", step.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("コントロールセンター", step.Detail, StringComparison.Ordinal);

        // A window that shows is not "without a window".
        Assert.Equal(StatusLevel.Ok, Steps(Working with { DeviceWithoutWindow = true }, settings)[4].Level);
    }

    [Fact]
    public void ReceiverShownUnderItsOwnNameIsFollowedByItsProcess()
    {
        // 5KPlayer: the list says "5KPlayer", the process that shows the picture is Airplay.exe.
        var fiveK = new ReceiverCandidate("5KPlayer", ReceiverCandidateKind.Attach, "Airplay", @"C:Program Files (x86)DearMobKPlayerKPlayer.exe", true);

        var settings = SetupViewModel.Apply(new AppSettings(), fiveK);

        Assert.Equal((ReceiverMode.AttachToProcess, "Airplay"), (settings.Receiver, settings.AttachProcessName));
        Assert.True(SetupViewModel.Uses(settings, fiveK));
        Assert.False(SetupViewModel.Uses(settings, AirServerRunning));
    }

    [Fact]
    public void TheNotRunningStepNamesTheReceiverAsTheListDoes()
    {
        var step = Steps(Working with { Receiver = ReceiverState.NotFound, ReceiverName = "5KPlayer" }, new AppSettings { Receiver = ReceiverMode.AttachToProcess, AttachProcessName = "Airplay" })[3];

        Assert.Contains("5KPlayer が動いていません", step.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Airplay", step.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstReceiverIsARunningOneThenTheLocalUxPlayThenAnInstalledOne()
    {
        Assert.Equal(AirServerRunning, SetupViewModel.ChooseFirst([LocalUxPlay, AirServerRunning]));
        Assert.Equal(LocalUxPlay, SetupViewModel.ChooseFirst([AirServerInstalled, LocalUxPlay]));
        Assert.Equal(AirServerInstalled, SetupViewModel.ChooseFirst([AirServerInstalled]));
        Assert.Null(SetupViewModel.ChooseFirst([]));
    }

    [Fact]
    public void ApplyingACandidateChangesOnlyTheReceiverSettings()
    {
        var before = new AppSettings { WheelStepPercent = 9 };

        var attached = SetupViewModel.Apply(before, AirServerRunning);
        var started = SetupViewModel.Apply(attached, LocalUxPlay);

        Assert.Equal((ReceiverMode.AttachToProcess, "AirServer", 9), (attached.Receiver, attached.AttachProcessName, attached.WheelStepPercent));
        Assert.True(SetupViewModel.Uses(attached, AirServerInstalled));
        Assert.False(SetupViewModel.Uses(attached, LocalUxPlay));
        Assert.Equal((ReceiverMode.StartUxPlay, LocalUxPlay.ExePath), (started.Receiver, started.UxPlayPath));
        Assert.True(SetupViewModel.Uses(started, LocalUxPlay));
        Assert.True(SetupViewModel.Uses(new AppSettings(), LocalUxPlay)); // no path = the local build
    }

    [Fact]
    public async Task UsingAReceiverSavesTheSettingsAndRestarts()
    {
        _finder.Candidates.AddRange([AirServerRunning, LocalUxPlay]);
        var store = new SettingsStore(_dir.File("settings.json"));
        var vm = new MainViewModel(_mirror, store, _log, new AppSettings(), Post, receivers: _finder);
        await RunPostedUntilAsync(() => vm.Setup.Receivers.Count == 2);
        await _mirror.StartAsync(vm.Settings); // running: "use" restarts it (stopped, it would only save)
        _mirror.Calls.Clear();
        _mirror.Started.Clear();

        var air = vm.Setup.Receivers.Single(r => r.Name == "AirServer");
        var ux = vm.Setup.Receivers.Single(r => r.Name == "UxPlay");
        Assert.True(ux.IsCurrent); // the defaults start the local uxplay
        Assert.False(ux.UseCommand.CanExecute(null));
        Assert.Equal("使う", air.UseText);
        Assert.False(air.CanLaunch); // running already

        air.UseCommand.Execute(null);
        await RunPostedUntilAsync(() => _mirror.Started.Count > 0);

        Assert.Equal(ReceiverMode.AttachToProcess, vm.Settings.Receiver);
        Assert.Equal("AirServer", vm.Settings.AttachProcessName);
        Assert.Equal("AirServer", store.Load(out _).AttachProcessName);
        Assert.Equal(["stop", "start"], _mirror.Calls);

        vm.Refresh();
        Assert.True(vm.Setup.Receivers.Single(r => r.Name == "AirServer").IsCurrent);
    }

    [Fact]
    public async Task AnInstalledReceiverCanBeStartedAndNothingFoundIsSaid()
    {
        _finder.Candidates.Add(AirServerInstalled);
        var vm = new MainViewModel(_mirror, new SettingsStore(_dir.File("s.json")), _log, new AppSettings(), Post, receivers: _finder);
        await RunPostedUntilAsync(() => vm.Setup.Receivers.Count == 1);

        var air = vm.Setup.Receivers[0];
        Assert.True(air.CanLaunch);
        air.LaunchCommand.Execute(null);
        // After a start the list is searched again.
        await RunPostedUntilAsync(() => _finder.Launched.Count == 1 && vm.Setup.SearchCommand.CanExecute(null));
        Assert.Equal(AirServerInstalled, _finder.Launched[0]);

        _finder.Candidates.Clear();
        vm.Setup.SearchCommand.Execute(null);
        await RunPostedUntilAsync(() => vm.Setup.NoReceivers);
        Assert.Empty(vm.Setup.Receivers);
    }

    [Fact]
    public void WithoutAFinderThereIsNoReceiverList()
    {
        var vm = new MainViewModel(_mirror, new SettingsStore(_dir.File("n.json")), _log, new AppSettings(), Post);

        Assert.False(vm.Setup.CanSearch);
        Assert.False(vm.Setup.NoReceivers);
        Assert.Equal(5, vm.Setup.Steps.Count);
    }

    private void Post(Action a)
    {
        lock (_lock)
        {
            _posted.Add(a);
        }
    }

    private async Task RunPostedUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 500; i++)
        {
            Action[] batch;
            lock (_lock)
            {
                batch = [.. _posted];
                _posted.Clear();
            }

            foreach (var a in batch)
            {
                a();
            }

            if (done())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("timed out waiting for the posted work");
    }
}
