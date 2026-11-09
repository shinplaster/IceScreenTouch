// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.App.Logging;

namespace IceScreenTouch.App.Services;

/// <summary>
/// Keeps the current <see cref="LicenseState"/>: reads it from the platform's <see cref="ILicenseSource"/> at start,
/// when the platform says it changed, and on a timer (the trial is short), and raises <see cref="Changed"/> when it
/// differs. Without a source (a build of your own) the state is <see cref="LicenseState.NotPackaged"/> for good and
/// nothing runs. Thread-safe; <see cref="Changed"/> comes from any thread.
/// </summary>
public sealed class LicenseService : IDisposable
{
    /// <summary>How often the license is read while it is fine.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The timer's tick, and how often the license is read again while it is expired, unknown, past the trial's end or
    /// waiting for a purchase to show up.
    /// </summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>How long one read may take before the state counts as unknown.</summary>
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(20);

    private readonly ILicenseSource? _source;
    private readonly TimeProvider _time;
    private readonly AppLog? _log;
    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private volatile LicenseState _current;
    private long _lastReadTicks;
    private volatile bool _hasRead;
    private volatile bool _expectPurchase;
    private Task? _loop;

    /// <summary>Creates the service; <paramref name="source"/> null = a build of your own (no license check).</summary>
    public LicenseService(ILicenseSource? source, TimeProvider? time = null, AppLog? log = null)
    {
        _source = source;
        _time = time ?? TimeProvider.System;
        _log = log;
        _current = source is null ? LicenseState.NotPackaged : LicenseState.Unknown;
        _hasRead = source is null;
        _lastReadTicks = _time.GetUtcNow().UtcTicks;
        if (source is not null)
        {
            source.Changed += OnSourceChanged;
        }
    }

    /// <summary>Raised (on any thread) when <see cref="Current"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The license as last read (<see cref="LicenseState.Unknown"/> until the first read).</summary>
    public LicenseState Current => _current;

    /// <summary>The first read has not finished yet (never true without a source): <see cref="Current"/> is a guess.</summary>
    public bool IsChecking => !_hasRead;

    /// <summary>The clock the service uses (UTC).</summary>
    public DateTimeOffset UtcNow => _time.GetUtcNow();

    /// <summary>The time zone the service shows times in.</summary>
    public TimeZoneInfo LocalTimeZone => _time.LocalTimeZone;

    /// <summary>Passes the main window's handle to the store (see <see cref="ILicenseSource.AttachWindow"/>).</summary>
    public void AttachWindow(nint handle) => _source?.AttachWindow(handle);

    /// <summary>Reads the license now and returns it. Never throws; a read that fails or hangs gives Unknown.</summary>
    public async Task<LicenseState> RefreshAsync()
    {
        if (_source is null)
        {
            return _current;
        }

        await _reading.WaitAsync().ConfigureAwait(false);
        try
        {
            var state = await ReadWithTimeoutAsync(_source).ConfigureAwait(false);
            Interlocked.Exchange(ref _lastReadTicks, _time.GetUtcNow().UtcTicks);
            Apply(state);
            _hasRead = true;
            return _current;
        }
        finally
        {
            _reading.Release();
        }
    }

    /// <summary>
    /// Starts the purchase and, when the store says the user owns the app, reads the license again. Returns how the
    /// request ended; the new state is in <see cref="Current"/>. Never throws. Call it on the UI thread: the call
    /// reaches <see cref="ILicenseSource.PurchaseAsync"/> before the first await, so the store's dialog is requested from
    /// the calling thread.
    /// </summary>
    public async Task<PurchaseOutcome> BuyAsync()
    {
        if (_source is null)
        {
            return PurchaseOutcome.Failed;
        }

        PurchaseOutcome outcome;
        try
        {
            outcome = await _source.PurchaseAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failing store must never crash the app.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Write($"[lic ] purchase failed: {ex.GetType().Name}");
            outcome = PurchaseOutcome.Failed;
        }

        _log?.Write($"[lic ] purchase: {outcome}");
        if (outcome is PurchaseOutcome.Succeeded or PurchaseOutcome.AlreadyPurchased)
        {
            var state = await RefreshAsync().ConfigureAwait(false);

            // The store says the app is bought but the license still says no: read it every minute until it agrees.
            _expectPurchase = state.Kind != LicenseKind.Purchased;
        }

        return outcome;
    }

