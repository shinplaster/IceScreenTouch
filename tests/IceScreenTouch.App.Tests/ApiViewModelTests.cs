// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using IceScreenTouch.Api;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Resources;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.App.ViewModels;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.App.Tests;

/// <summary>The status line, the settings section and the token controls of the automation API in the window's view model.</summary>
#pragma warning disable CA1001 // disposed by DisposeAsync (IAsyncLifetime), which CA1001 does not know
public sealed class ApiViewModelTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _dir = new();
    private readonly AppLog _log = new(null);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeLicenseSource _source = new();
    private readonly FakeMirror _mirror = new();
    private readonly List<Action> _posted = [];
    private LicenseService? _license;
    private ApiService? _api;
    private SettingsStore? _store;
    private MainViewModel? _vm;

    private MainViewModel Vm => _vm!;

    public async Task InitializeAsync()
    {
        _source.Next = LicenseState.Purchased;
        _license = new LicenseService(_source, _time);
        await _license.RefreshAsync();
        _store = new SettingsStore(_dir.File("settings.json"));
        var settings = new AppSettings();
        _api = new ApiService(_mirror, _license, _dir.Path, () => _vm?.Settings ?? settings, _log.Write, anyPort: true);
        _vm = new MainViewModel(_mirror, _store, _log, settings, _posted.Add, license: _license, api: _api);
    }

    public async Task DisposeAsync()
    {
        await _api!.StopAsync();
        _api.Dispose();
        _license!.Dispose();
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

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task ApplyAsync()
    {
        Vm.ApplySettingsCommand.Execute(null);
        await Vm.ApplySettingsCommand.Last;
    }

    [Fact]
    public void TheStatusLineSaysWhatTheApiIsDoing()
    {
        Assert.Equal("自動操作 API: 127.0.0.1:47821 で待ち受け中", MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.Listening, 47821)).Text);
        Assert.Equal(StatusLevel.Ok, MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.Listening, 47821)).Level);
        Assert.Equal("自動操作 API: スクリプトが操作中", MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.Operating, 47821)).Text);
        var inUse = MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.PortInUse, 50000));
        Assert.Equal("自動操作 API: 起動できません（ポート 50000 はほかのプログラムが使っています）", inUse.Text);
        Assert.Equal(StatusLevel.Error, inUse.Level);
        // The details (paths, exception texts) stay in the log: the window and the tray say one fixed sentence.
        Assert.Equal("自動操作 API: 起動できません（詳しくは「ログ」タブを見てください）", MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.Failed, 1, "cannot write a file in the data folder")).Text);
        Assert.Equal(StatusLevel.Waiting, MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.NotAllowed, 47821)).Level);
        Assert.Equal(string.Empty, MainViewModel.DescribeApi(ApiServiceStatus.Off).Text); // off: nothing to say
    }

    [Fact]
    public void TheStatusLineSpeaksEnglishToo()
    {
        using var _ = new UiCulture("en-US");

        Assert.Equal("Automation API: listening on 127.0.0.1:47821", MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.Listening, 47821)).Text);
        Assert.Equal("Automation API: a script is operating the device", MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.Operating, 47821)).Text);
        Assert.Equal("Automation API: could not start (port 50000 is in use by another program)", MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.PortInUse, 50000)).Text);
    }

    [Fact]
    public void WhileTheApiIsOffTheStatusAndTheTokenRowAreEmpty()
    {
        Vm.Refresh();

        Assert.False(Vm.HasApiStatus);
        Assert.Equal(string.Empty, Vm.ApiStatusText);
        Assert.False(Vm.HasApiToken);
        Assert.False(Vm.ApiEnabled);
        Assert.Equal(47821, Vm.ApiPort);
        Assert.True(Vm.ApiAvailable);
        Assert.False(Vm.ApiTrialLimited);
        Assert.False(Vm.HasApiTrialLeft);
    }

    [Fact]
    public async Task ApplyingTheSwitchStartsTheApiWithoutRestartingTheSession()
    {
        await Vm.StartAsync();
        _mirror.Calls.Clear();
        var port = FreePort();

        Vm.ApiEnabled = true;
        Vm.ApiPort = port;
        Assert.True(Vm.SettingsDirty);
        await ApplyAsync();

        Assert.Empty(_mirror.Calls); // the API runs apart from the session
        Assert.Equal(ApiRunState.Listening, _api!.Status.State);
        Assert.True(Vm.HasApiStatus);
        Assert.Equal($"自動操作 API: 127.0.0.1:{port} で待ち受け中", Vm.ApiStatusText);
        Assert.True(Vm.ApiOk);
        Assert.True(Vm.HasApiToken);
        Assert.Equal(_api.Token, Vm.ApiToken);
        var saved = _store!.Load(out _);
        Assert.True(saved.ApiEnabled);
        Assert.Equal(port, saved.ApiPort);
        Assert.DoesNotContain(_api.Token!, File.ReadAllText(_store.Path), StringComparison.Ordinal);

        Vm.ApiEnabled = false;
        await ApplyAsync();

        Assert.Empty(_mirror.Calls);
        Assert.False(Vm.HasApiStatus);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "api.json")));
    }

    [Fact]
    public async Task APortInUseShowsAsAnErrorOnTheStatusLine()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;

        Vm.ApiEnabled = true;
        Vm.ApiPort = port;
        await ApplyAsync();

        Assert.Equal($"自動操作 API: 起動できません（ポート {port} はほかのプログラムが使っています）", Vm.ApiStatusText);
        Assert.True(Vm.ApiError);
        Assert.Contains(_log.Recent(), l => l.Contains("[api]", StringComparison.Ordinal) && l.Contains("port in use", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSwitchIsDisabledAfterTheTrialAndEnabledAgainByThePurchaseWithoutARestart()
    {
        _source.Next = LicenseState.Expired;
        await _license!.RefreshAsync();
        Vm.Refresh();

        Assert.False(Vm.ApiAvailable);
        Assert.False(Vm.ApiTrialLimited);

        // A switch that is on (saved during the trial, or by hand) does not start the server; the line says why.
        Vm.ApiEnabled = true;
        Vm.ApiPort = FreePort();
        await ApplyAsync();
        Assert.Equal("自動操作 API: 購入すると使えます", Vm.ApiStatusText);
        Assert.Equal(StatusLevel.Waiting, Vm.ApiStatusLevel);
        Assert.False(Vm.HasApiToken);

        _source.Next = LicenseState.Purchased;
        await _license.RefreshAsync();
        await TestWait.UntilAsync(() => _api!.Status.State == ApiRunState.Listening, "the API starts after the purchase");
        Vm.Refresh();

        Assert.True(Vm.ApiAvailable);
        Assert.StartsWith("自動操作 API: 127.0.0.1:", Vm.ApiStatusText, StringComparison.Ordinal);
        Assert.True(Vm.HasApiToken);
    }

    [Fact]
    public async Task InTheTrialTheSwitchWorksAndTheLinesSayTheQuotaUntilThePurchaseLiftsIt()
    {
        _source.Next = LicenseState.Trial(Start.AddHours(24));
        await _license!.RefreshAsync();
        Vm.Refresh();

        Assert.True(Vm.ApiAvailable);
        Assert.True(Vm.ApiTrialLimited);
        Assert.Equal("試用中は、自動操作 API を合計で操作 300 回、スクリーンショットと文字の読み取り 300 回まで使えます（使った回数は戻りません）。WebSocket の接続は 1 回 5 分までです。購入すると、回数の制限なしで使えます。", Vm.ApiTrialNote);
        Assert.Equal("自動操作 API は試用中、合計で操作 300 回、スクリーンショットと文字の読み取り 300 回まで使えます。", Vm.TrialApiNote);

        var port = FreePort();
        Vm.ApiEnabled = true;
        Vm.ApiPort = port;
        await ApplyAsync();

        Assert.Equal(ApiRunState.Listening, _api!.Status.State);
        Assert.Equal($"自動操作 API: 127.0.0.1:{port} で待ち受け中（試用中: 残り 操作 300 回・スクリーンショットと文字の読み取り 300 回）", Vm.ApiStatusText);
        Assert.True(Vm.ApiOk);
        Assert.True(Vm.HasApiTrialLeft);
        Assert.Equal("試用の残り: 操作 300 回、スクリーンショットと文字の読み取り 300 回", Vm.ApiTrialLeft);

        _source.Next = LicenseState.Purchased;
        await _license.RefreshAsync();
        Vm.Refresh();

        Assert.False(Vm.ApiTrialLimited);
        Assert.False(Vm.HasApiTrialLeft);
        Assert.Equal($"自動操作 API: 127.0.0.1:{port} で待ち受け中", Vm.ApiStatusText);
    }

    [Fact]
    public void AUsedUpTrialQuotaIsAWaitingLineInBothLanguages()
    {
        var listening = new ApiServiceStatus(ApiRunState.Listening, 47821);
        var usedUp = new ApiTrialInfo(0, 300, 12, 300, 300);
        using (new UiCulture("ja-JP"))
        {
            var line = MainViewModel.DescribeApi(listening, usedUp);
            Assert.Equal("自動操作 API: 試用の回数を使い切りました（残り 操作 0 回・スクリーンショットと文字の読み取り 12 回）。続けて使うには購入してください", line.Text);
            Assert.Equal(StatusLevel.Waiting, line.Level);
        }

        using var en = new UiCulture("en-US");
        Assert.Equal("Automation API: the trial limit is used up (0 operations and 12 screenshots or text readings left). Buy IceScreen Touch to keep using it", MainViewModel.DescribeApi(listening, usedUp).Text);
        Assert.Equal("Automation API: listening on 127.0.0.1:47821 (trial: 5 operations and 6 screenshots or text readings left)", MainViewModel.DescribeApi(listening, new ApiTrialInfo(5, 300, 6, 300, 300)).Text);
        Assert.Equal("Automation API: a script is operating the device", MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.Operating, 47821), usedUp).Text);
    }

    [Fact]
    public async Task TheTrialNoteAppearsWhenTheLicenseTurnsOutToBeATrial()
    {
        // The startup order: the license is still being read (API unavailable, no trial known), then it says Trial.
        var gate = new TaskCompletionSource<LicenseState>();
        var source = new FakeLicenseSource { Hang = gate };
        using var license = new LicenseService(source, _time);
        using var api = new ApiService(_mirror, license, _dir.Path, () => new AppSettings(), _log.Write, anyPort: true);
        var vm = new MainViewModel(_mirror, _store!, _log, new AppSettings(), _posted.Add, license: license, api: api);
        vm.Refresh();
        Assert.False(vm.ApiAvailable);
        Assert.False(vm.ApiTrialLimited);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        var refresh = license.RefreshAsync();
        gate.SetResult(LicenseState.Trial(Start.AddHours(24)));
        await refresh;
        vm.Refresh();

        Assert.True(vm.ApiAvailable);
        Assert.True(vm.ApiTrialLimited);
        Assert.Contains(nameof(MainViewModel.ApiTrialLimited), changed);
        Assert.Contains(nameof(MainViewModel.ApiTrialLeft), changed);
        await api.StopAsync();
    }

    [Fact]
    public async Task RegeneratingTheTokenAsksFirstAndCancellingChangesNothing()
    {
        Vm.ApiEnabled = true;
        Vm.ApiPort = FreePort();
        await ApplyAsync();
        var token = Vm.ApiToken;

        Assert.True(Vm.RegenerateApiTokenCommand.CanExecute(null));
        Vm.RegenerateApiTokenCommand.Execute(null);
        Assert.True(Vm.ApiConfirmingRegenerate);
        Assert.False(Vm.RegenerateApiTokenCommand.CanExecute(null));

        Vm.CancelRegenerateApiTokenCommand.Execute(null);
        Assert.False(Vm.ApiConfirmingRegenerate);
        Vm.Refresh();
        Assert.Equal(token, Vm.ApiToken);

        Vm.RegenerateApiTokenCommand.Execute(null);
        Vm.ConfirmRegenerateApiTokenCommand.Execute(null);
        await Vm.ConfirmRegenerateApiTokenCommand.Last;

        Assert.False(Vm.ApiConfirmingRegenerate);
        Assert.NotEqual(token, Vm.ApiToken);
        Assert.Equal(_api!.Token, Vm.ApiToken);
        Assert.True(ApiToken.IsValid(Vm.ApiToken));
        Assert.Contains(_log.Recent(), l => l.Contains("regenerated", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Recent(), l => l.Contains(Vm.ApiToken, StringComparison.Ordinal) || l.Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTokenIsHiddenUntilTheUserAsks()
    {
        Vm.ApiEnabled = true;
        Vm.ApiPort = FreePort();
        await ApplyAsync();

        Assert.False(Vm.ApiTokenVisible);
        Assert.Equal("表示", Vm.ApiTokenToggleText);

        Vm.ToggleApiTokenCommand.Execute(null);
        Assert.True(Vm.ApiTokenVisible);
        Assert.Equal("隠す", Vm.ApiTokenToggleText);

        Vm.ToggleApiTokenCommand.Execute(null);
        Assert.False(Vm.ApiTokenVisible);
    }

    [Fact]
    public void WhereTheScriptsFindTheTokenIsShownWithThePath()
    {
        var text = Vm.ApiConnectionFileText;

        Assert.Contains(Path.Combine(_dir.Path, "api.json"), text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptiedPortBoxKeepsTheLastNumberAndTheFormStaysAPort()
    {
        Vm.ApiPort = 50001;
        Vm.ApiPort = null;

        Assert.Equal(50001, Vm.ApiPort);
        Assert.True(Vm.SettingsDirty);
        Vm.RevertSettingsCommand.Execute(null);
        Assert.Equal(47821, Vm.ApiPort);
        Assert.False(Vm.SettingsDirty);
    }

    [Fact]
    public void TheFailureSentenceIsFixedInEnglishToo()
    {
        using var _ = new UiCulture("en-US");

        var text = MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.Failed, 1, @"cannot write C:\Users\someone\api.json")).Text;

        Assert.Equal("Automation API: could not start (see the Log tab)", text);
    }

    [Fact]
    public async Task AFailedStartShowsTheFixedSentenceAndKeepsTheDetailsInTheLog()
    {
        // A file where the data folder should be: the token file cannot be written.
        var blocked = Path.Combine(_dir.Path, "not-a-folder");
        File.WriteAllText(blocked, "x");
        using var api = new ApiService(_mirror, _license!, blocked, () => new AppSettings(), _log.Write, anyPort: true);
        var vm = new MainViewModel(_mirror, _store!, _log, new AppSettings(), _posted.Add, license: _license, api: api);

        vm.ApiEnabled = true;
        vm.ApiPort = FreePort();
        vm.ApplySettingsCommand.Execute(null);
        await vm.ApplySettingsCommand.Last;

        Assert.Equal("自動操作 API: 起動できません（詳しくは「ログ」タブを見てください）", vm.ApiStatusText);
        Assert.DoesNotContain(blocked, vm.ApiStatusText, StringComparison.Ordinal);
        Assert.Contains(_log.Recent(), l => l.Contains("[api]", StringComparison.Ordinal) && l.Contains("token file", StringComparison.Ordinal));
        await api.StopAsync();
    }

    [Fact]
    public async Task TheSwitchStaysDisabledUntilTheFirstLicenseReadHasFinished()
    {
        var gate = new TaskCompletionSource<LicenseState>();
        var source = new FakeLicenseSource { Hang = gate };
        using var license = new LicenseService(source, _time);
        using var api = new ApiService(_mirror, license, _dir.Path, () => new AppSettings(), _log.Write, anyPort: true);
        var vm = new MainViewModel(_mirror, _store!, _log, new AppSettings(), _posted.Add, license: license, api: api);

        vm.Refresh();
        Assert.False(vm.ApiAvailable); // a guess that counts as usable must not open the switch
        Assert.False(vm.ApiTrialLimited); // and nothing is said about the trial yet

        var refresh = license.RefreshAsync();
        gate.SetResult(LicenseState.Purchased);
        await refresh;
        vm.Refresh();

        Assert.True(vm.ApiAvailable);
        await api.StopAsync();
    }

    [Fact]
    public void TheProbePortIsNotAcceptedForTheApiAndTheBoxShowsTheDefaultInstead()
    {
        var changed = new List<string?>();
        Vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Vm.ApiPort = 8000;

        Assert.Equal(47821, Vm.ApiPort);
        Assert.Contains(nameof(MainViewModel.ApiPort), changed); // the box is told to show 47821 even though that was the old value
        Assert.False(Vm.SettingsDirty);
    }

    [Fact]
    public async Task ATargetChosenByAScriptIsSavedLikeTheOneOnTheStatusTab()
    {
        _mirror.Status = new MirrorStatus { Running = true, Devices = [new HidClient("dev-1", "iPhone", 1)] };
        Vm.Refresh();

        Assert.True(_api!.Host.ChooseTarget("dev-1"));
        RunPosted();

        Assert.Contains("target dev-1", _mirror.Calls);
        var saved = _store!.Load(out _);
        Assert.Equal("dev-1", saved.TargetDevice);
        Assert.Equal("iPhone", saved.TargetDeviceName);

        Assert.True(_api.Host.ChooseTarget(null));
        RunPosted();
        Assert.Null(_store.Load(out _).TargetDevice);
        await Task.CompletedTask;
    }

    [Fact]
    public void ALandscapeRotationSetByAScriptWhileStoppedIsSaved()
    {
        _api!.Host.SetRotation(Rotation.R270, null);
        RunPosted();

        Assert.Equal(270, _store!.Load(out _).LandscapeRotation);
        Assert.Equal(270, Vm.Settings.LandscapeRotation);
    }

    [Fact]
    public void EveryApiTextIsInBothLanguages()
    {
        foreach (var culture in new[] { "ja-JP", "en-US" })
        {
            using var _ = new UiCulture(culture);
            string[] texts =
            [
                Strings.SectionApi, Strings.OptionApiEnabled, Strings.FormatApiTrialNote, Strings.FormatApiTrialLeft, Strings.FormatApiListeningTrial,
                Strings.FormatApiTrialUsedUp, Strings.FormatTrialApiNote, Strings.LabelApiPort, Strings.LabelApiToken,
                Strings.ApiTokenPending, Strings.ButtonShowToken, Strings.ButtonHideToken, Strings.ButtonCopyToken,
                Strings.ButtonRegenerateToken, Strings.ButtonCancelRegenerate, Strings.ApiRegenerateQuestion, Strings.ApiWarning,
                Strings.ApiOperating, Strings.ApiNeedsPurchase, Strings.ApiFailed, Strings.ApiUnknownNote, Strings.ApiLicenseUnknown,
            ];
            Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        }
    }

    [Fact]
    public async Task AnUnreadableLicenseKeepsTheSwitchUsableShowsItsOwnNoteAndTheApiStartsWhenTheLicenseIsRead()
    {
        _source.Next = LicenseState.Unknown;
        using var license = new LicenseService(_source, _time);
        await license.RefreshAsync(); // the first read fails: Unknown, and no guess any more
        using var api = new ApiService(_mirror, license, _dir.Path, () => new AppSettings(), _log.Write, anyPort: true);
        var vm = new MainViewModel(_mirror, _store!, _log, new AppSettings(), _posted.Add, license: license, api: api);

        vm.Refresh();

        Assert.True(vm.ApiAvailable); // the user may turn it on: it starts when the license is read
        Assert.True(vm.ApiLicenseUnknown);
        Assert.False(vm.ApiTrialLimited); // a paying user who is offline is not told about the trial

        vm.ApiEnabled = true;
        vm.ApiPort = FreePort();
        vm.ApplySettingsCommand.Execute(null);
        await vm.ApplySettingsCommand.Last;

        Assert.Equal(ApiRunState.LicenseUnknown, api.Status.State);
        Assert.Equal("自動操作 API: Microsoft Store のライセンスを確認できるまで止まっています", vm.ApiStatusText);
        Assert.Equal(StatusLevel.Waiting, vm.ApiStatusLevel);
        Assert.False(vm.HasApiToken);

        _source.Next = LicenseState.Purchased;
        await license.RefreshAsync();
        await TestWait.UntilAsync(() => api.Status.State == ApiRunState.Listening, "the API starts when the license is read");
        vm.Refresh();

        Assert.False(vm.ApiLicenseUnknown);
        Assert.StartsWith("自動操作 API: 127.0.0.1:", vm.ApiStatusText, StringComparison.Ordinal);
        await api.StopAsync();
    }

    [Fact]
    public void TheUnreadableLicenseNoteAndTheTrialNoteAreDifferentInBothLanguages()
    {
        foreach (var culture in new[] { "ja-JP", "en-US" })
        {
            using var _ = new UiCulture(culture);

            Assert.NotEqual(Strings.FormatApiTrialNote, Strings.ApiUnknownNote);
            Assert.NotEqual(Strings.ApiNeedsPurchase, Strings.ApiLicenseUnknown);
        }

        using var en = new UiCulture("en-US");
        Assert.Equal("Automation API: off until the Microsoft Store license can be checked", MainViewModel.DescribeApi(new ApiServiceStatus(ApiRunState.LicenseUnknown, 47821)).Text);
    }
}
