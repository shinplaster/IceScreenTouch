// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Services;

/// <summary>What the automation API is doing, for the status tab and the tray.</summary>
public enum ApiRunState
{
    /// <summary>The setting is off: nothing listens.</summary>
    Off,

    /// <summary>The setting is on but the license does not include the API (trial, ended): nothing listens.</summary>
    NotAllowed,

    /// <summary>The setting is on but the Store license could not be read (<see cref="LicenseKind.Unknown"/>): nothing listens until it is read.</summary>
    LicenseUnknown,

    /// <summary>Listening on 127.0.0.1.</summary>
    Listening,

    /// <summary>A request or a WebSocket session of a script is operating the device.</summary>
    Operating,

    /// <summary>The port is used by another program.</summary>
    PortInUse,

    /// <summary>The server could not start for another reason (<see cref="ApiServiceStatus.Error"/>).</summary>
    Failed,
}

/// <summary>The state of <see cref="ApiService"/>.</summary>
/// <param name="State">What it is doing.</param>
/// <param name="Port">The port in use (<see cref="ApiRunState.Listening"/>, <see cref="ApiRunState.Operating"/>) or the one that could not be used.</param>
/// <param name="Error">Why it could not start (<see cref="ApiRunState.Failed"/>), in English; never the token.</param>
public sealed record ApiServiceStatus(ApiRunState State, int Port, string? Error = null)
{
    /// <summary>The setting is off.</summary>
    public static ApiServiceStatus Off { get; } = new(ApiRunState.Off, 0);

    /// <summary>The server listens (an operation may be running).</summary>
    public bool IsListening => State is ApiRunState.Listening or ApiRunState.Operating;
}

/// <summary>
/// Runs the automation API for the application's lifetime: the <see cref="ApiController"/> and the <see cref="ApiServer"/> on
/// 127.0.0.1, while the setting is on AND the license includes the API (docs/API-DESIGN.md, chapter 5). It is independent of
/// the mirror session: the server keeps listening while the session is stopped or restarted (requests get <c>not_running</c>),
/// and it reads the current session through <see cref="IMirrorService.ApiSession"/> on every request.
/// <para>
/// The token lives in its own file (<see cref="ApiTokenStore"/>), is created the first time the API is switched on and changes
/// only by <see cref="RegenerateTokenAsync"/>. <c>api.json</c> (port, token, pid) exists while the server listens. Nothing in
/// the log carries a token, coordinates, keys or text. Thread-safe; the state is reconciled one change at a time.
/// </para>
/// </summary>
public sealed class ApiService : IDisposable
{
    private readonly LicenseService _license;
    private readonly ApiTokenStore _tokens;
    private readonly ApiConnectionFile _connectionFile;
    private readonly ApiHost _host;
    private readonly TimeProvider _time;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _stateLock = new();
    private readonly bool _anyPort;
    private bool _enabled;
    private int _port = ApiServer.DefaultPort;
    private bool _closed;
    private bool _cleaned;
    private string? _token;
    private ApiController? _controller;
    private ApiServer? _server;
    private bool _connectionFileStale; // api.json could not be rewritten after a new token; the next reconcile tries again
    private int _runningFor; // the port that was asked for (0 = any, in tests), which the running server was started with
    private ApiServiceStatus _status = ApiServiceStatus.Off;

    /// <summary>Creates the service; nothing listens until <see cref="ConfigureAsync"/>.</summary>
    /// <param name="mirror">The session: the API reads its current <see cref="IMirrorService.ApiSession"/>.</param>
    /// <param name="license">The license (<see cref="Allows"/>); a change is followed at once.</param>
    /// <param name="dataDirectory">Where <c>api-token</c> and <c>api.json</c> go (the app's data folder).</param>
    /// <param name="settings">The settings in effect (the saved landscape rotation, for a request while no session runs).</param>
    /// <param name="log">The app log (lines start with <c>[api]</c>).</param>
    /// <param name="time">The clock of the controller and of the session's input arbitration (pass the same one to the session).</param>
    /// <param name="anyPort">Port 0 ("any free port") is accepted: for tests and the self-check, never for a user's setting.</param>
    public ApiService(IMirrorService mirror, LicenseService license, string dataDirectory, Func<AppSettings> settings, Action<string>? log = null, TimeProvider? time = null, bool anyPort = false)
    {
        ArgumentNullException.ThrowIfNull(mirror);
        ArgumentNullException.ThrowIfNull(license);
        ArgumentNullException.ThrowIfNull(settings);
        _license = license;
        _log = log;
        _time = time ?? TimeProvider.System;
        _tokens = new ApiTokenStore(dataDirectory, log);
        _connectionFile = new ApiConnectionFile(dataDirectory, log: log);
        _host = new ApiHost(mirror, settings, _time, log);
        _host.LandscapeRotationSetWhileStopped += r => LandscapeRotationSetWhileStopped?.Invoke(r);
        _host.TargetChosen += id => TargetChosen?.Invoke(id);
        _anyPort = anyPort;
        _license.Changed += OnLicenseChanged;
    }

