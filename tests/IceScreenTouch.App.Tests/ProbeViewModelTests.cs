using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.App.ViewModels;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Tests;

internal sealed class FakeProbe : IProbeService
{
    public TaskCompletionSource<ProbeReport> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IProgress<ProbeUpdate>? Progress { get; private set; }

    public CancellationToken Token { get; private set; }

    public List<ProbeKind> Runs { get; } = [];

    public FirewallState Firewall { get; set; } = FirewallState.Missing;

    public bool FirewallFromPackage { get; set; }

    public bool AllowSucceeds { get; set; } = true;

    public int AllowCalls { get; private set; }

    public int Port => 8000;

    /// <summary>When set, <see cref="PageUrls"/> throws it (the network interfaces cannot be listed).</summary>
    public Exception? PageUrlsError { get; set; }

    public IReadOnlyList<string> PageUrls(ProbeKind kind) =>
        PageUrlsError is { } error ? throw error : [kind switch
        {
            ProbeKind.Scroll => "http://192.168.1.10:8000/scroll",
            ProbeKind.Latency => "http://192.168.1.10:8000/latency",
            ProbeKind.Keys => "http://192.168.1.10:8000/keys",
            _ => "http://192.168.1.10:8000/touch",
        }];

    public Task<ProbeReport> RunAsync(ProbeKind kind, IProgress<ProbeUpdate> progress, CancellationToken cancellationToken)
    {
        Runs.Add(kind);
        Progress = progress;
        Token = cancellationToken;
        cancellationToken.Register(() => Result.TrySetCanceled(cancellationToken));
        return Result.Task;
    }

    public Task<FirewallState> CheckFirewallAsync(CancellationToken cancellationToken = default) => Task.FromResult(Firewall);

    public Task<bool> AllowFirewallAsync(CancellationToken cancellationToken = default)
    {
        AllowCalls++;
        if (AllowSucceeds)
        {
            Firewall = FirewallState.Allowed;
        }

        return Task.FromResult(AllowSucceeds);
    }
}

public sealed class ProbeViewModelTests : IDisposable
{
    private static readonly MirrorStatus Connected = new() { Running = true, Ble = BleState.Connected, Receiver = ReceiverState.Showing };

    private readonly TempFolder _dir = new();
    private readonly FakeMirror _mirror = new();
    private readonly FakeProbe _probe = new();
    private readonly AppLog _log = new(null);
    private readonly List<Action> _posted = [];
    private readonly Lock _lock = new();
    private readonly MainViewModel _vm;

    public ProbeViewModelTests()
    {
        _vm = new MainViewModel(_mirror, new SettingsStore(_dir.File("settings.json")), _log, new AppSettings(), Post, _probe);
    }

    private ProbeViewModel Probe => _vm.Probe!;

    public void Dispose()
    {
        _log.Dispose();
        _dir.Dispose();
    }

    private void Post(Action a)
    {
        lock (_lock)
        {
            _posted.Add(a);
        }
    }

    // Runs what other threads post until done() holds (at most 5 s).
    private async Task RunPostedUntilAsync(Func<bool> done)
    {
        for (var i = 0; i < 500; i++)
        {
            RunPosted();
            if (done())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("timed out waiting for the posted work");
    }

    private Task FirewallCheckedAsync() => RunPostedUntilAsync(() => Probe.Firewall != FirewallState.Unknown);

    private Task RunFinishedAsync() => RunPostedUntilAsync(() => !Probe.IsRunning);

    private void RunPosted()
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
    }

    private void Connect()
    {
        _mirror.Status = Connected;
        _vm.Refresh();
    }

    [Fact]
    public async Task ShowsTheFirewallRuleAndNoAddressBeforeARun()
    {
        await FirewallCheckedAsync();

        Assert.True(_vm.HasProbe);
        Assert.False(Probe.ShowQr); // the address shows (with its QR code) once a run waits for the page
        Assert.Equal(FirewallState.Missing, Probe.Firewall);
        Assert.Equal(StatusLevel.Waiting, Probe.FirewallLevel);
        Assert.True(Probe.AllowFirewallCommand.CanExecute(null));
    }

    [Fact]
    public void WithoutAProbeServiceThereIsNoTab()
    {
        var vm = new MainViewModel(_mirror, new SettingsStore(_dir.File("other.json")), _log, new AppSettings(), Post);

        Assert.False(vm.HasProbe);
        Assert.Null(vm.Probe);
    }

