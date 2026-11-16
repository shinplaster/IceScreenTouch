// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace IceScreenTouch.Api;

/// <summary>One route of the API server. Used to add routes beyond the built-in ones (see <see cref="ApiServer.AddRoute"/>).</summary>
/// <param name="Method">The HTTP method, upper case (<c>GET</c>, <c>PUT</c>, <c>POST</c>).</param>
/// <param name="Path">The exact path (case-sensitive, no query, no trailing slash), for example <c>/v1/status</c>.</param>
/// <param name="Handler">
/// The handler. By the time it runs the request has passed the Origin and Host checks, the token check (unless
/// <paramref name="Anonymous"/>), and the content-type and size checks.
/// </param>
/// <param name="Anonymous">
/// True when the route needs no <c>Authorization</c> header (<c>GET /v1/openapi.json</c>, and a WebSocket that takes
/// the token in its first message). The Origin and Host checks still apply.
/// </param>
public sealed record ApiRoute(string Method, string Path, RequestDelegate Handler, bool Anonymous = false);

/// <summary>The outcome of <see cref="ApiServer.StartAsync"/>.</summary>
/// <param name="IsSuccess">The server listens.</param>
/// <param name="Port">The bound port (0 on failure).</param>
/// <param name="Error">A sentence for the user when it failed; null on success.</param>
/// <param name="PortInUse">The failure is that another program already listens on the port.</param>
public sealed record ApiServerStartResult(bool IsSuccess, int Port, string? Error, bool PortInUse);

/// <summary>
/// The HTTP server of the automation API (docs/API-DESIGN.md, chapters 4, 7 and 10). It listens on 127.0.0.1 only, answers
/// JSON (errors as <c>application/problem+json</c>) and hands every operation to the <see cref="ApiController"/>.
/// A request passes, in this order: the Origin check (any <c>Origin</c> header is 403 <c>forbidden_origin</c>, there is no
/// CORS), the Host check (only <c>127.0.0.1:port</c> with the bound port, else 403
/// <c>forbidden_origin</c>), the token check (<c>Authorization: Bearer</c>, else 401 <c>unauthorized</c>; not for an
/// anonymous route), the route lookup (404 <c>not_found</c>, 405 <c>method_not_allowed</c>), and for a request with a body
/// the checks of the size (at most <see cref="MaxBodyBytes"/>) and the content type (<c>application/json</c>); a body
/// that does not fit the endpoint is 400 <c>invalid_request</c>. An unexpected exception is 500 <c>internal_error</c>
/// with a generic detail.
/// </summary>
/// <remarks>
/// Success responses are <c>200</c> with a JSON body (the screenshot is an image): <see cref="EmptyResponse"/> (<c>{}</c>) for plain operations,
/// <see cref="ApiStatus"/>, <see cref="RotationInfo"/>, <see cref="TypeTextResponse"/>, <see cref="CancelResponse"/> as
/// listed on the routes. Every response carries <c>Cache-Control: no-store</c> and <c>X-Content-Type-Options: nosniff</c>.
/// </remarks>
public sealed class ApiServer : IAsyncDisposable
{
    /// <summary>The default port (docs/API-DESIGN.md, chapter 4).</summary>
    public const int DefaultPort = 47821;

    /// <summary>The largest request body (64 KB).</summary>
    public const int MaxBodyBytes = 64 * 1024;

    private const string BearerPrefix = "Bearer ";

    private static readonly byte[] OpenApiDocument = LoadOpenApi();

    private readonly ApiController _controller;
    private readonly int _requestedPort;
    private readonly ApiConnectionFile? _connectionFile;
    private readonly Action<string>? _log;
    private readonly RejectionLog _rejections;
    private readonly ApiStreamEndpoint _streams;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly List<ApiRoute> _routes = [];
    private volatile TokenState _token;
    private volatile Dictionary<string, Dictionary<string, ApiRoute>> _table = [];
    private WebApplication? _app;
    private volatile int _port;
    private volatile bool _stopping;

