// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using IceScreenTouch.App.Logging;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;
using IceScreenTouch.App.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.App.Tests;

/// <summary>Polls a condition in real time; throws instead of giving up quietly.</summary>
internal static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"Timed out after 10 s waiting for: {what}");
            }

            await Task.Delay(5);
        }
    }
}

internal sealed class FakeLicenseSource : ILicenseSource
{
    public LicenseState Next { get; set; } = LicenseState.Purchased;

    public Exception? ReadFails { get; set; }

    public TaskCompletionSource<LicenseState>? Hang { get; set; }

    public PurchaseOutcome Outcome { get; set; } = PurchaseOutcome.NotPurchased;

    /// <summary>What the license becomes when the purchase succeeds.</summary>
    public LicenseState AfterPurchase { get; set; } = LicenseState.Purchased;

    public int Reads { get; private set; }

    public int Purchases { get; private set; }

    /// <summary>The thread PurchaseAsync was called on.</summary>
    public int PurchaseThread { get; private set; }

    public List<nint> Windows { get; } = [];

    public event EventHandler? Changed;

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void AttachWindow(nint handle) => Windows.Add(handle);

    public Task<LicenseState> ReadAsync()
    {
        Reads++;
        if (ReadFails is { } ex)
        {
            return Task.FromException<LicenseState>(ex);
        }

        return Hang?.Task ?? Task.FromResult(Next);
    }

    public Task<PurchaseOutcome> PurchaseAsync()
    {
        Purchases++;
        PurchaseThread = Environment.CurrentManagedThreadId;
        if (Outcome is PurchaseOutcome.Succeeded or PurchaseOutcome.AlreadyPurchased)
        {
            Next = AfterPurchase;
        }

        return Task.FromResult(Outcome);
    }
}

public sealed class LicenseStateTests
{
    private static readonly DateTimeOffset End = new(2026, 11, 9, 5, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheStoreFlagsMapToStates()
    {
        Assert.Equal(LicenseState.Purchased, LicenseState.From(isActive: true, isTrial: false, End));
        Assert.Equal(LicenseState.Trial(End), LicenseState.From(isActive: true, isTrial: true, End));
        Assert.Equal(LicenseState.Expired, LicenseState.From(isActive: false, isTrial: true, End));
        Assert.Equal(LicenseState.Expired, LicenseState.From(isActive: false, isTrial: false, End));
    }

    [Fact]
    public void ATrialWithoutADateCannotBeTimedAndCountsAsUnknown()
    {
        Assert.Equal(LicenseState.Unknown, LicenseState.From(isActive: true, isTrial: true, default));
    }

    [Theory]
    [InlineData(LicenseKind.NotPackaged, true)]
    [InlineData(LicenseKind.Purchased, true)]
    [InlineData(LicenseKind.Trial, true)]
    [InlineData(LicenseKind.Unknown, true)]
    [InlineData(LicenseKind.Expired, false)]
    public void OnlyAnEndedTrialBlocksTheSession(LicenseKind kind, bool allowed)
    {
        Assert.Equal(allowed, new LicenseState(kind).AllowsSession);
    }
}

public sealed class LicenseLinesTests
{
    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("test-jst", TimeSpan.FromHours(9), "JST", "JST");
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 5, 0, 0, TimeSpan.Zero);
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Ja = CultureInfo.GetCultureInfo("ja-JP");