    [Theory]
    [InlineData(false, BleState.Off, false)]
    [InlineData(true, BleState.DryRun, false)]
    [InlineData(true, BleState.Unsupported, false)]
    [InlineData(true, BleState.Advertising, true)]
    [InlineData(true, BleState.Connected, true)]
    public void StartsOnlyWithBluetooth(bool running, BleState ble, bool canStart)
    {
        _mirror.Status = new MirrorStatus { Running = running, Ble = ble };

        _vm.Refresh();

        Assert.Equal(canStart, Probe.StartTouchCommand.CanExecute(null));
        Assert.Equal(canStart, Probe.StartScrollCommand.CanExecute(null));
        Assert.Equal(canStart, Probe.StartLatencyCommand.CanExecute(null));
    }

    [Fact]
    public async Task TheLatencyRunTellsNotToCoverTheMirror()
    {
        Connect();
        Probe.StartLatencyCommand.Execute(null);

        Assert.Equal([ProbeKind.Latency], _probe.Runs);
        _probe.Progress!.Report(new ProbeUpdate { Phase = ProbePhase.Waiting, WaitingFor = ProbeWaitingFor.MirrorHidden, Urls = ["http://10.0.0.5:8000/latency"] });
        RunPosted();
        Assert.Null(Probe.QrUrl); // the page is open: only the mirror is missing
        Assert.Contains("隠さない", Probe.StatusText, StringComparison.Ordinal);

        _probe.Progress.Report(new ProbeUpdate { Phase = ProbePhase.Running });
        RunPosted();
        Assert.Contains("ミラー画面を動かしたり隠したりしない", Probe.StatusText, StringComparison.Ordinal);

        _probe.Result.SetResult(new ProbeReport(ProbePhase.Passed, "中央値 120 ms", []));
        await RunFinishedAsync();
        Assert.Equal("中央値 120 ms", Probe.StatusText);
    }

    [Fact]
    public async Task AQrCodeOfThePageShowsUntilThePageIsOpen()
    {
        Connect();
        Assert.False(Probe.ShowQr);
        Probe.StartKeysCommand.Execute(null);

        _probe.Progress!.Report(new ProbeUpdate { Phase = ProbePhase.Waiting, WaitingFor = ProbeWaitingFor.PageNotOpened, Urls = ["http://10.0.0.5:8000/keys", "http://172.16.0.2:8000/keys"] });
        RunPosted();
        Assert.True(Probe.ShowQr);
        Assert.Equal("http://10.0.0.5:8000/keys", Probe.QrUrl); // the first address (the default route's)

        // the page is open: now the box needs the cursor, the code goes away
        _probe.Progress.Report(new ProbeUpdate { Phase = ProbePhase.Waiting, WaitingFor = ProbeWaitingFor.FieldNotFocused });
        RunPosted();
        Assert.False(Probe.ShowQr);

        // back in the background: the code again
        _probe.Progress.Report(new ProbeUpdate { Phase = ProbePhase.Waiting, WaitingFor = ProbeWaitingFor.PageNotInFront });
        RunPosted();
        Assert.True(Probe.ShowQr);

        _probe.Result.SetResult(new ProbeReport(ProbePhase.Cancelled, "x", []));
        await RunFinishedAsync();
        Assert.False(Probe.ShowQr);
    }

    [Fact]
    public void TheQrCodeHasItsQuietZoneAndFinderPatterns()
    {
        var m = IceScreenTouch.App.Controls.QrCodeView.Encode("http://192.168.1.10:8000/keys");
        var n = m.GetLength(0);

        Assert.Equal(n, m.GetLength(1));
        Assert.Equal(0, (n - 8 - 17) % 4); // 4 * version + 17 modules, plus a quiet zone of 4 on each side
        for (var i = 0; i < n; i++)
        {
            Assert.False(m[0, i] || m[i, 0] || m[3, i] || m[i, n - 1]); // quiet zone
        }

        // top-left finder: dark ring, light ring, dark 3x3 core
        Assert.True(m[4, 4] && m[4, 10] && m[10, 4] && m[10, 10]);
        Assert.False(m[5, 5] || m[5, 9]);
        Assert.True(m[7, 7] && m[6, 6] && m[8, 8]);
    }