    /// <summary>Starts the timer (once). Does nothing without a source.</summary>
    public void StartMonitoring()
    {
        if (_source is null || _loop is not null)
        {
            return;
        }

        _loop = RunAsync(_stop.Token);
    }

    /// <summary>
    /// One timer tick: reads the license when it is due (every <see cref="RecheckInterval"/>; every
    /// <see cref="Tick"/> while it is expired or unknown, when the trial's end has passed, or while a purchase has not
    /// shown up yet, since those are the moments that matter). Public for tests; the timer calls it.
    /// </summary>
    public async Task TickAsync()
    {
        var now = _time.GetUtcNow();
        var sinceRead = now - new DateTimeOffset(Interlocked.Read(ref _lastReadTicks), TimeSpan.Zero);
        var state = _current;
        var soon = state.Kind is LicenseKind.Expired or LicenseKind.Unknown
            || (state.Kind == LicenseKind.Trial && state.Expiration <= now)
            || _expectPurchase;
        if (sinceRead >= RecheckInterval || (soon && sinceRead >= Tick))
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_source is not null)
        {
            _source.Changed -= OnSourceChanged;
        }

        _stop.Cancel();
        (_source as IDisposable)?.Dispose();
    }

    private static string Describe(LicenseState state) =>
        state.Expiration is { } end ? $"{state.Kind}, until {end.UtcDateTime:yyyy-MM-dd HH:mm} UTC" : state.Kind.ToString();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(Tick, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await TickAsync().ConfigureAwait(false);
                }
#pragma warning disable CA1031 // One bad tick must not stop the checks for good.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _log?.Write($"[lic ] license check failed: {ex.GetType().Name}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposed.
        }
    }

    private async Task<LicenseState> ReadWithTimeoutAsync(ILicenseSource source)
    {
        // The timeout is cancelled as soon as the read is done, so no timer lingers for 20 seconds per read.
        using var finished = new CancellationTokenSource();
        try
        {
            var read = source.ReadAsync();
            var timeout = Task.Delay(ReadTimeout, _time, finished.Token);
            var first = await Task.WhenAny(read, timeout).ConfigureAwait(false);
            if (first == read)
            {
                await finished.CancelAsync().ConfigureAwait(false);
                return await read.ConfigureAwait(false);
            }

            _log?.Write("[lic ] reading the license timed out");
            return LicenseState.Unknown;
        }
#pragma warning disable CA1031 // Unknown fails open: the session keeps running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Write($"[lic ] reading the license failed: {ex.GetType().Name}");
            return LicenseState.Unknown;
        }
    }

    // A read that failed (Unknown) does not erase what an earlier read said: Purchased and Expired stay, and a trial
    // stays until its end passes. Only before any good read is Unknown the state, and that one is usable. Otherwise
    // blocking the Store service would bring an ended trial back. A trial whose end has passed is Expired whatever
    // the store still says (a stale answer).
    private LicenseState Normalize(LicenseState read)
    {
        var state = read;
        if (read.Kind == LicenseKind.Unknown && _current.Kind is LicenseKind.Purchased or LicenseKind.Expired or LicenseKind.Trial)
        {
            state = _current;
        }

        return state.Kind == LicenseKind.Trial && state.Expiration <= _time.GetUtcNow() ? LicenseState.Expired : state;
    }

    private void Apply(LicenseState read)
    {
        var state = Normalize(read);
        var before = Interlocked.Exchange(ref _current, state);
        if (before == state)
        {
            return;
        }

        if (state.Kind == LicenseKind.Purchased)
        {
            _expectPurchase = false;
        }

        // No personal data: the kind and the end of the trial only.
        _log?.Write($"[lic ] license: {Describe(state)}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSourceChanged(object? sender, EventArgs e) => _ = RefreshAsync();
}