    [Theory]
    [InlineData((24 * 60) - 1, "あと 23 時間")] // 23h59m: still hours
    [InlineData((23 * 60) + 24, "あと 23 時間")] // 23.4 hours: rounded down
    [InlineData(60, "あと 1 時間")]
    [InlineData(59, "あと 59 分")] // under an hour: minutes, never "0 hours"
    [InlineData(1, "あと 1 分")]
    public void HoursAreRoundedDownAndMinutesTakeOverUnderAnHour(int minutesLeft, string expected)
    {
        var text = LicenseLines.TrialRemaining(Now.AddMinutes(minutesLeft), Now, Tokyo, Ja);

        Assert.StartsWith("試用中: " + expected, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(7 * 24 * 60, "あと 7 日")]
    [InlineData((6 * 24 * 60) + (23 * 60), "あと 6 日")] // 6 days 23 hours: rounded down
    [InlineData(48 * 60, "あと 2 日")]
    [InlineData((48 * 60) - 1, "あと 1 日")] // 47h59m: singular
    [InlineData(24 * 60, "あと 1 日")]
    public void FromADayOnDaysAreCountedAndRoundedDown(int minutesLeft, string expected)
    {
        var text = LicenseLines.TrialRemaining(Now.AddMinutes(minutesLeft), Now, Tokyo, Ja);

        Assert.StartsWith("試用中: " + expected + "（", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DaysUseTheSingularInEnglishAndHoursTakeOverUnderADay()
    {
        using var _ = new UiCulture("en-US");

        Assert.StartsWith("Trial: 7 days left (until Nov 15, ", LicenseLines.TrialRemaining(Now.AddDays(7), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 6 days left (until ", LicenseLines.TrialRemaining(Now.AddMinutes((6 * 24 * 60) + (23 * 60)), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 2 days left (until ", LicenseLines.TrialRemaining(Now.AddHours(48), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 1 day left (until ", LicenseLines.TrialRemaining(Now.AddMinutes((48 * 60) - 1), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 1 day left (until ", LicenseLines.TrialRemaining(Now.AddHours(24), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 23 hours left (until ", LicenseLines.TrialRemaining(Now.AddMinutes((24 * 60) - 1), Now, Tokyo, En), StringComparison.Ordinal);
    }

    [Fact]
    public void MinutesAreRoundedDownButNeverBelowOne()
    {
        Assert.Contains("あと 59 分", LicenseLines.TrialRemaining(Now.AddMinutes(59.99), Now, Tokyo, Ja), StringComparison.Ordinal);
        Assert.Contains("あと 1 分", LicenseLines.TrialRemaining(Now.AddSeconds(30), Now, Tokyo, Ja), StringComparison.Ordinal);
        Assert.Contains("あと 1 分", LicenseLines.TrialRemaining(Now.AddMinutes(-5), Now, Tokyo, Ja), StringComparison.Ordinal);
    }

    [Fact]
    public void TheEndIsShownInLocalTimeInTheUiLanguage()
    {
        var end = new DateTimeOffset(2026, 11, 9, 5, 0, 0, TimeSpan.Zero); // 14:00 in Tokyo

        Assert.Equal("11月9日 14:00", LicenseLines.Until(end, Tokyo, Ja));
        var english = LicenseLines.Until(end, Tokyo, En);
        Assert.StartsWith("Nov 9, 2:00", english, StringComparison.Ordinal);
    }

    [Fact]
    public void OneHourAndOneMinuteUseTheSingularInEnglish()
    {
        using var _ = new UiCulture("en-US");

        Assert.StartsWith("Trial: 1 hour left (until ", LicenseLines.TrialRemaining(Now.AddMinutes(60), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 1 hour left (until ", LicenseLines.TrialRemaining(Now.AddMinutes(119), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 2 hours left (until ", LicenseLines.TrialRemaining(Now.AddMinutes(120), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 1 minute left (until ", LicenseLines.TrialRemaining(Now.AddMinutes(1), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 1 minute left (until ", LicenseLines.TrialRemaining(Now.AddSeconds(10), Now, Tokyo, En), StringComparison.Ordinal);
        Assert.StartsWith("Trial: 2 minutes left (until ", LicenseLines.TrialRemaining(Now.AddMinutes(2), Now, Tokyo, En), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrialLineFollowsTheLanguage()
    {
        using var _ = new UiCulture("en-US");

        var hours = LicenseLines.TrialRemaining(Now.AddMinutes(23 * 60), Now, Tokyo, En);
        var minutes = LicenseLines.TrialRemaining(Now.AddMinutes(20), Now, Tokyo, En);

        Assert.StartsWith("Trial: 23 hours left (until Nov 9, ", hours, StringComparison.Ordinal);
        Assert.StartsWith("Trial: 20 minutes left (until Nov 8, ", minutes, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAboutLineSaysEveryState()
    {
        var end = new DateTimeOffset(2026, 11, 9, 5, 0, 0, TimeSpan.Zero);

        Assert.Equal("ライセンス: 購入済み", LicenseLines.About(LicenseState.Purchased, Tokyo, Ja));
        Assert.Equal("ライセンス: 試用中（11月9日 14:00 まで）", LicenseLines.About(LicenseState.Trial(end), Tokyo, Ja));
        Assert.Equal("ライセンス: 試用期間の終了", LicenseLines.About(LicenseState.Expired, Tokyo, Ja));
        Assert.Equal("ライセンス: 自分でビルドしたもの（ライセンスの確認なし）", LicenseLines.About(LicenseState.NotPackaged, Tokyo, Ja));
        Assert.Equal("ライセンス: 確認できませんでした", LicenseLines.About(LicenseState.Unknown, Tokyo, Ja));

        using var _ = new UiCulture("en-US");
        Assert.Equal("License: Purchased", LicenseLines.About(LicenseState.Purchased, Tokyo, En));
        Assert.Equal("License: Trial ended", LicenseLines.About(LicenseState.Expired, Tokyo, En));
        Assert.Equal("License: Self-built (no license check)", LicenseLines.About(LicenseState.NotPackaged, Tokyo, En));
        Assert.Equal("License: Could not be checked", LicenseLines.About(LicenseState.Unknown, Tokyo, En));
    }
}

public sealed class LicenseServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 5, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeLicenseSource _source = new();

    private LicenseService Service() => new(_source, _time);

    // The source's event starts a read on the thread pool: wait for its effect (real time, not the fake clock).
    private static Task WaitAsync(Func<bool> condition, string what) => TestWait.UntilAsync(condition, what);

    [Fact]
    public async Task WithoutASourceTheBuildIsNotPackagedForGood()
    {
        using var service = new LicenseService(null, _time);

        Assert.Equal(LicenseState.NotPackaged, service.Current);
        Assert.Equal(LicenseState.NotPackaged, await service.RefreshAsync());
        Assert.Equal(PurchaseOutcome.Failed, await service.BuyAsync());
        service.StartMonitoring();
        service.AttachWindow(1);
        Assert.True(service.Current.AllowsSession);
    }

    [Fact]
    public async Task ARefreshReadsTheSourceAndRaisesChangedOnlyOnAChange()
    {
        using var service = Service();
        var raised = 0;
        service.Changed += (_, _) => raised++;
        _source.Next = LicenseState.Trial(Start.AddHours(24));

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(LicenseState.Trial(Start.AddHours(24)), service.Current);
        Assert.Equal(1, raised);

        _source.Next = LicenseState.Expired;
        await service.RefreshAsync();
        Assert.Equal(2, raised);
        Assert.False(service.Current.AllowsSession);
    }

    [Fact]
    public async Task AFailingReadIsUnknownAndUsable()
    {
        using var service = Service();
        _source.ReadFails = new InvalidOperationException("no store");

        var state = await service.RefreshAsync();

        Assert.Equal(LicenseState.Unknown, state);
        Assert.True(state.AllowsSession);
    }

    [Fact]
    public async Task AHangingReadTimesOutAsUnknown()
    {
        using var service = Service();
        _source.Hang = new TaskCompletionSource<LicenseState>();

        var refresh = service.RefreshAsync();
        Assert.False(refresh.IsCompleted);
        _time.Advance(LicenseService.ReadTimeout + TimeSpan.FromSeconds(1));

        Assert.Equal(LicenseState.Unknown, await refresh);
    }

    [Fact]
    public async Task AFailedReadKeepsWhatAnEarlierReadSaid()
    {
        using var service = Service();
        _source.Next = LicenseState.Expired;
        await service.RefreshAsync();
        _source.ReadFails = new InvalidOperationException();

        // Blocking the Store service must not bring an ended trial back.
        Assert.Equal(LicenseState.Expired, await service.RefreshAsync());

        _source.ReadFails = null;
        _source.Next = LicenseState.Purchased;
        await service.RefreshAsync();
        _source.ReadFails = new InvalidOperationException();
        Assert.Equal(LicenseState.Purchased, await service.RefreshAsync());
    }

    [Fact]
    public async Task AFailedReadKeepsATrialUntilItsEnd()
    {
        using var service = Service();
        _source.Next = LicenseState.Trial(Start.AddHours(1));
        await service.RefreshAsync();
        _source.ReadFails = new InvalidOperationException();

        Assert.Equal(LicenseState.Trial(Start.AddHours(1)), await service.RefreshAsync());

        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(LicenseState.Expired, await service.RefreshAsync());
    }

    [Fact]
    public async Task TheTimerReadsEveryTenMinutes()
    {
        using var service = Service();
        _source.Next = LicenseState.Trial(Start.AddHours(24));
        await service.RefreshAsync();
        Assert.Equal(1, _source.Reads);

        _time.Advance(TimeSpan.FromMinutes(9));
        await service.TickAsync();
        Assert.Equal(1, _source.Reads);

        _time.Advance(TimeSpan.FromMinutes(1));
        await service.TickAsync();
        Assert.Equal(2, _source.Reads);
    }

    [Fact]
    public async Task TheTimerReadsEveryMinuteOnceTheTrialHasEndedOrExpired()
    {
        using var service = Service();
        _source.Next = LicenseState.Trial(Start.AddMinutes(5));
        await service.RefreshAsync();

        // The end passes between two reads: the next tick asks the Store at once.
        _time.Advance(TimeSpan.FromMinutes(6));
        _source.Next = LicenseState.Expired;
        await service.TickAsync();

        Assert.Equal(LicenseState.Expired, service.Current);
        Assert.Equal(2, _source.Reads);

        // Expired: every minute (a purchase made elsewhere shows up soon).
        _time.Advance(TimeSpan.FromSeconds(30));
        await service.TickAsync();
        Assert.Equal(2, _source.Reads);
        _time.Advance(TimeSpan.FromSeconds(30));
        await service.TickAsync();
        Assert.Equal(3, _source.Reads);
    }

    [Fact]
    public async Task TheStoreEventReadsTheLicense()
    {
        using var service = Service();
        _source.Next = LicenseState.Expired;

        _source.RaiseChanged();
        await WaitAsync(() => service.Current == LicenseState.Expired, "the license to become Expired");

        Assert.Equal(1, _source.Reads);
        Assert.Equal(LicenseState.Expired, service.Current);
    }

    [Fact]
    public async Task ASuccessfulPurchaseReadsTheLicenseAgain()
    {
        using var service = Service();
        _source.Next = LicenseState.Expired;
        await service.RefreshAsync();
        _source.Outcome = PurchaseOutcome.Succeeded;

        Assert.Equal(PurchaseOutcome.Succeeded, await service.BuyAsync());

        Assert.Equal(LicenseState.Purchased, service.Current);
    }

    [Theory]
    [InlineData(PurchaseOutcome.NotPurchased)]
    [InlineData(PurchaseOutcome.NetworkError)]
    [InlineData(PurchaseOutcome.ServerError)]
    [InlineData(PurchaseOutcome.Failed)]
    public async Task AnUnsuccessfulPurchaseChangesNothing(PurchaseOutcome outcome)
    {
        using var service = Service();
        _source.Next = LicenseState.Expired;
        await service.RefreshAsync();
        _source.Outcome = outcome;
        var reads = _source.Reads;

        Assert.Equal(outcome, await service.BuyAsync());

        Assert.Equal(reads, _source.Reads);
        Assert.Equal(LicenseState.Expired, service.Current);
    }

    [Fact]
    public async Task AFailingPurchaseDoesNotThrow()
    {
        using var service = new LicenseService(new ThrowingPurchase(), _time);

        Assert.Equal(PurchaseOutcome.Failed, await service.BuyAsync());
    }

    [Fact]
    public void TheWindowGoesToTheSource()
    {
        using var service = Service();

        service.AttachWindow(42);

        Assert.Equal<nint>([42], _source.Windows);
    }

    [Fact]
    public async Task AnUnknownLicenseIsReadEveryMinute()
    {
        using var service = Service();
        _source.ReadFails = new InvalidOperationException("no store");
        await service.RefreshAsync();
        Assert.Equal(LicenseState.Unknown, service.Current);
        Assert.Equal(1, _source.Reads);

        _time.Advance(TimeSpan.FromSeconds(30));
        await service.TickAsync();
        Assert.Equal(1, _source.Reads);

        _time.Advance(TimeSpan.FromSeconds(30));
        await service.TickAsync();
        Assert.Equal(2, _source.Reads);

        // And it recovers by itself.
        _source.ReadFails = null;
        _source.Next = LicenseState.Purchased;
        _time.Advance(LicenseService.Tick);
        await service.TickAsync();
        Assert.Equal(LicenseState.Purchased, service.Current);
    }

    [Fact]
    public async Task ATrialThatTheStoreStillReportsAfterItsEndIsExpired()
    {
        using var service = Service();
        var raised = 0;
        service.Changed += (_, _) => raised++;
        _source.Next = LicenseState.Trial(Start.AddMinutes(-1));

        var state = await service.RefreshAsync();

        Assert.Equal(LicenseState.Expired, state);
        Assert.False(service.Current.AllowsSession);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task AnUnconfirmedPurchaseIsReadEveryMinuteUntilTheLicenseAgrees()
    {
        using var service = Service();
        _source.Next = LicenseState.Trial(Start.AddHours(1));
        await service.RefreshAsync();
        _source.Outcome = PurchaseOutcome.Succeeded;
        _source.AfterPurchase = LicenseState.Trial(Start.AddHours(1)); // not caught up
        var reads = _source.Reads;
        await service.BuyAsync();
        Assert.Equal(reads + 1, _source.Reads);

        _time.Advance(LicenseService.Tick);
        await service.TickAsync();
        Assert.Equal(reads + 2, _source.Reads);

        _source.Next = LicenseState.Purchased;
        _time.Advance(LicenseService.Tick);
        await service.TickAsync();
        Assert.Equal(LicenseState.Purchased, service.Current);

        // Back to the ten minutes.
        _time.Advance(LicenseService.Tick);
        await service.TickAsync();
        Assert.Equal(reads + 3, _source.Reads);
    }

    [Fact]
    public async Task ThePurchaseIsRequestedFromTheCallingThread()
    {
        using var service = Service();
        _source.Outcome = PurchaseOutcome.NotPurchased;

        await service.BuyAsync();

        Assert.Equal(Environment.CurrentManagedThreadId, _source.PurchaseThread);
    }

    [Fact]
    public async Task TheMonitoringLoopKeepsReadingAfterFailedReads()
    {
        using var service = Service();
        _source.ReadFails = new InvalidOperationException("no store");
        service.StartMonitoring();

        // Keep advancing the fake clock until enough reads happened: PeriodicTimer ticks can coalesce on a slow runner.
        await WaitAsync(
            () =>
            {
                _time.Advance(LicenseService.Tick);
                return _source.Reads >= 3;
            },
            "three reads of the license despite failures");
        Assert.True(_source.Reads >= 3, $"only {_source.Reads} reads");
    }

    [Fact]
    public async Task TheFirstReadIsPendingUntilItIsDone()
    {
        using var service = Service();
        Assert.True(service.IsChecking);

        await service.RefreshAsync();

        Assert.False(service.IsChecking);
        using var none = new LicenseService(null, _time);
        Assert.False(none.IsChecking);
    }

    [Fact]
    public void DisposingTheServiceDisposesTheSource()
    {
        var source = new DisposableSource();
        var service = new LicenseService(source, _time);

        service.Dispose();

        Assert.True(source.Disposed);
    }

    private sealed class DisposableSource : ILicenseSource, IDisposable
    {
        public bool Disposed { get; private set; }

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public void AttachWindow(nint handle)
        {
        }

        public Task<LicenseState> ReadAsync() => Task.FromResult(LicenseState.Purchased);

        public Task<PurchaseOutcome> PurchaseAsync() => Task.FromResult(PurchaseOutcome.NotPurchased);

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingPurchase : ILicenseSource
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public void AttachWindow(nint handle)
        {
        }

        public Task<LicenseState> ReadAsync() => Task.FromResult(LicenseState.Unknown);

        public Task<PurchaseOutcome> PurchaseAsync() => throw new InvalidOperationException("boom");
    }
}

/// <summary>The status tab and the session under each license state.</summary>
public sealed class LicenseViewModelTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 8, 5, 0, 0, TimeSpan.Zero);
    private static readonly MirrorStatus Ready = new()
    {
        Running = true,
        Ble = BleState.Connected,
        Receiver = ReceiverState.Showing,
    };

    private readonly TempFolder _dir = new();
    private readonly FakeMirror _mirror = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeLicenseSource _source = new();
    private readonly AppLog _log = new(null);
    private readonly List<Action> _posted = [];
    private LicenseService? _license;

    public void Dispose()
    {
        _license?.Dispose();
        _log.Dispose();
        _dir.Dispose();
    }

    private readonly Lock _lock = new();

    private void Post(Action a)
    {
        lock (_lock)
        {
            _posted.Add(a);
        }
    }

    private async Task<MainViewModel> CreateAsync(LicenseState state, IMirrorService? mirror = null, IReceiverFinder? receivers = null)
    {
        _source.Next = state;
        _license = new LicenseService(_source, _time, _log);
        await _license.RefreshAsync();
        lock (_lock)
        {
            _posted.Clear();
        }

        return new MainViewModel(mirror ?? _mirror, new SettingsStore(_dir.File("settings.json")), _log, new AppSettings(), Post, receivers: receivers, license: _license);
    }

    private async Task RunPostedAsync(MainViewModel vm)
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

        await vm.LicenseChange;
    }

    // The trial runs out: the clock passes the end and the next read says so.
    private async Task EndTheTrialAsync()
    {
        _time.Advance(TimeSpan.FromMinutes(6));
        _source.Next = LicenseState.Expired;
        await _license!.TickAsync();
    }

    /// <summary>A session whose start and stop can be held half way.</summary>
    private sealed class GatedMirror : IMirrorService
    {
        public MirrorStatus Status { get; private set; } = MirrorStatus.Idle;

        public List<string> Calls { get; } = [];

        public TaskCompletionSource? StartGate { get; set; }

        public TaskCompletionSource? StopGate { get; set; }

        public event EventHandler<Rotation>? LandscapeRotationChanged
        {
            add { }
            remove { }
        }

        public async Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Calls.Add("start");
            if (StartGate is { } gate)
            {
                await gate.Task;
            }

            Status = Status with { Running = true };
        }

        public async Task StopAsync()
        {
            Calls.Add("stop");
            if (StopGate is { } gate)
            {
                await gate.Task;
            }

            Status = Status with { Running = false };
        }

        public MirrorStatus GetStatus() => Status;

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

    [Fact]
    public async Task TheTrialEndingWhileTheSessionIsStartingStopsItWhenTheStartIsDone()
    {
        var mirror = new GatedMirror { StartGate = new TaskCompletionSource() };
        var vm = await CreateAsync(LicenseState.Trial(Start.AddMinutes(5)), mirror);

        var start = vm.StartAsync();
        Assert.False(start.IsCompleted);
        await EndTheTrialAsync();
        await RunPostedAsync(vm);
        Assert.Equal(["start"], mirror.Calls); // busy: looked at again later

        mirror.StartGate.SetResult();
        await start;
        await RunPostedAsync(vm);

        Assert.Equal(["start", "stop"], mirror.Calls);
        Assert.False(mirror.GetStatus().Running);
        Assert.True(vm.IsExpired);
    }

    [Fact]
    public async Task TheTrialEndingDuringARestartLetsTheStopThroughAndRefusesTheStart()
    {
        var mirror = new GatedMirror();
        var vm = await CreateAsync(LicenseState.Trial(Start.AddMinutes(5)), mirror);
        await vm.StartAsync();
        mirror.StopGate = new TaskCompletionSource();

        vm.RestartCommand.Execute(null);
        Assert.False(vm.RestartCommand.Last.IsCompleted);
        await EndTheTrialAsync();
        await RunPostedAsync(vm);
        mirror.StopGate.SetResult();
        await vm.RestartCommand.Last;
        await RunPostedAsync(vm);

        Assert.Equal(["start", "stop"], mirror.Calls); // the second start was refused
        Assert.False(mirror.GetStatus().Running);

        // And a purchase starts it again.
        mirror.StopGate = null;
        _source.Next = LicenseState.Purchased;
        await _license!.RefreshAsync();
        await RunPostedAsync(vm);
        Assert.Equal(["start", "stop", "start"], mirror.Calls);
    }

    [Fact]
    public async Task ApplyingSettingsAfterTheTrialEndedStopsButDoesNotRestart()
    {
        var vm = await CreateAsync(LicenseState.Trial(Start.AddMinutes(5)));
        await vm.StartAsync();
        await EndTheTrialAsync(); // the view model has not looked at the change yet
        vm.AirPlayName = "Another name";

        vm.ApplySettingsCommand.Execute(null);
        await vm.ApplySettingsCommand.Last;

        Assert.Equal(["start", "stop"], _mirror.Calls);
        Assert.Equal("Another name", vm.Settings.AirPlayName);
    }

    [Fact]
    public async Task ChoosingAReceiverAfterTheTrialEndedStopsButDoesNotRestart()
    {
        var finder = new FakeFinder();
        finder.Candidates.Add(new ReceiverCandidate("AirServer", ReceiverCandidateKind.Attach, "AirServer", @"C:\Program Files\AirServer\AirServer.exe", true));
        var vm = await CreateAsync(LicenseState.Trial(Start.AddMinutes(5)), receivers: finder);
        await vm.StartAsync();
        for (var i = 0; i < 500 && vm.Setup.Receivers.Count == 0; i++)
        {
            await RunPostedAsync(vm);
            await Task.Delay(10);
        }

        Assert.Single(vm.Setup.Receivers);
        await EndTheTrialAsync();

        vm.Setup.Receivers[0].UseCommand.Execute(null);
        await TestWait.UntilAsync(() => _mirror.Calls.Count >= 2, "the session to be stopped");
        await RunPostedAsync(vm);

        Assert.Equal(["start", "stop"], _mirror.Calls);
        Assert.Equal("AirServer", vm.Settings.AttachProcessName);
    }

    [Theory]
    [InlineData(PurchaseOutcome.NetworkError)]
    [InlineData(PurchaseOutcome.ServerError)]
    [InlineData(PurchaseOutcome.Failed)]
    public async Task APurchaseThatCouldNotBeMadeSaysSo(PurchaseOutcome outcome)
    {
        var vm = await CreateAsync(LicenseState.Expired);
        _source.Outcome = outcome;

        vm.BuyCommand.Execute(null);
        await vm.BuyCommand.Last;

        Assert.True(vm.HasPurchaseMessage);
        Assert.Equal("Microsoft Store の購入を開けませんでした。しばらくしてから、もう一度試してください。", vm.PurchaseMessage);
        Assert.True(vm.IsExpired);
    }

    [Fact]
    public async Task ClosingTheStoreDialogSaysNothing()
    {
        var vm = await CreateAsync(LicenseState.Expired);
        _source.Outcome = PurchaseOutcome.ServerError;
        vm.BuyCommand.Execute(null);
        await vm.BuyCommand.Last;
        Assert.True(vm.HasPurchaseMessage);

        _source.Outcome = PurchaseOutcome.NotPurchased;
        vm.BuyCommand.Execute(null);
        await vm.BuyCommand.Last;

        Assert.False(vm.HasPurchaseMessage);
        Assert.Equal(string.Empty, vm.PurchaseMessage);
    }

    [Fact]
    public async Task ABoughtAppWhoseLicenseDoesNotSayYesIsBeingCheckedUntilItDoes()
    {
        var vm = await CreateAsync(LicenseState.Expired);
        _source.Outcome = PurchaseOutcome.Succeeded;
        _source.AfterPurchase = LicenseState.Expired; // the license has not caught up

        vm.BuyCommand.Execute(null);
        await vm.BuyCommand.Last;

        Assert.Equal("購入を確認しています…", vm.PurchaseMessage);
        Assert.True(vm.IsExpired);
        Assert.Empty(_mirror.Calls);

        // The expired cadence reads every minute; the license catches up.
        _source.Next = LicenseState.Purchased;
        _time.Advance(LicenseService.Tick);
        await _license!.TickAsync();
        await RunPostedAsync(vm);

        Assert.False(vm.IsExpired);
        Assert.Equal(string.Empty, vm.PurchaseMessage);
    }

    [Fact]
    public void EachPurchaseOutcomeHasItsWord()
    {
        Assert.Equal(string.Empty, MainViewModel.DescribePurchase(PurchaseOutcome.NotPurchased, LicenseState.Expired));
        Assert.Equal(string.Empty, MainViewModel.DescribePurchase(PurchaseOutcome.Succeeded, LicenseState.Purchased));
        Assert.Equal("購入を確認しています…", MainViewModel.DescribePurchase(PurchaseOutcome.AlreadyPurchased, LicenseState.Expired));
        Assert.Equal("購入を確認しています…", MainViewModel.DescribePurchase(PurchaseOutcome.Succeeded, LicenseState.Unknown));
        Assert.Equal(
            "Microsoft Store の購入を開けませんでした。しばらくしてから、もう一度試してください。",
            MainViewModel.DescribePurchase(PurchaseOutcome.NetworkError, LicenseState.Expired));
    }

    [Fact]
    public async Task WhileTheFirstReadIsPendingTheStatusSaysTheLicenseIsBeingChecked()
    {
        _source.Next = LicenseState.Purchased;
        _license = new LicenseService(_source, _time, _log);
        var vm = new MainViewModel(_mirror, new SettingsStore(_dir.File("pending.json")), _log, new AppSettings(), Post, license: _license);

        Assert.True(_license.IsChecking);
        Assert.Equal(("ライセンスを確認しています…", StatusLevel.Waiting), (vm.Headline, vm.HeadlineLevel));
        Assert.Equal("ライセンスを確認できしだい、IceScreen Touch を開始します。", vm.Hint);

        await _license.RefreshAsync();
        vm.Refresh();

        Assert.False(_license.IsChecking);
        Assert.Equal("停止中", vm.Headline);
    }

    [Theory]
    [InlineData(LicenseKind.Purchased)]
    [InlineData(LicenseKind.Unknown)]
    [InlineData(LicenseKind.NotPackaged)]
    public async Task EveryStateButExpiredStartsTheSession(LicenseKind kind)
    {
        var vm = await CreateAsync(new LicenseState(kind));

        await vm.StartAsync();

        Assert.Equal(["start"], _mirror.Calls);
        Assert.False(vm.IsTrial);
        Assert.False(vm.IsExpired);
    }

    [Fact]
    public async Task ATrialStartsTheSessionAndShowsTheTimeLeftWithABuyButton()
    {
        var vm = await CreateAsync(LicenseState.Trial(Start.AddMinutes((23 * 60) + 24)));

        await vm.StartAsync();

        Assert.Equal(["start"], _mirror.Calls);
        Assert.True(vm.IsTrial);
        Assert.StartsWith("試用中: あと 23 時間（", vm.TrialText, StringComparison.Ordinal);
        Assert.True(vm.BuyCommand.CanExecute(null));
        Assert.StartsWith("ライセンス: 試用中（", vm.About.LicenseStatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheTrialLineCountsDownEachMinuteAndSwitchesToMinutes()
    {
        var vm = await CreateAsync(LicenseState.Trial(Start.AddMinutes(61)));
        Assert.StartsWith("試用中: あと 1 時間（", vm.TrialText, StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromMinutes(2));
        vm.Refresh();

        Assert.StartsWith("試用中: あと 59 分（", vm.TrialText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndedTrialRefusesEveryStartAndSaysSo()
    {
        var vm = await CreateAsync(LicenseState.Expired);

        await vm.StartAsync();
        await vm.RestartCommand.Last;
        vm.RestartCommand.Execute(null);
        vm.StartStopCommand.Execute(null);

        Assert.Empty(_mirror.Calls);
        Assert.True(vm.IsExpired);
        Assert.False(vm.IsTrial);
        Assert.Equal(("試用期間が終わりました", StatusLevel.Error), (vm.Headline, vm.HeadlineLevel));
        Assert.True(vm.HeadlineError);
        Assert.Equal("引き続き使うには、Microsoft Store で購入してください。購入すると、すぐに使えるようになります。", vm.Hint);
        Assert.False(vm.StartStopCommand.CanExecute(null));
        Assert.False(vm.RestartCommand.CanExecute(null));
        Assert.True(vm.BuyCommand.CanExecute(null));
        Assert.Equal("ライセンス: 試用期間の終了", vm.About.LicenseStatusText);
    }

    [Fact]
    public async Task TheTrialEndingWhileRunningStopsTheSessionAndShowsTheEndedState()
    {
        var vm = await CreateAsync(LicenseState.Trial(Start.AddMinutes(5)));
        await vm.StartAsync();
        _mirror.Status = Ready with { Running = true };
        vm.Refresh();
        Assert.True(vm.IsRunning);

        _time.Advance(TimeSpan.FromMinutes(6));
        _source.Next = LicenseState.Expired;
        await _license!.TickAsync();
        await RunPostedAsync(vm);

        Assert.Equal(["start", "stop"], _mirror.Calls);
        Assert.False(vm.IsRunning);
        Assert.True(vm.IsExpired);
        Assert.Equal("試用期間が終わりました", vm.Headline);
        Assert.Equal(string.Empty, vm.TrialText);
        Assert.False(vm.StartStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task APurchaseLiftsTheBlockAndStartsTheSessionWithoutARestart()
    {
        var vm = await CreateAsync(LicenseState.Expired);
        await vm.StartAsync();
        Assert.Empty(_mirror.Calls);
        _source.Outcome = PurchaseOutcome.Succeeded;

        vm.BuyCommand.Execute(null);
        await vm.BuyCommand.Last;
        await RunPostedAsync(vm);

        Assert.Equal(1, _source.Purchases);
        Assert.Equal(["start"], _mirror.Calls);
        Assert.False(vm.IsExpired);
        Assert.Equal("ライセンス: 購入済み", vm.About.LicenseStatusText);
        Assert.True(vm.StartStopCommand.CanExecute(null));
    }

    [Fact]
    public async Task APurchaseAfterTheSessionWasStoppedByTheLicenseRestartsIt()
    {
        var vm = await CreateAsync(LicenseState.Trial(Start.AddMinutes(5)));
        await vm.StartAsync();
        _time.Advance(TimeSpan.FromMinutes(6));
        _source.Next = LicenseState.Expired;
        await _license!.TickAsync();
        await RunPostedAsync(vm);
        Assert.Equal(["start", "stop"], _mirror.Calls);
        _source.Outcome = PurchaseOutcome.AlreadyPurchased;

        vm.BuyCommand.Execute(null);
        await vm.BuyCommand.Last;
        await RunPostedAsync(vm);

        Assert.Equal(["start", "stop", "start"], _mirror.Calls);
    }

    [Fact]
    public async Task APurchaseThatDoesNotHappenLeavesTheEndedState()
    {
        var vm = await CreateAsync(LicenseState.Expired);
        await vm.StartAsync();
        _source.Outcome = PurchaseOutcome.NetworkError;

        vm.BuyCommand.Execute(null);
        await vm.BuyCommand.Last;
        await RunPostedAsync(vm);

        Assert.Empty(_mirror.Calls);
        Assert.True(vm.IsExpired);
        Assert.True(vm.BuyCommand.CanExecute(null)); // try again
    }

    [Fact]
    public async Task ALicenseThatBecomesPurchasedWhileTheSessionWasStoppedByTheUserDoesNotStartIt()
    {
        var vm = await CreateAsync(LicenseState.Trial(Start.AddHours(1)));
        await vm.StartAsync();
        await vm.StopAsync();
        _mirror.Calls.Clear();

        _source.Next = LicenseState.Purchased;
        await _license!.RefreshAsync();
        await RunPostedAsync(vm);

        Assert.Empty(_mirror.Calls);
        Assert.False(vm.IsTrial);
    }

    [Fact]
    public async Task WithoutALicenseServiceTheViewModelIsUnrestricted()
    {
        var vm = new MainViewModel(_mirror, new SettingsStore(_dir.File("plain.json")), _log, new AppSettings(), _posted.Add);

        await vm.StartAsync();

        Assert.Equal(["start"], _mirror.Calls);
        Assert.Equal("ライセンス: 自分でビルドしたもの（ライセンスの確認なし）", vm.About.LicenseStatusText);
        Assert.False(vm.IsTrial);
        Assert.False(vm.IsExpired);
    }
}