    [Fact]
    public async Task ARunShowsTheProgressAndThePassedResult()
    {
        Connect();
        Probe.StartTouchCommand.Execute(null);

        Assert.True(Probe.IsRunning);
        Assert.Equal([ProbeKind.Touch], _probe.Runs);
        Assert.False(_vm.RestartCommand.CanExecute(null)); // a restart would pull Bluetooth from the probe
        Assert.False(Probe.StartScrollCommand.CanExecute(null));
        Assert.True(Probe.CancelCommand.CanExecute(null));

        _probe.Progress!.Report(new ProbeUpdate { Phase = ProbePhase.Waiting, WaitingFor = ProbeWaitingFor.PageNotOpened, Urls = ["http://10.0.0.5:8000/touch"] });
        RunPosted();
        Assert.Equal("http://10.0.0.5:8000/touch", Probe.QrUrl);
        Assert.Contains("Safari", Probe.StatusText, StringComparison.Ordinal);
        Assert.Equal(StatusLevel.Waiting, Probe.StatusLevel);

        _probe.Progress.Report(new ProbeUpdate { Phase = ProbePhase.Countdown, SecondsLeft = 2 });
        _probe.Progress.Report(new ProbeUpdate { Phase = ProbePhase.Running, Line = "matched 9/9 points" });
        RunPosted();
        Assert.Contains("検証中", Probe.StatusText, StringComparison.Ordinal);
        Assert.Equal(["matched 9/9 points"], Probe.Output);

        _probe.Result.SetResult(new ProbeReport(ProbePhase.Passed, "合格", [new ProbeCheckItem("9 点のタップ", true, "9/9")]));
        await RunFinishedAsync();
        await Probe.Last;

        Assert.False(Probe.IsRunning);
        Assert.Equal("合格", Probe.StatusText);
        Assert.Equal(StatusLevel.Ok, Probe.StatusLevel);
        Assert.Single(Probe.Checks);
        Assert.True(_vm.RestartCommand.CanExecute(null));

        // The result stays when the status timer ticks.
        _vm.Refresh();
        Assert.Equal("合格", Probe.StatusText);
    }

    [Fact]
    public async Task CancelStopsTheRun()
    {
        Connect();
        Probe.StartScrollCommand.Execute(null);

        Probe.CancelCommand.Execute(null);
        await RunFinishedAsync();
        await Probe.Last;

        Assert.True(_probe.Token.IsCancellationRequested);
        Assert.False(Probe.IsRunning);
        Assert.Equal("中止しました。", Probe.StatusText);
        Assert.Equal(StatusLevel.Idle, Probe.StatusLevel);
    }