    /// <summary>Creates the server (not started).</summary>
    /// <param name="controller">Runs the operations.</param>
    /// <param name="token">The bearer token, in the format of <see cref="ApiToken.Generate"/> (anything else is an <see cref="ArgumentException"/>).</param>
    /// <param name="port">The port on 127.0.0.1; 0 takes any free port (tests).</param>
    /// <param name="connectionFile">Written after the server has bound, deleted when it stops; null for none.</param>
    /// <param name="log">Receives the server's log lines (start, stop, bind failure, rejected requests by category).</param>
    /// <param name="time">The clock for the rate limit of the rejection log.</param>
    public ApiServer(
        ApiController controller,
        string token,
        int port = DefaultPort,
        ApiConnectionFile? connectionFile = null,
        Action<string>? log = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _controller = controller;
        _token = TokenState.Of(token);
        _requestedPort = port;
        _connectionFile = connectionFile;
        _log = log;
        time ??= TimeProvider.System;
        _rejections = new RejectionLog(log, time);
        _streams = new ApiStreamEndpoint(controller, time, log, _rejections, VerifyToken, () => _token.Generation);
        RegisterBuiltInRoutes();
    }

    /// <summary>The bound port, or 0 while the server is not running.</summary>
    public int Port => _port;

    /// <summary>The server listens.</summary>
    public bool IsRunning => _app is not null;

    /// <summary>The WebSocket streams that are open now (those still waiting for their <c>auth</c> included).</summary>
    public int OpenStreams => _streams.Count;

    internal ApiStreamEndpoint Streams => _streams;

    /// <summary>The current token.</summary>
    public string Token => _token.Value;

    /// <summary>The registered routes as <c>"GET /v1/status"</c>, in registration order (for the OpenAPI cross-check).</summary>
    public IReadOnlyList<string> Routes
    {
        get
        {
            lock (_routes)
            {
                return [.. _routes.Select(r => $"{r.Method} {r.Path}")];
            }
        }
    }

    /// <summary>
    /// Adds a route (beyond the built-in ones). Only before <see cref="StartAsync"/>;
    /// the same method and path twice is an error.
    /// </summary>
    public void AddRoute(ApiRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentException.ThrowIfNullOrEmpty(route.Method);
        ArgumentException.ThrowIfNullOrEmpty(route.Path);
        if (IsRunning)
        {
            throw new InvalidOperationException("routes cannot be added while the server runs");
        }

        lock (_routes)
        {
            if (_routes.Any(r => r.Method == route.Method && r.Path == route.Path))
            {
                throw new ArgumentException($"{route.Method} {route.Path} is already registered", nameof(route));
            }

            _routes.Add(route);
        }
    }

    /// <summary>
    /// Replaces the token (it must have the format of <see cref="ApiToken.Generate"/>); it applies to the next request (a
    /// request being handled keeps the decision it got). Serialized with start and stop. While
    /// the server runs the connection file is rewritten (an <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> from that is thrown after the token has changed). Every open WebSocket stream
    /// (it authenticated with the old token) is ended: everything it holds is released and it is closed with 1008.
    /// </summary>
    public async Task SetTokenAsync(string token)
    {
        var state = TokenState.Of(token);
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            state = state with { Generation = _token.Generation + 1 };
            _token = state;
            if (_app is not null)
            {
                _connectionFile?.Write(_port, token);
            }
        }
        finally
        {
            _lifecycle.Release();
            await _streams.CloseStaleAsync(state.Generation).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Starts listening on 127.0.0.1 (no other address, no other port: a port in use is a failure, never a reason to
    /// look for another). Then writes the connection file. Idempotent: when the server already runs it returns the
    /// current port. Cancelling the token before the bind finishes throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task<ApiServerStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_app is not null)
            {
                return new ApiServerStartResult(true, _port, null, false);
            }

            // A connection file left by an earlier run (a crash, a kill) points at a token and a port that mean nothing
            // now: remove it (and the temporary files of a write that was cut short) before anything is bound, so it stays
            // absent when the bind fails.
            if (_connectionFile is not null && !_connectionFile.Delete())
            {
                _log?.Invoke("[api] connection file could not be deleted");
                return new ApiServerStartResult(false, 0, $"an old API connection file in {_connectionFile.Directory} could not be removed", false);
            }

            _stopping = false;
            _table = BuildTable();
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });

            // Nothing from the environment or a config file may add an endpoint or change the limits: the host reads
            // no configuration at all (and the start checks the bound address below).
            builder.Configuration.Sources.Clear();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(o =>
            {
                o.AddServerHeader = false;
                o.Limits.MaxRequestBodySize = MaxBodyBytes;
                o.Limits.MaxConcurrentConnections = 64;
                o.Limits.MaxConcurrentUpgradedConnections = 2 * ApiStreamEndpoint.MaxStreams; // above our own count, which refuses with a 409 first
                o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
                o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
                o.Listen(IPAddress.Loopback, _requestedPort, listen => listen.Protocols = HttpProtocols.Http1);
            });
            var app = builder.Build();