    /// <summary>Raised (on any thread) when a request set the landscape rotation while no session ran: save it with the settings.</summary>
    public event Action<Rotation>? LandscapeRotationSetWhileStopped;

    /// <summary>Raised (on any thread) when a request chose the target device (null = automatic): save the choice with the settings.</summary>
    public event Action<string?>? TargetChosen;

    /// <summary>
    /// The API is a paid feature: only <see cref="LicenseKind.Purchased"/> and <see cref="LicenseKind.NotPackaged"/> (a build of
    /// your own) include it. The trial, the ended license and a license that could not be read (<see cref="LicenseKind.Unknown"/>)
    /// do not, unlike the session, which fails open on Unknown (<see cref="LicenseState.AllowsSession"/>).
    /// </summary>
    public static bool Allows(LicenseState license)
    {
        ArgumentNullException.ThrowIfNull(license);
        return license.Kind is LicenseKind.Purchased or LicenseKind.NotPackaged;
    }

    /// <summary>
    /// The current license includes the API AND has been read: until the first read finishes (the Store build) the state is only a
    /// guess (Unknown), and a trial user must not get a window in which the server runs.
    /// </summary>
    public bool IsAllowed => !_license.IsChecking && Allows(_license.Current);

    /// <summary>What it is doing now (an operation in progress turns <see cref="ApiRunState.Listening"/> into <see cref="ApiRunState.Operating"/>).</summary>
    public ApiServiceStatus Status
    {
        get
        {
            ApiServiceStatus status;
            ApiController? controller;
            lock (_stateLock)
            {
                status = _status;
                controller = _controller;
            }

            return status.State == ApiRunState.Listening && controller is { IsOperating: true } ? status with { State = ApiRunState.Operating } : status;
        }
    }

    /// <summary>The host the controller runs on (tests drive it directly).</summary>
    internal IApiHost Host => _host;

    /// <summary>The current token, or null before the API was first switched on.</summary>
    public string? Token
    {
        get
        {
            lock (_stateLock)
            {
                return _token;
            }
        }
    }

    /// <summary>The path of <c>api.json</c> as this process writes it (a packaged app's path is redirected for other programs, see <see cref="AppPaths.ForExternalProgram"/>).</summary>
    public string ConnectionFilePath => _connectionFile.Path;

    /// <summary>
    /// Sets what the user wants and makes it so: starts, restarts (another port, or a retry after a failed start) or stops the
    /// server. Never throws; a failure shows in <see cref="Status"/> and the log.
    /// </summary>
    /// <param name="enabled">The setting.</param>
    /// <param name="port">The port (<see cref="AppSettings.IsValidApiPort"/>; anything else is the default).</param>
    public Task ConfigureAsync(bool enabled, int port)
    {
        lock (_stateLock)
        {
            _enabled = enabled;
            _port = AppSettings.IsValidApiPort(port) || (_anyPort && port == 0) ? port : ApiServer.DefaultPort;
        }

        return ReconcileAsync(retryFailed: true);
    }