    [Fact]
    public async Task AFailedRunIsShownAsAnError()
    {
        Connect();
        Probe.StartTouchCommand.Execute(null);

        _probe.Result.SetException(new InvalidOperationException("boom"));
        await RunFinishedAsync();

        Assert.Equal(StatusLevel.Error, Probe.StatusLevel);
        Assert.Contains("boom", Probe.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureToListThePageAddressesIsShownAndTheStateIsReset()
    {
        // NetworkInterface.GetAllNetworkInterfaces can throw: the button must not silently do nothing (review, 2026-11-07).
        Connect();
        _probe.PageUrlsError = new System.Net.NetworkInformation.NetworkInformationException(1);
        Probe.StartTouchCommand.Execute(null);
        await RunFinishedAsync();
        await Probe.Last;

        Assert.False(Probe.IsRunning);
        Assert.Equal(StatusLevel.Error, Probe.StatusLevel);
        Assert.NotEmpty(Probe.StatusText);
        Assert.Empty(_probe.Runs); // the probe itself never started
        Assert.Null(Probe.QrUrl);

        // And it can be started again once the network is back.
        _probe.PageUrlsError = null;
        Assert.True(Probe.StartTouchCommand.CanExecute(null));
        Probe.StartTouchCommand.Execute(null);
        Assert.True(Probe.IsRunning);
        Assert.Equal([ProbeKind.Touch], _probe.Runs);
        _probe.Result.SetResult(new ProbeReport(ProbePhase.Cancelled, "x", []));
        await RunFinishedAsync();
    }

    [Fact]
    public async Task AllowFirewallChecksAgainAfterwards()
    {
        await FirewallCheckedAsync();

        Probe.AllowFirewallCommand.Execute(null);
        Assert.False(Probe.AllowFirewallCommand.CanExecute(null)); // busy
        await RunPostedUntilAsync(() => Probe.Firewall == FirewallState.Allowed);

        Assert.Equal(1, _probe.AllowCalls);
        Assert.Equal(FirewallState.Allowed, Probe.Firewall);
        Assert.Equal(StatusLevel.Ok, Probe.FirewallLevel);
        Assert.False(Probe.AllowFirewallCommand.CanExecute(null)); // nothing left to do
    }

    [Fact]
    public async Task APackagedBuildAcceptsTheManifestRuleAndNeverOffersTheButton()
    {
        _probe.FirewallFromPackage = true;
        _probe.Firewall = FirewallState.AllowedByPackage;
        var vm = new MainViewModel(_mirror, new SettingsStore(_dir.File("packaged.json")), _log, new AppSettings(), Post, _probe);
        await RunPostedUntilAsync(() => vm.Probe!.Firewall != FirewallState.Unknown);

        Assert.Equal(StatusLevel.Ok, vm.Probe!.FirewallLevel);
        Assert.Contains("インストール", vm.Probe.FirewallText, StringComparison.Ordinal);
        Assert.False(vm.Probe.ShowFirewallButton);
        Assert.False(vm.Probe.AllowFirewallCommand.CanExecute(null));
    }

    [Fact]
    public async Task APackagedBuildWithoutTheRuleSaysToRepairAndHasNoButton()
    {
        _probe.FirewallFromPackage = true;
        _probe.Firewall = FirewallState.Missing;
        var vm = new MainViewModel(_mirror, new SettingsStore(_dir.File("packaged.json")), _log, new AppSettings(), Post, _probe);
        await RunPostedUntilAsync(() => vm.Probe!.Firewall != FirewallState.Unknown);

        Assert.Equal(StatusLevel.Waiting, vm.Probe!.FirewallLevel);
        Assert.Contains("修復", vm.Probe.FirewallText, StringComparison.Ordinal);
        Assert.False(vm.Probe.ShowFirewallButton);
        Assert.False(vm.Probe.AllowFirewallCommand.CanExecute(null));
    }

    [Fact]
    public async Task APlainExecutableKeepsTheButton()
    {
        await FirewallCheckedAsync();

        Assert.True(Probe.ShowFirewallButton);
        Assert.DoesNotContain("修復", Probe.FirewallText, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePackagedWaitingHintDoesNotPointAtTheButton()
    {
        var plain = ProbeViewModel.DescribeWaiting(ProbeKind.Touch, ProbeWaitingFor.PageNotOpened, FirewallState.Missing);
        var packaged = ProbeViewModel.DescribeWaiting(ProbeKind.Touch, ProbeWaitingFor.PageNotOpened, FirewallState.Missing, firewallFromPackage: true);

        Assert.Contains("ファイアウォールを設定", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("ファイアウォールを設定", packaged, StringComparison.Ordinal);
        Assert.Contains("修復", packaged, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclinedFirewallPromptIsReported()
    {
        await FirewallCheckedAsync();
        _probe.AllowSucceeds = false;

        Probe.AllowFirewallCommand.Execute(null);
        await RunPostedUntilAsync(() => Probe.AllowFirewallCommand.CanExecute(null));

        Assert.StartsWith("設定できませんでした", Probe.FirewallText, StringComparison.Ordinal);
        Assert.True(Probe.AllowFirewallCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(ProbeWaitingFor.Bluetooth | ProbeWaitingFor.PageNotOpened, FirewallState.Allowed, "Bluetooth")]
    [InlineData(ProbeWaitingFor.PageNotOpened, FirewallState.Missing, "ファイアウォール")]
    [InlineData(ProbeWaitingFor.PageNotOpened, FirewallState.Allowed, "同じネットワーク")]
    [InlineData(ProbeWaitingFor.WrongPage, FirewallState.Allowed, "別の計測ページ")]
    [InlineData(ProbeWaitingFor.PageNotInFront, FirewallState.Allowed, "前面")]
    public void WaitingTextNamesTheMostBlockingThing(ProbeWaitingFor waiting, FirewallState firewall, string expected)
    {
        Assert.Contains(expected, ProbeViewModel.DescribeWaiting(ProbeKind.Touch, waiting, firewall), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLatencyPageIsNamedAndThePageComesBeforeTheMirror()
    {
        Assert.Contains("/latency", ProbeViewModel.DescribeWaiting(ProbeKind.Latency, ProbeWaitingFor.PageNotOpened | ProbeWaitingFor.MirrorHidden, FirewallState.Allowed), StringComparison.Ordinal);
        Assert.Contains("ミラー画面", ProbeViewModel.DescribeWaiting(ProbeKind.Latency, ProbeWaitingFor.MirrorHidden, FirewallState.Allowed), StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyboardPageIsNamedAndAsksForTheCursor()
    {
        Assert.Contains("/keys", ProbeViewModel.DescribeWaiting(ProbeKind.Keys, ProbeWaitingFor.PageNotOpened | ProbeWaitingFor.FieldNotFocused, FirewallState.Allowed), StringComparison.Ordinal);
        Assert.Contains("入力欄", ProbeViewModel.DescribeWaiting(ProbeKind.Keys, ProbeWaitingFor.FieldNotFocused, FirewallState.Allowed), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusTabSaysTheMouseIsPausedDuringAProbe()
    {
        var s = Connected with { Bridge = BridgeStatus.Initial with { Window = 1, Suspended = true } };

        Assert.Contains("検証", MainViewModel.DescribeNextStep(s, new AppSettings()), StringComparison.Ordinal);
    }
}