            // The WebSocket feature only; the Origin, Host and token checks of HandleAsync come before the handshake is accepted.
            app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15), KeepAliveTimeout = TimeSpan.FromSeconds(10) });
            app.Run(HandleAsync);

            try
            {
                await app.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                await app.DisposeAsync().ConfigureAwait(false);
                var inUse = IsAddressInUse(ex);
                var error = inUse
                    ? $"port {_requestedPort} is already in use by another program"
                    : $"the API could not listen on 127.0.0.1:{_requestedPort}: {ex.Message}";
                _log?.Invoke($"[api] server could not start: {(inUse ? "port in use" : "bind failed")} ({_requestedPort})");
                return new ApiServerStartResult(false, 0, error, inUse);
            }

            var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToList();
            if (addresses.Count != 1 || !addresses[0].StartsWith("http://127.0.0.1:", StringComparison.Ordinal))
            {
                // Something outside the code added an endpoint: never serve the API on it.
                await StopHostAsync(app).ConfigureAwait(false);
                _log?.Invoke("[api] server could not start: unexpected listening address");
                return new ApiServerStartResult(false, 0, "the API server would listen on an address other than 127.0.0.1", false);
            }

            var port = new Uri(addresses[0]).Port;
            if (_connectionFile is not null)
            {
                try
                {
                    _connectionFile.Write(port, _token.Value);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await StopHostAsync(app).ConfigureAwait(false);
                    _log?.Invoke("[api] server stopped: the connection file could not be written");
                    return new ApiServerStartResult(false, 0, $"the API connection file {_connectionFile.Path} could not be written: {ex.Message}", false);
                }
            }

            _app = app;
            _port = port;
            _log?.Invoke($"[api] server listening on 127.0.0.1:{port}");
            return new ApiServerStartResult(true, port, null, false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Stops the server, ends a running operation (every finger and key is lifted) and deletes the connection file. Idempotent.</summary>
    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var app = _app;
            if (app is null)
            {
                return;
            }

            _app = null;
            _port = 0;
            if (_connectionFile is not null && !await Task.Run(_connectionFile.Delete).ConfigureAwait(false))
            {
                _log?.Invoke("[api] connection file could not be deleted");
            }

            await StopHostAsync(app).ConfigureAwait(false);
            _rejections.Flush();
            _log?.Invoke("[api] server stopped");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private async Task StopHostAsync(WebApplication app)
    {
        // A request accepted just before the listener closed must not start (or keep running) an operation: refuse new
        // ones, and keep cancelling the running one until the host has finished stopping.
        _stopping = true;
        var closing = _streams.CloseAllAsync(StreamEnd.ServerStopping, TimeSpan.FromSeconds(3));

        // A stream stuck in a device write is cancelled together with its session if it has not ended after a second.
        if (await Task.WhenAny(closing, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false) != closing)
        {
            _controller.CancelCurrent();
        }

        await closing.ConfigureAwait(false);
        _controller.CancelCurrent();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var stop = app.StopAsync(timeout.Token);
        try
        {
            while (!stop.IsCompleted)
            {
                _controller.CancelCurrent();
                await Task.WhenAny(stop, Task.Delay(25)).ConfigureAwait(false);
            }

            await stop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Requests that did not finish in time are dropped with the host.
        }

        await app.DisposeAsync().ConfigureAwait(false);
        _controller.CancelCurrent();
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
            {
                return true;
            }
        }

        return false;
    }

    // ---- Routes (the one place that lists them) ------------------------------------------------------------

    private void RegisterBuiltInRoutes()
    {
        var c = _controller;

        // GET: the status objects (ApiStatus, RotationInfo).
        Add("GET", "/v1/status", ctx => ApiResponses.WriteResultAsync(ctx, c.GetStatus(), ApiJson.Default.ApiStatus));
        Add("GET", "/v1/rotation", ctx => ApiResponses.WriteResultAsync(ctx, c.GetRotation(), ApiJson.Default.RotationInfo));

        // PUT /v1/target: { "deviceId": "..." | null } -> {}. PUT /v1/rotation: { "landscape", "portrait" } -> RotationInfo.
        Add("PUT", "/v1/target", ctx => WithBodyAsync(ctx, ApiJson.Default.TargetRequest, c.SetTargetAsync));
        Add("PUT", "/v1/rotation", ctx => WithBodyAsync(ctx, ApiJson.Default.RotationRequest, c.SetRotationAsync, ApiJson.Default.RotationInfo));

        // Touch: the request records of ApiRequests.cs -> {}.
        Add("POST", "/v1/touch/tap", ctx => WithBodyAsync(ctx, ApiJson.Default.TapRequest, c.TapAsync));
        Add("POST", "/v1/touch/long-press", ctx => WithBodyAsync(ctx, ApiJson.Default.LongPressRequest, c.LongPressAsync));
        Add("POST", "/v1/touch/swipe", ctx => WithBodyAsync(ctx, ApiJson.Default.SwipeRequest, c.SwipeAsync));
        Add("POST", "/v1/touch/path", ctx => WithBodyAsync(ctx, ApiJson.Default.PathRequest, c.PathAsync));
        Add("POST", "/v1/touch/pinch", ctx => WithBodyAsync(ctx, ApiJson.Default.PinchRequest, c.PinchAsync));

        // Keys: press -> {}; type -> { "skipped": "..." } (the characters that have no key on a US keyboard).
        Add("POST", "/v1/keys/press", ctx => WithBodyAsync(ctx, ApiJson.Default.KeyPressRequest, c.PressKeyAsync));
        Add("POST", "/v1/keys/type", ctx => WithBodyAsync(ctx, ApiJson.Default.TypeTextRequest, c.TypeTextAsync, ApiJson.Default.TypeTextResponse));

        // POST /v1/home -> {}. POST /v1/cancel -> { "cancelled": true | false }.
        Add("POST", "/v1/home", async ctx => await ApiResponses.WriteResultAsync(ctx, await c.HomeAsync(ctx.RequestAborted).ConfigureAwait(false)).ConfigureAwait(false));
        Add("POST", "/v1/cancel", ctx => ApiResponses.WriteJsonAsync(ctx, StatusCodes.Status200OK, new CancelResponse(c.CancelCurrent()), ApiJson.Default.CancelResponse));

        // GET /v1/screenshot?format=&maxSize=&quality= -> the image (not JSON), with the orientation, rotation and source size in headers.
        Add("GET", "/v1/screenshot", ScreenshotAsync);

        // GET /v1/stream: the WebSocket of the low-level operations. The token is the first message (a browser cannot set the
        // header), so the route is anonymous for the header check; the Origin and Host checks still apply before the handshake.
        Add("GET", "/v1/stream", ctx => ctx.WebSockets.IsWebSocketRequest
            ? _streams.HandleAsync(ctx)
            : RejectAsync(ctx, ApiError.InvalidRequest("this endpoint needs a WebSocket upgrade (GET with Upgrade: websocket)")), anonymous: true);

        // The specification: needs no token (the Origin and Host checks still apply).
        Add("GET", "/v1/openapi.json", ctx => ApiResponses.WriteBytesAsync(ctx, StatusCodes.Status200OK, ApiResponses.JsonContentType, OpenApiDocument), anonymous: true);

        void Add(string method, string path, RequestDelegate handler, bool anonymous = false) =>
            _routes.Add(new ApiRoute(method, path, handler, anonymous));
    }

    private Dictionary<string, Dictionary<string, ApiRoute>> BuildTable()
    {
        Dictionary<string, Dictionary<string, ApiRoute>> table = new(StringComparer.Ordinal);
        lock (_routes)
        {
            foreach (var route in _routes)
            {
                if (!table.TryGetValue(route.Path, out var methods))
                {
                    methods = new Dictionary<string, ApiRoute>(StringComparer.Ordinal);
                    table[route.Path] = methods;
                }

                methods[route.Method] = route;
            }
        }

        return table;
    }

    // ---- The request pipeline ------------------------------------------------------------------------------

    private async Task HandleAsync(HttpContext context)
    {
        var request = context.Request;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        try
        {
            // 1. Browsers always send Origin on cross-origin requests (and on preflights); scripts do not.
            if (request.Headers.ContainsKey(HeaderNames.Origin))
            {
                await RejectAsync(context, ApiError.Create(ApiErrorCodes.ForbiddenOrigin, "requests from a browser page are not accepted")).ConfigureAwait(false);
                return;
            }

            // 2. DNS rebinding: a page on another name resolving to 127.0.0.1 still sends its own name as Host.
            if (!IsAllowedHost(context))
            {
                await RejectAsync(context, ApiError.Create(ApiErrorCodes.ForbiddenOrigin, "the Host header must be 127.0.0.1 with the port of the API")).ConfigureAwait(false);
                return;
            }

            // 3. The token (unless the exact route is anonymous).
            _table.TryGetValue(request.Path.Value ?? string.Empty, out var methods);
            ApiRoute? route = null;
            methods?.TryGetValue(request.Method, out route);
            if (route is not { Anonymous: true } && !IsAuthorized(request))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await RejectAsync(context, ApiError.Create(ApiErrorCodes.Unauthorized, "a valid bearer token is required")).ConfigureAwait(false);
                return;
            }

            // 4. The route.
            if (methods is null)
            {
                await ApiResponses.WriteProblemAsync(context, ApiError.Create(ApiErrorCodes.NotFound, "no such endpoint")).ConfigureAwait(false);
                return;
            }

            if (route is null)
            {
                context.Response.Headers.Allow = string.Join(", ", methods.Keys.Order(StringComparer.Ordinal));
                await ApiResponses.WriteProblemAsync(context, ApiError.Create(ApiErrorCodes.MethodNotAllowed, "this endpoint does not accept that HTTP method")).ConfigureAwait(false);
                return;
            }

            // 5. A body: not larger than the limit and JSON.
            if (HasBody(request))
            {
                if (request.ContentLength > MaxBodyBytes)
                {
                    await RejectAsync(context, ApiError.InvalidRequest($"the request body is larger than {MaxBodyBytes / 1024} KB")).ConfigureAwait(false);
                    return;
                }

                if (!IsJsonContentType(request.ContentType))
                {
                    await RejectAsync(context, ApiError.InvalidRequest("the Content-Type must be application/json (UTF-8)")).ConfigureAwait(false);
                    return;
                }
            }

            if (_stopping)
            {
                await ApiResponses.WriteProblemAsync(context, ApiError.Create(ApiErrorCodes.NotRunning, "the API is shutting down")).ConfigureAwait(false);
                return;
            }

            await route.Handler(context).ConfigureAwait(false);
        }
        catch (RequestRejectedException ex)
        {
            await RejectAsync(context, ex.Error).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsClientSide(ex, context))
        {
            // The client went away, sent a broken request or timed out: nothing to answer, nothing to log. (The
            // controller has lifted everything when its caller's token was cancelled.)
        }
        catch (Exception ex)
        {
            _rejections.Record($"request failed: internal_error ({ex.GetType().Name})");
            if (!context.Response.HasStarted)
            {
                context.Response.Headers.Remove(HeaderNames.WWWAuthenticate);
                var error = ApiError.Create(ApiErrorCodes.InternalError, "the request could not be carried out because of an internal error");
                await ApiResponses.WriteProblemAsync(context, error).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// A failure that belongs to the client's side of the connection (it reset, aborted, sent a malformed request or timed
    /// out), not to the application: it is neither logged as an internal error nor answered with a 500.
    /// </summary>
    private static bool IsClientSide(Exception ex, HttpContext context)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is Microsoft.AspNetCore.Http.BadHttpRequestException or Microsoft.AspNetCore.Connections.ConnectionResetException)
            {
                return true;
            }

            if (e is IOException or OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            {
                return true;
            }
        }

        return false;
    }

    private Task RejectAsync(HttpContext context, ApiError error)
    {
        _rejections.Record($"request rejected: {error.Code}");
        return ApiResponses.WriteProblemAsync(context, error);
    }

    private static bool IsAllowedHost(HttpContext context)
    {
        var hosts = context.Request.Headers.Host;
        if (hosts.Count != 1 || hosts[0] is not { } host)
        {
            return false;
        }

        // The exact port the server is bound to: a name without a port, or with another one, is refused.
        var port = context.Connection.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Only the address: "localhost" is a name that a resolver (or a hosts file) decides, and the server listens on 127.0.0.1 only.
        return string.Equals(host, $"127.0.0.1:{port}", StringComparison.Ordinal);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1 || values[0] is not { } header || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return VerifyToken(header[BearerPrefix.Length..]) is not null;
    }

    /// <summary>The generation of the token when <paramref name="presented"/> is the current one, else null (constant time).</summary>
    private int? VerifyToken(string? presented)
    {
        if (presented is null)
        {
            return null;
        }

        // Hash both sides so the comparison always covers 32 bytes whatever the length of the presented value.
        var state = _token;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(hash, state.Hash) ? state.Generation : null;
    }

    private static bool HasBody(HttpRequest request) =>
        request.ContentLength > 0 || request.Headers.ContainsKey(HeaderNames.TransferEncoding);

    private static bool IsJsonContentType(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var media)
            || !media.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var parameter in media.Parameters)
        {
            if (parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parameter.Value.Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // ---- Handlers ------------------------------------------------------------------------------------------

    private async Task ScreenshotAsync(HttpContext context)
    {
        List<KeyValuePair<string, string>> query = [];
        foreach (var (name, values) in context.Request.Query)
        {
            if (values.Count == 0)
            {
                query.Add(new KeyValuePair<string, string>(name, string.Empty));
            }

            foreach (var value in values)
            {
                query.Add(new KeyValuePair<string, string>(name, value ?? string.Empty));
            }
        }

        var result = await _controller.ScreenshotAsync(query, context.RequestAborted).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            await ApiResponses.WriteProblemAsync(context, error).ConfigureAwait(false);
            return;
        }

        var image = result.Value!;
        var headers = context.Response.Headers;
        headers["X-IceScreenTouch-Orientation"] = image.Orientation;
        headers["X-IceScreenTouch-Rotation"] = image.Rotation.ToString(System.Globalization.CultureInfo.InvariantCulture);
        headers["X-IceScreenTouch-Source-Size"] = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{image.SourceWidth}x{image.SourceHeight}");
        await ApiResponses.WriteBytesAsync(context, StatusCodes.Status200OK, image.ContentType, image.Data).ConfigureAwait(false);
    }

    private static async Task WithBodyAsync<TRequest>(
        HttpContext context,
        JsonTypeInfo<TRequest> requestInfo,
        Func<TRequest?, CancellationToken, ValueTask<ApiResult>> operation)
        where TRequest : class
    {
        var request = await ReadBodyAsync(context, requestInfo).ConfigureAwait(false);
        var result = await operation(request, context.RequestAborted).ConfigureAwait(false);
        await ApiResponses.WriteResultAsync(context, result).ConfigureAwait(false);
    }

    private static async Task WithBodyAsync<TRequest, TResponse>(
        HttpContext context,
        JsonTypeInfo<TRequest> requestInfo,
        Func<TRequest?, CancellationToken, ValueTask<ApiResult<TResponse>>> operation,
        JsonTypeInfo<TResponse> responseInfo)
        where TRequest : class
    {
        var request = await ReadBodyAsync(context, requestInfo).ConfigureAwait(false);
        var result = await operation(request, context.RequestAborted).ConfigureAwait(false);
        await ApiResponses.WriteResultAsync(context, result, responseInfo).ConfigureAwait(false);
    }

    /// <summary>The body as <typeparamref name="TRequest"/>; null when there is none (the controller answers "a body is required").</summary>
    private static async Task<TRequest?> ReadBodyAsync<TRequest>(HttpContext context, JsonTypeInfo<TRequest> typeInfo)
        where TRequest : class
    {
        if (!HasBody(context.Request))
        {
            return null;
        }

        try
        {
            return await JsonSerializer.DeserializeAsync(context.Request.Body, typeInfo, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            // The path names the member (for example "$.x"), never its value.
            var where = string.IsNullOrEmpty(ex.Path) || ex.Path == "$" ? string.Empty : $" at {ex.Path}";
            throw new RequestRejectedException(ApiError.InvalidRequest($"the request body is not valid for this endpoint{where}"));
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            throw new RequestRejectedException(ApiError.InvalidRequest($"the request body could not be read (limit {MaxBodyBytes / 1024} KB)"));
        }
    }

    private sealed class RequestRejectedException(ApiError error) : Exception("request rejected")
    {
        public ApiError Error { get; } = error;
    }

    private static byte[] LoadOpenApi()
    {
        using var stream = typeof(ApiServer).Assembly.GetManifestResourceStream("IceScreenTouch.Api.openapi.json")
            ?? throw new InvalidOperationException("the embedded openapi.json is missing");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The token, its SHA-256 (what a request's token is compared with) and its generation (grows with every replacement).</summary>
    private sealed record TokenState(string Value, byte[] Hash, int Generation = 0)
    {
        public static TokenState Of(string token)
        {
            if (!ApiToken.IsValid(token))
            {
                throw new ArgumentException("the token must be 43 Base64url characters (ApiToken.Generate)", nameof(token));
            }

            return new TokenState(token, SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        }
    }
}