    /// <summary>
    /// Makes a new token. A running server takes it at once (the old token stops working and <c>api.json</c> is rewritten); a
    /// stopped one keeps it for the next start. The result says what happened, and the log says it in one distinct line each:
    /// <see cref="TokenRegeneration.Failed"/> (the token file could not be written: nothing changed),
    /// <see cref="TokenRegeneration.ConnectionFileStale"/> (the new token is in force but <c>api.json</c> still holds the old one;
    /// it is written again at the next <see cref="ConfigureAsync"/>) or <see cref="TokenRegeneration.Done"/>.
    /// </summary>
    public async Task<TokenRegeneration> RegenerateTokenAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string token;
            try
            {
                token = _tokens.Regenerate();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"[api] the token was NOT regenerated: cannot write the token file ({ex.GetType().Name})");
                return TokenRegeneration.Failed;
            }

            lock (_stateLock)
            {
                _token = token;
            }

            if (_server is { IsRunning: true } server && !await WriteConnectionFileAsync(server, token).ConfigureAwait(false))
            {
                _log?.Invoke("[api] token regenerated, but api.json could not be updated (it still holds the old token); it is written again at the next apply");
                return TokenRegeneration.ConnectionFileStale;
            }

            _log?.Invoke("[api] token regenerated");
            return TokenRegeneration.Done;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Gives the server the token (it rewrites api.json). False when api.json could not be written: the server uses the token
    // all the same, and the file is tried again by the next reconcile.
    private async Task<bool> WriteConnectionFileAsync(ApiServer server, string token)
    {
        try
        {
            await server.SetTokenAsync(token).ConfigureAwait(false);
            _connectionFileStale = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _connectionFileStale = true;
            _log?.Invoke($"[api] cannot write api.json: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>Stops the server for good (the application exits; it cannot be started again): a running operation is cancelled, every finger and key lifted, <c>api.json</c> deleted. Awaited by the exit path. Idempotent.</summary>
    public async Task StopAsync()
    {
        _license.Changed -= OnLicenseChanged;
        lock (_stateLock)
        {
            _closed = true;
        }

        await ReconcileAsync(retryFailed: false).ConfigureAwait(false);
    }

    /// <summary>Releases the service's lock; call after <see cref="StopAsync"/>.</summary>
    public void Dispose() => _gate.Dispose();

    private void OnLicenseChanged(object? sender, EventArgs e)
    {
        // A purchase (or a license that is read at last) makes the API usable at once; the end of the trial or of the license stops the server.
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(retryFailed: false).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A license event must never take the app down; the state shows in the status.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Invoke($"[api] cannot follow the license change: {ex.GetType().Name}");
            }
        });
    }

    // Brings the server in line with (setting on && license allows && not closing). A server that runs on the right port is left alone.
    private async Task ReconcileAsync(bool retryFailed)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool enabled;
            int port;
            bool closed;
            lock (_stateLock)
            {
                (enabled, port, closed) = (_enabled, _port, _closed);
            }

            var allowed = IsAllowed;
            if (closed || !enabled || !allowed)
            {
                await StopServerAsync().ConfigureAwait(false);
                CleanLeftoverOnce();
                // While the first license read is pending nothing is said (it is not a refusal yet).
                SetStatus(!closed && enabled && !allowed && !_license.IsChecking
                    ? new ApiServiceStatus(_license.Current.Kind == LicenseKind.Unknown ? ApiRunState.LicenseUnknown : ApiRunState.NotAllowed, port)
                    : ApiServiceStatus.Off);
                return;
            }

            if (_server is { IsRunning: true } running && _runningFor == port)
            {
                if (_connectionFileStale && Token is { } current && await WriteConnectionFileAsync(running, current).ConfigureAwait(false))
                {
                    _log?.Invoke("[api] api.json was updated with the current token");
                }

                return;
            }

            if (_server is null && !retryFailed && _status is { State: ApiRunState.PortInUse or ApiRunState.Failed } failed && failed.Port == port)
            {
                return; // a license event is no reason to try a port that was just refused again
            }

            await StopServerAsync().ConfigureAwait(false);
            await StartServerAsync(port).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartServerAsync(int port)
    {
        string token;
        try
        {
            token = _tokens.GetOrCreate();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable token file is not replaced (that would log every script out for a hiccup): the start fails and
            // applying again tries once more.
            _log?.Invoke($"[api] cannot read or write the token file ({ex.GetType().Name}); the API did not start");
            SetStatus(new ApiServiceStatus(ApiRunState.Failed, port, "cannot read or write the token file"));
            return;
        }

        var controller = new ApiController(_host, () => IsAllowed, _time, _log);
        var server = new ApiServer(controller, token, port, _connectionFile, _log, _time);
        ApiServerStartResult result;
        try
        {
            result = await server.StartAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Whatever the server throws, the app stays up and the status says it did not start.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _log?.Invoke($"[api] server could not start: {ex.GetType().Name}");
            result = new ApiServerStartResult(false, 0, ex.Message, false);
        }

        lock (_stateLock)
        {
            _token = token;
        }

        if (!result.IsSuccess)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            controller.Dispose();
            if (!result.PortInUse)
            {
                // The details only go to the log; the status tab and the tray say a fixed sentence.
                _log?.Invoke($"[api] could not start: {result.Error}");
            }

            SetStatus(new ApiServiceStatus(result.PortInUse ? ApiRunState.PortInUse : ApiRunState.Failed, port, result.Error));
            return;
        }

        lock (_stateLock)
        {
            _controller = controller;
            _server = server;
            _runningFor = port;
        }

        SetStatus(new ApiServiceStatus(ApiRunState.Listening, result.Port));
    }

    private async Task StopServerAsync()
    {
        ApiServer? server;
        ApiController? controller;
        lock (_stateLock)
        {
            (server, controller) = (_server, _controller);
            _server = null;
            _controller = null;
        }

        if (server is not null)
        {
            try
            {
                await server.DisposeAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Stopping goes on: the controller below still lifts every finger and key.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _log?.Invoke($"[api] stopping the server failed: {ex.GetType().Name}");
            }
        }

        controller?.Dispose();
    }

    // An api.json left by a crash holds a valid token for nothing: with the API off it must not stay. (The app runs once per
    // user, so no other process owns it.)
    private void CleanLeftoverOnce()
    {
        if (_cleaned)
        {
            return;
        }

        _cleaned = true;
        try
        {
            _connectionFile.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"[api] cannot remove an old api.json: {ex.GetType().Name}");
        }
    }

    private void SetStatus(ApiServiceStatus status)
    {
        lock (_stateLock)
        {
            _status = status;
        }
    }
}

/// <summary>What <see cref="ApiService.RegenerateTokenAsync"/> achieved.</summary>
public enum TokenRegeneration
{
    /// <summary>The token file could not be written: the old token is still in force.</summary>
    Failed,

    /// <summary>The new token is in force everywhere.</summary>
    Done,

    /// <summary>The new token is in force in the server and in the token file, but <c>api.json</c> still holds the old one (written again at the next apply).</summary>
    ConnectionFileStale,
}
