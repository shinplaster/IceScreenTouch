// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using IceScreenTouch.Api;
using IceScreenTouch.App.Services;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.App.Tests;

/// <summary>A session as the API sees it: records what is sent, answers the bridge's state from properties.</summary>
internal sealed class FakeApiSession : IApiSession, ITouchSink, IKeyboardSink, IHomeButtonSink
{
    public FakeApiSession(TimeProvider time)
    {
        Arbiter = new ApiInputArbiter(time);
        Input = new ArbiterInput(Arbiter);
    }

    public ApiInputArbiter Arbiter { get; }

    public bool IsSubscribed { get; set; } = true;

    public bool HasKeyboard { get; set; } = true;

    public bool HasHomeButton { get; set; } = true;

    public ITouchSink Touch => this;

    public IKeyboardSink Keyboard => this;

    public IHomeButtonSink HomeButton => this;

    public ArbiterInput Input { get; }

    public BridgeStatus Bridge { get; set; } = BridgeStatus.Initial;

    public List<(Rotation? Landscape, Rotation? Portrait)> RotationSets { get; } = [];

    public List<int> TouchReports { get; } = [];

    public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
    {
        TouchReports.Add(contacts.Length);
        return ValueTask.CompletedTask;
    }

    public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public ValueTask PressHomeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    public void SetRotation(Rotation? landscape, Rotation? portrait) => RotationSets.Add((landscape, portrait));

    /// <summary>What a screenshot gets from this session (null = the capture fails).</summary>
    public CapturedPicture? Captured { get; set; }

    public ValueTask<CapturedPicture?> CapturePictureAsync(CancellationToken cancellationToken) => new(Captured);
}

/// <summary>A mirror service whose API session the test sets.</summary>
internal sealed class ApiFakeMirror : IMirrorService
{
    public MirrorStatus Status { get; set; } = MirrorStatus.Idle;

    public IApiSession? Session { get; set; }

    public List<string?> Targets { get; } = [];

    public IApiSession? ApiSession => Session;

    public event EventHandler<Rotation>? LandscapeRotationChanged
    {
        add { }
        remove { }
    }

    public Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAsync() => Task.CompletedTask;

    public MirrorStatus GetStatus() => Status;

    public void Home()
    {
    }

    public void ToggleRotation()
    {
    }

    public void ChooseTarget(string? deviceId) => Targets.Add(deviceId);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>What the API shows about the app: the host's snapshot and status, the input hand-over, the rotation and target requests.</summary>
#pragma warning disable CA1001 // the fakes own nothing that needs disposing here
public sealed class ApiHostTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ApiFakeMirror _mirror = new();
    private AppSettings _settings = new() { LandscapeRotation = 270 };
    private readonly List<string> _log = [];

    private ApiHost Host(TimeSpan? timeout = null) => new(_mirror, () => _settings, _time, _log.Add, timeout);

    private static MirrorStatus Running(params HidClient[] devices) => new()
    {
        Running = true,
        Ble = BleState.Connected,
        Receiver = ReceiverState.Showing,
        ReceiverName = "AirServer",
        Devices = devices,
        Target = devices.Length == 0 ? TargetChoice.None : new TargetChoice(devices[0], TargetReason.Latest),
    };

    [Fact]
    public void WithoutASessionNothingRunsAndTheSavedRotationIsReported()
    {
        var snapshot = Host().GetSnapshot();

        Assert.False(snapshot.Running);
        Assert.False(snapshot.Subscribed);
        Assert.False(snapshot.HasKeyboard);
        Assert.False(snapshot.HasHomeButton);
        Assert.Null(snapshot.Picture);
        Assert.Equal(Rotation.R270, snapshot.LandscapeRotation);
        Assert.Equal(Rotation.R0, snapshot.PortraitRotation);
        Assert.False(snapshot.UserInputActive);
        Assert.Null(snapshot.LastUserInput);
    }

    [Fact]
    public async Task WithoutASessionTheSinksSayNotConnectedAndSendNothing()
    {
        var host = Host();

        Assert.False(host.Touch.IsSubscribed);
        Assert.False(host.Keyboard.HasKeyboard);
        Assert.False(host.HomeButton.HasHomeButton);
        await host.Touch.SendAsync(new TouchContact[1], CancellationToken.None); // goes nowhere, throws nothing
    }

    [Fact]
    public async Task TheSnapshotComesFromTheBridgeOfTheCurrentSession()
    {
        var session = new FakeApiSession(_time)
        {
            Bridge = BridgeStatus.Initial with { Picture = new RectD(10, 20, 390.4, 843.6), LandscapeRotation = Rotation.R270, PortraitRotation = Rotation.R180, Suspended = true },
        };
        _mirror.Status = Running();
        _mirror.Session = session;

        var snapshot = Host().GetSnapshot();

        Assert.True(snapshot.Running);
        Assert.True(snapshot.ProbeSuspended);
        Assert.True(snapshot.Subscribed);
        Assert.True(snapshot.HasKeyboard);
        Assert.True(snapshot.HasHomeButton);
        Assert.Equal(new PictureInfo(390, 844), snapshot.Picture); // rounded, in PC pixels
        Assert.Equal(Rotation.R270, snapshot.LandscapeRotation);
        Assert.Equal(Rotation.R180, snapshot.PortraitRotation);

        // The sinks follow the session that is current NOW (a restart gives a new one).
        var next = new FakeApiSession(_time) { IsSubscribed = false };
        _mirror.Session = next;
        var host = Host();
        Assert.False(host.Touch.IsSubscribed);
        await host.Touch.SendAsync(new TouchContact[2], CancellationToken.None);
        Assert.Equal([2], next.TouchReports);
        Assert.Empty(session.TouchReports);
    }

    [Fact]
    public async Task ACaptureWithoutASessionOrFromAFailedCaptureIsNoPicture()
    {
        var host = Host();
        Assert.Null(await host.CapturePictureAsync(CancellationToken.None));

        _mirror.Status = Running();
        _mirror.Session = new FakeApiSession(_time) { Captured = null };
        Assert.Null(await host.CapturePictureAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACaptureGivesThePixelsAndTheOrientationAndRotationOfTheSnapshotItWasCutBy()
    {
        var pixels = new byte[(390 * 4 * 3) + 8];
        var cut = BridgeStatus.Initial with { Picture = new RectD(10, 20, 390.4, 843.6), LandscapeRotation = Rotation.R90, PortraitRotation = Rotation.R180 };
        var session = new FakeApiSession(_time)
        {
            // The bridge has moved on (landscape now); the screenshot keeps what it was cut by.
            Bridge = BridgeStatus.Initial with { Picture = new RectD(10, 20, 844, 390), LandscapeRotation = Rotation.R270 },
            Captured = new CapturedPicture(pixels, 390, 3, 390 * 4, cut),
        };
        _mirror.Status = Running();
        _mirror.Session = session;

        var frame = await Host().CapturePictureAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Same(pixels, frame.Bgra);
        Assert.Equal((390, 3, 390 * 4), (frame.Width, frame.Height, frame.Stride));
        Assert.False(frame.IsLandscape);
        Assert.Equal(Rotation.R180, frame.Rotation); // the portrait rotation
    }

    [Fact]
    public async Task ALandscapeCaptureHasTheLandscapeRotationAndASquareOneIsPortrait()
    {
        var landscape = BridgeStatus.Initial with { Picture = new RectD(0, 0, 844.2, 390.2), LandscapeRotation = Rotation.R270, PortraitRotation = Rotation.R180 };
        var square = BridgeStatus.Initial with { Picture = new RectD(0, 0, 500, 500), LandscapeRotation = Rotation.R270, PortraitRotation = Rotation.R180 };
        var session = new FakeApiSession(_time);
        _mirror.Status = Running();
        _mirror.Session = session;
        var host = Host();

        session.Captured = new CapturedPicture(new byte[844 * 4], 844, 1, 844 * 4, landscape);
        var wide = await host.CapturePictureAsync(CancellationToken.None);
        session.Captured = new CapturedPicture(new byte[500 * 4], 500, 1, 500 * 4, square);
        var even = await host.CapturePictureAsync(CancellationToken.None);

        Assert.True(wide!.IsLandscape);
        Assert.Equal(Rotation.R270, wide.Rotation);
        Assert.False(even!.IsLandscape);
        Assert.Equal(Rotation.R180, even.Rotation);
    }

    [Fact]
    public async Task ARotationTheApiJustAskedForIsShownByTheScreenshotLikeByTheStatus()
    {
        var landscape = BridgeStatus.Initial with { Picture = new RectD(0, 0, 844, 390), LandscapeRotation = Rotation.R90 };
        var session = new FakeApiSession(_time) { Bridge = landscape, Captured = new CapturedPicture(new byte[844 * 4], 844, 1, 844 * 4, landscape) };
        _mirror.Status = Running();
        _mirror.Session = session;
        var host = Host();

        host.SetRotation(Rotation.R270, null); // the bridge has not applied it yet

        Assert.Equal(Rotation.R270, host.GetSnapshot().LandscapeRotation);
        Assert.Equal(Rotation.R270, (await host.CapturePictureAsync(CancellationToken.None))!.Rotation);
    }

    [Fact]
    public void AnUnknownPictureIsNullNotZero()
    {
        _mirror.Session = new FakeApiSession(_time) { Bridge = BridgeStatus.Initial with { Picture = null } };
        _mirror.Status = Running();
        Assert.Null(Host().GetSnapshot().Picture);

        _mirror.Session = new FakeApiSession(_time) { Bridge = BridgeStatus.Initial with { Picture = new RectD(0, 0, 0.2, 0.2) } };
        Assert.Null(Host().GetSnapshot().Picture);
    }

    [Fact]
    public void TheStatusDescribesTheSessionTheDevicesAndTheReceiver()
    {
        var phone = new HidClient("dev-1", "Taro's iPhone", 1, "iPhone16,1");
        var pad = new HidClient("dev-2", null, 2);
        _mirror.Status = Running(phone, pad);
        _mirror.Session = new FakeApiSession(_time) { Bridge = BridgeStatus.Initial with { Picture = new RectD(0, 0, 390, 844) } };

        var status = Host().GetStatus();

        Assert.Equal(AppInfo.Version, status.Version);
        Assert.Equal(1, status.ApiVersion);
        Assert.True(status.Running);
        Assert.Equal("connected", status.BleState);
        Assert.Equal([new ApiDevice("dev-1", "Taro's iPhone", "iPhone16,1", true), new ApiDevice("dev-2", string.Empty, null, true)], status.Devices);
        Assert.Equal("dev-1", status.TargetDeviceId);
        Assert.Equal("AirServer", status.Receiver);
        Assert.Equal(new PictureInfo(390, 844), status.Picture);
        Assert.Equal(new RotationInfo(90, 0), status.Rotation);
        Assert.False(status.Operating);
    }

    [Fact]
    public void TheStatusOfAStoppedSessionHasNoReceiverAndNoDevices()
    {
        var status = Host().GetStatus();

        Assert.False(status.Running);
        Assert.Equal("off", status.BleState);
        Assert.Empty(status.Devices);
        Assert.Null(status.TargetDeviceId);
        Assert.Null(status.Receiver);
        Assert.Null(status.Picture);
    }

    [Fact]
    public void ALandscapeRotationSetWhileStoppedIsReadBackAtOnceBeforeAnySavedSettingCatchesUp()
    {
        var host = Host();
        var saved = new List<Rotation>();
        host.LandscapeRotationSetWhileStopped += saved.Add;
        Assert.Equal(Rotation.R270, host.GetSnapshot().LandscapeRotation); // the saved one

        host.SetRotation(Rotation.R90, null);

        // The settings are saved later, on the UI thread (nothing pumps it here): the answer must already be right.
        Assert.Equal([Rotation.R90], saved);
        Assert.Equal(Rotation.R90, host.GetSnapshot().LandscapeRotation);
        Assert.Equal(new RotationInfo(90, 0), host.GetStatus().Rotation);

        host.SetRotation(Rotation.R270, null);
        Assert.Equal(Rotation.R270, host.GetSnapshot().LandscapeRotation);
    }

    [Fact]
    public void APortraitRotationWhileStoppedIsRefusedAndNothingAtAllChanges()
    {
        var host = Host();
        var saved = new List<Rotation>();
        host.LandscapeRotationSetWhileStopped += saved.Add;

        var ex = Assert.Throws<InputRefusedException>(() => host.SetRotation(Rotation.R90, Rotation.R180));

        Assert.Equal(ApiErrorCodes.NotRunning, ex.Code);
        Assert.Contains("portrait", ex.Message, StringComparison.Ordinal);
        Assert.Empty(saved); // not even the landscape half was applied
        Assert.Equal(Rotation.R270, host.GetSnapshot().LandscapeRotation);
        Assert.Equal(Rotation.R0, host.GetSnapshot().PortraitRotation);
    }

    [Fact]
    public void ARotationRequestGoesToTheSessionAndIsShownUntilTheBridgeHasAppliedIt()
    {
        var session = new FakeApiSession(_time) { Bridge = BridgeStatus.Initial with { LandscapeRotation = Rotation.R90, PortraitRotation = Rotation.R0 } };
        _mirror.Status = Running();
        _mirror.Session = session;
        var host = Host();
        var saved = new List<Rotation>();
        host.LandscapeRotationSetWhileStopped += saved.Add;

        host.SetRotation(Rotation.R270, Rotation.R180);

        Assert.Equal([(Rotation?)Rotation.R270, Rotation.R180], [session.RotationSets[0].Landscape, session.RotationSets[0].Portrait]);
        Assert.Empty(saved); // a running session saves through its own event
        var now = host.GetSnapshot(); // the bridge has not ticked yet: still 90 / 0
        Assert.Equal(Rotation.R270, now.LandscapeRotation);
        Assert.Equal(Rotation.R180, now.PortraitRotation);

        // The bridge applies the landscape one: from then on its value is the one shown; the portrait one is still on its way.
        session.Bridge = session.Bridge with { LandscapeRotation = Rotation.R270 };
        Assert.Equal(Rotation.R270, host.GetSnapshot().LandscapeRotation);
        Assert.Equal(Rotation.R180, host.GetSnapshot().PortraitRotation);

        session.Bridge = session.Bridge with { PortraitRotation = Rotation.R180 };
        Assert.Equal(Rotation.R180, host.GetSnapshot().PortraitRotation);

        // The user switches back with Ctrl+R: the request is over, the real value is believed.
        session.Bridge = session.Bridge with { LandscapeRotation = Rotation.R90 };
        Assert.Equal(Rotation.R90, host.GetSnapshot().LandscapeRotation);
    }

    [Fact]
    public void ARequestTheBridgeNeverAppliesIsNotBelievedForever()
    {
        var session = new FakeApiSession(_time) { Bridge = BridgeStatus.Initial with { LandscapeRotation = Rotation.R90 } }; // a dead bridge loop
        _mirror.Status = Running();
        _mirror.Session = session;
        var host = Host();

        host.SetRotation(Rotation.R270, null);
        Assert.Equal(Rotation.R270, host.GetSnapshot().LandscapeRotation);

        _time.Advance(ApiHost.WantedRotationLifetime + TimeSpan.FromMilliseconds(1));

        Assert.Equal(Rotation.R90, host.GetSnapshot().LandscapeRotation);
    }

    [Fact]
    public void ARotationRequestWhileAMeasurementSuspendsTheBridgeIsBusy()
    {
        var session = new FakeApiSession(_time) { Bridge = BridgeStatus.Initial with { Suspended = true } };
        _mirror.Status = Running();
        _mirror.Session = session;

        var ex = Assert.Throws<InputRefusedException>(() => Host().SetRotation(Rotation.R270, null));

        Assert.Equal(ApiErrorCodes.Busy, ex.Code);
        Assert.Empty(session.RotationSets);
    }

    [Fact]
    public void ChoosingATargetNeedsAConnectedDeviceOrAutomatic()
    {
        var host = Host();
        var chosen = new List<string?>();
        host.TargetChosen += chosen.Add;
        _mirror.Status = Running(new HidClient("dev-1", "iPhone", 1));

        Assert.False(host.ChooseTarget("nope"));
        Assert.Empty(_mirror.Targets);

        Assert.True(host.ChooseTarget("dev-1"));
        Assert.True(host.ChooseTarget(null));

        Assert.Equal(["dev-1", null], _mirror.Targets);
        Assert.Equal(["dev-1", null], chosen);
    }

    [Fact]
    public async Task WithoutASessionTheInputIsRefusedAsNotRunning()
    {
        var ex = await Assert.ThrowsAsync<InputRefusedException>(async () => await Host().AcquireInputAsync(CancellationToken.None));

        Assert.Equal(ApiErrorCodes.NotRunning, ex.Code);
    }

    [Fact]
    public async Task ASessionThatEndedIsRefusedAsNotRunning()
    {
        var session = new FakeApiSession(_time);
        _mirror.Session = session;
        session.Arbiter.Close(); // the bridge loop ended

        var ex = await Assert.ThrowsAsync<InputRefusedException>(async () => await Host().AcquireInputAsync(CancellationToken.None));

        Assert.Equal(ApiErrorCodes.NotRunning, ex.Code);
    }

    [Fact]
    public async Task ALoopThatNeverAnswersMakesTheRequestBusyNotHung()
    {
        _mirror.Session = new FakeApiSession(_time); // nobody runs a bridge loop: the arbiter is never answered

        var ex = await Assert.ThrowsAsync<InputRefusedException>(async () => await Host(TimeSpan.FromMilliseconds(80)).AcquireInputAsync(CancellationToken.None));

        Assert.Equal(ApiErrorCodes.Busy, ex.Code);
        Assert.Contains(_log, l => l.StartsWith("[api]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACallerThatGivesUpIsCancelledNotRefused()
    {
        _mirror.Session = new FakeApiSession(_time);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Host(TimeSpan.FromSeconds(30)).AcquireInputAsync(cts.Token));
    }
}

/// <summary>
/// The service on a fake mirror and license, with the real controller and server on an ephemeral port: it starts, stops and
/// restarts with the setting, the port and the license, and keeps api.json and the token in step.
/// </summary>
#pragma warning disable CA1001 // disposed by DisposeAsync (IAsyncLifetime), which CA1001 does not know
public sealed class ApiServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _dir = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeLicenseSource _source = new();
    private readonly ApiFakeMirror _mirror = new();
    private readonly HttpClient _http = new(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
    private LicenseService? _license;
    private ApiService? _api;

    private ApiService Api => _api!;

    public async Task InitializeAsync()
    {
        _license = new LicenseService(_source, _time);
        _source.Next = LicenseState.Purchased;
        await _license.RefreshAsync();
        _api = new ApiService(_mirror, _license, _dir.Path, () => new AppSettings(), _log.Enqueue, anyPort: true);
    }

    public async Task DisposeAsync()
    {
        await Api.StopAsync();
        Api.Dispose();
        _license!.Dispose();
        _http.Dispose();
        _dir.Dispose();
    }

    private string ConnectionFile => Path.Combine(_dir.Path, "api.json");

    private string TokenFile => Path.Combine(_dir.Path, "api-token");

    private async Task<HttpStatusCode> StatusCodeAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{Api.Status.Port}{path}");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(request);
        return response.StatusCode;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task WhileTheSettingIsOffNothingListensAndNothingIsWritten()
    {
        await Api.ConfigureAsync(enabled: false, port: 0);

        Assert.Equal(ApiRunState.Off, Api.Status.State);
        Assert.Null(Api.Token);
        Assert.False(File.Exists(ConnectionFile));
        Assert.False(File.Exists(TokenFile));
    }

    [Fact]
    public async Task SwitchingItOnListensWritesTheConnectionFileAndServesTheStatus()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);

        Assert.Equal(ApiRunState.Listening, Api.Status.State);
        Assert.True(Api.Status.Port > 0);
        Assert.True(ApiToken.IsValid(Api.Token));
        Assert.Equal(Api.Token, File.ReadAllText(TokenFile));
        var connection = JsonElement.Parse(File.ReadAllText(ConnectionFile));
        Assert.Equal(Api.Status.Port, connection.GetProperty("port").GetInt32());
        Assert.Equal(Api.Token, connection.GetProperty("token").GetString());
        Assert.Equal(Environment.ProcessId, connection.GetProperty("pid").GetInt32());
        Assert.Equal(ConnectionFile, Api.ConnectionFilePath);

        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/v1/status", Api.Token));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusCodeAsync("/v1/status", null));
    }

    [Fact]
    public async Task ARequestWhileTheSessionIsStoppedGetsNotRunning()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Api.Status.Port}/v1/home");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Api.Token);
        using var response = await _http.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("not_running", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingItOffStopsTheServerAndRemovesTheConnectionFileButKeepsTheToken()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        var port = Api.Status.Port;
        var token = Api.Token;

        await Api.ConfigureAsync(enabled: false, port: 0);

        Assert.Equal(ApiRunState.Off, Api.Status.State);
        Assert.False(File.Exists(ConnectionFile));
        Assert.Equal(token, File.ReadAllText(TokenFile));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync($"http://127.0.0.1:{port}/v1/openapi.json"));

        // Switched on again: the same token (it only changes when the user regenerates it).
        await Api.ConfigureAsync(enabled: true, port: 0);
        Assert.Equal(token, Api.Token);
    }

    [Fact]
    public async Task AnotherPortRestartsTheServerOnIt()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        var first = Api.Status.Port;
        var second = FreePort();

        await Api.ConfigureAsync(enabled: true, port: 0); // the same setting again: nothing restarts
        Assert.Equal(first, Api.Status.Port);

        // 0 is the test's "any port"; a real port from the valid range is what a user can choose.
        Assert.InRange(second, AppSettings.MinApiPort, AppSettings.MaxApiPort);
        await Api.ConfigureAsync(enabled: true, port: second);

        Assert.Equal(ApiRunState.Listening, Api.Status.State);
        Assert.Equal(second, Api.Status.Port);
        Assert.Equal(second, JsonElement.Parse(File.ReadAllText(ConnectionFile)).GetProperty("port").GetInt32());
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/v1/status", Api.Token));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync($"http://127.0.0.1:{first}/v1/openapi.json"));
    }

    [Fact]
    public async Task APortInUseIsReportedNeverReplacedByAnotherAndApplyingAgainRetries()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        Assert.InRange(port, AppSettings.MinApiPort, AppSettings.MaxApiPort);

        await Api.ConfigureAsync(enabled: true, port: port);

        Assert.Equal(ApiRunState.PortInUse, Api.Status.State);
        Assert.Equal(port, Api.Status.Port);
        Assert.False(Api.Status.IsListening);
        Assert.False(File.Exists(ConnectionFile));

        // A license event is no reason to try the refused port again; applying the setting is.
        _source.Next = LicenseState.NotPackaged;
        await _license!.RefreshAsync();
        await Task.Delay(100);
        Assert.Equal(ApiRunState.PortInUse, Api.Status.State);

        blocker.Stop();
        await Api.ConfigureAsync(enabled: true, port: port);

        Assert.Equal(ApiRunState.Listening, Api.Status.State);
        Assert.Equal(port, Api.Status.Port);
    }

    [Fact]
    public async Task RegeneratingTheTokenTakesEffectAtOnceInTheServerTheFileAndTheConnectionFile()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        var old = Api.Token!;

        Assert.Equal(TokenRegeneration.Done, await Api.RegenerateTokenAsync());

        var token = Api.Token!;
        Assert.NotEqual(old, token);
        Assert.True(ApiToken.IsValid(token));
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusCodeAsync("/v1/status", old));
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/v1/status", token));
        Assert.Equal(token, File.ReadAllText(TokenFile));
        Assert.Equal(token, JsonElement.Parse(File.ReadAllText(ConnectionFile)).GetProperty("token").GetString());
    }

    [Fact]
    public async Task ATokenRegeneratedWhileTheApiIsOffIsUsedAtTheNextStart()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        await Api.ConfigureAsync(enabled: false, port: 0);

        Assert.Equal(TokenRegeneration.Done, await Api.RegenerateTokenAsync());
        var regenerated = Api.Token;
        Assert.False(File.Exists(ConnectionFile));

        await Api.ConfigureAsync(enabled: true, port: 0);

        Assert.Equal(regenerated, Api.Token);
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/v1/status", regenerated));
    }

    [Fact]
    public async Task TheTrialDoesNotStartTheServerAndAPurchaseStartsItWithoutAnotherApply()
    {
        _source.Next = LicenseState.Trial(Start.AddHours(24));
        await _license!.RefreshAsync();

        await Api.ConfigureAsync(enabled: true, port: 0);

        Assert.Equal(ApiRunState.NotAllowed, Api.Status.State);
        Assert.False(Api.IsAllowed);
        Assert.False(File.Exists(ConnectionFile));
        Assert.False(File.Exists(TokenFile)); // no token before the API may run

        _source.Next = LicenseState.Purchased;
        await _license.RefreshAsync(); // the purchase shows up in the license

        await TestWait.UntilAsync(() => Api.Status.State == ApiRunState.Listening, "the API starts after the purchase");
        Assert.True(Api.IsAllowed);
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/v1/status", Api.Token));
    }

    [Fact]
    public async Task ABuildOfYourOwnMayUseTheApi()
    {
        await Api.ConfigureAsync(enabled: false, port: 0);
        _source.Next = LicenseState.NotPackaged;
        await _license!.RefreshAsync();
        await Task.Delay(50);

        await Api.ConfigureAsync(enabled: true, port: 0);

        Assert.Equal(ApiRunState.Listening, Api.Status.State);
    }

    [Fact]
    public async Task AnUnreadableLicenseDoesNotStartTheServerAndTheLicenseBeingReadStartsItWithoutAnotherApply()
    {
        // The first read fails: Unknown, and the first read is over (no guess any more).
        var source = new FakeLicenseSource { Next = LicenseState.Unknown };
        using var license = new LicenseService(source, _time);
        await license.RefreshAsync();
        using var api = new ApiService(_mirror, license, _dir.Path, () => new AppSettings(), _log.Enqueue, anyPort: true);

        await api.ConfigureAsync(enabled: true, port: 0);

        Assert.False(license.IsChecking);
        Assert.False(api.IsAllowed);
        Assert.Equal(ApiRunState.LicenseUnknown, api.Status.State);
        Assert.False(File.Exists(ConnectionFile));
        Assert.False(File.Exists(TokenFile));

        source.Next = LicenseState.Purchased;
        await license.RefreshAsync(); // the Store answers at last (the timer reads Unknown again every minute)

        await TestWait.UntilAsync(() => api.Status.State == ApiRunState.Listening, "the API starts once the license is read");
        Assert.True(api.IsAllowed);
        await api.StopAsync();
    }

    [Fact]
    public async Task TheOcrEnginesReachTheHostAndTheStatus()
    {
        using var license = new LicenseService(null, _time);
        var engine = new StubOcrEngine();
        using var api = new ApiService(_mirror, license, _dir.Path, () => new AppSettings(), _log.Enqueue, anyPort: true, ocrEngines: [engine]);
        Assert.Same(engine, Assert.Single(api.Host.OcrEngines));
        using var controller = new ApiController(api.Host, () => true);
        var ocr = (await controller.GetStatusAsync()).Value!.Ocr!;
        Assert.Equal(("stub", "ja"), (ocr.Engine!.Id, ocr.DefaultLanguage));

        using var none = new ApiService(_mirror, license, _dir.Path, () => new AppSettings(), _log.Enqueue, anyPort: true);
        Assert.Empty(none.Host.OcrEngines);
    }

    [Fact]
    public async Task AControllerAnswersPurchaseRequiredWhileTheLicenseIsUnknown()
    {
        var source = new FakeLicenseSource { Next = LicenseState.Unknown };
        using var license = new LicenseService(source, _time);
        await license.RefreshAsync();
        using var api = new ApiService(_mirror, license, _dir.Path, () => new AppSettings(), _log.Enqueue, anyPort: true);
        var controller = new ApiController(api.Host, () => api.IsAllowed);

        Assert.False((await controller.GetStatusAsync()).IsSuccess);
        Assert.Equal(ApiErrorCodes.PurchaseRequired, (await controller.GetStatusAsync()).Error!.Code);
        controller.Dispose();
    }

    [Fact]
    public async Task APurchasedLicenseThatCannotBeReadLaterStaysPurchasedAndTheServerKeepsRunning()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        Assert.Equal(ApiRunState.Listening, Api.Status.State);

        _source.ReadFails = new InvalidOperationException("no store");
        await _license!.RefreshAsync(); // the read fails (offline): the earlier answer stays

        Assert.Equal(LicenseKind.Purchased, _license.Current.Kind);
        Assert.True(Api.IsAllowed);
        Assert.Equal(ApiRunState.Listening, Api.Status.State);
    }

    [Fact]
    public async Task TheEndOfTheLicenseStopsARunningServer()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        var port = Api.Status.Port;
        Assert.Equal(ApiRunState.Listening, Api.Status.State);

        _source.Next = LicenseState.Expired;
        await _license!.RefreshAsync();

        await TestWait.UntilAsync(() => Api.Status.State == ApiRunState.NotAllowed, "the API stops when the license ends");
        Assert.False(File.Exists(ConnectionFile));
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => _http.GetAsync($"http://127.0.0.1:{port}/v1/openapi.json"));
    }

    [Fact]
    public async Task AControllerThatStillRunsAfterTheLicenseChangedAnswersPurchaseRequired()
    {
        // The controller asks the license itself too (a request between the change and the stop).
        await Api.ConfigureAsync(enabled: true, port: 0);
        var controller = new ApiController(Api.Host, () => Api.IsAllowed);
        Assert.True((await controller.GetStatusAsync()).IsSuccess);

        _source.Next = LicenseState.Trial(Start.AddHours(1));
        await _license!.RefreshAsync();

        Assert.False((await controller.GetStatusAsync()).IsSuccess);
        Assert.Equal(ApiErrorCodes.PurchaseRequired, (await controller.GetStatusAsync()).Error!.Code);
        controller.Dispose();
    }

    [Fact]
    public async Task StoppingForGoodRemovesTheConnectionFileAndNothingStartsItAgain()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        Assert.True(File.Exists(ConnectionFile));

        await Api.StopAsync();

        Assert.Equal(ApiRunState.Off, Api.Status.State);
        Assert.False(File.Exists(ConnectionFile));

        await Api.ConfigureAsync(enabled: true, port: 0); // too late: the app is exiting
        Assert.Equal(ApiRunState.Off, Api.Status.State);
        await Api.StopAsync(); // idempotent
    }

    [Fact]
    public async Task AConnectionFileLeftByACrashIsRemovedWhileTheApiIsOff()
    {
        File.WriteAllText(ConnectionFile, """{ "port": 47821, "token": "stale", "pid": 1 }""");

        await Api.ConfigureAsync(enabled: false, port: 0);

        Assert.False(File.Exists(ConnectionFile));
    }

    [Fact]
    public async Task AFailureToWriteTheTokenIsAStatusNotACrash()
    {
        // A file where the data folder should be: neither the token nor api.json can be written.
        var blocked = Path.Combine(_dir.Path, "not-a-folder");
        File.WriteAllText(blocked, "x");
        using var api = new ApiService(_mirror, _license!, blocked, () => new AppSettings(), _log.Enqueue, anyPort: true);

        await api.ConfigureAsync(enabled: true, port: 0);

        Assert.Equal(ApiRunState.Failed, api.Status.State);
        Assert.NotNull(api.Status.Error);
        Assert.Contains(_log, l => l.Contains("token file", StringComparison.Ordinal));
        await api.StopAsync();
    }

    [Fact]
    public async Task ATokenFileThatCannotBeReadIsNotReplacedTheStartFailsAndApplyingAgainRecovers()
    {
        // The token exists (an earlier run); another program holds the file for the moment of the start.
        var token = new ApiTokenStore(_dir.Path).GetOrCreate();
        using (new FileStream(TokenFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Api.ConfigureAsync(enabled: true, port: 0);

            Assert.Equal(ApiRunState.Failed, Api.Status.State);
            Assert.False(File.Exists(ConnectionFile));
            Assert.Contains(_log, l => l.Contains("token file", StringComparison.Ordinal));
        }

        Assert.Equal(token, File.ReadAllText(TokenFile)); // not regenerated

        await Api.ConfigureAsync(enabled: true, port: 0);

        Assert.Equal(ApiRunState.Listening, Api.Status.State);
        Assert.Equal(token, Api.Token);
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/v1/status", token));
    }

    [Fact]
    public async Task WhenOnlyTheConnectionFileCannotBeRewrittenTheRegenerationSaysSoAndTheNextApplyWritesIt()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        var old = Api.Token!;

        TokenRegeneration result;
        using (new FileStream(ConnectionFile, FileMode.Open, FileAccess.Read, FileShare.Read)) // no FileShare.Delete: the replace fails
        {
            result = await Api.RegenerateTokenAsync();
        }

        Assert.Equal(TokenRegeneration.ConnectionFileStale, result);
        var token = Api.Token!;
        Assert.NotEqual(old, token);
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/v1/status", token)); // in force in the server
        Assert.Equal(token, File.ReadAllText(TokenFile)); // and in the token file
        Assert.Equal(old, JsonElement.Parse(File.ReadAllText(ConnectionFile)).GetProperty("token").GetString()); // api.json is stale
        Assert.Contains(_log, l => l.Contains("api.json could not be updated", StringComparison.Ordinal));

        await Api.ConfigureAsync(enabled: true, port: 0); // the next apply

        Assert.Equal(token, JsonElement.Parse(File.ReadAllText(ConnectionFile)).GetProperty("token").GetString());
        Assert.Contains(_log, l => l.Contains("api.json was updated", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, l => l.Contains(token, StringComparison.Ordinal) || l.Contains(old, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFailedTokenFileWriteDuringRegenerationChangesNothing()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        var token = Api.Token!;
        Directory.CreateDirectory(Path.Combine(_dir.Path, "x"));
        File.Delete(TokenFile);
        Directory.CreateDirectory(TokenFile); // a folder where the file belongs: the replace fails

        var result = await Api.RegenerateTokenAsync();

        Assert.Equal(TokenRegeneration.Failed, result);
        Assert.Equal(token, Api.Token);
        Assert.Equal(HttpStatusCode.OK, await StatusCodeAsync("/v1/status", token));
        Assert.Contains(_log, l => l.Contains("NOT regenerated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BeforeTheFirstLicenseReadTheApiDoesNotStartAndNothingIsSaidAboutPurchases()
    {
        // The Store build: the license is a guess (Unknown, which is usable) until the first read finishes. A trial user must
        // not get a window in which the server runs.
        var gate = new TaskCompletionSource<LicenseState>();
        var source = new FakeLicenseSource { Hang = gate };
        using var license = new LicenseService(source, _time);
        using var api = new ApiService(_mirror, license, _dir.Path, () => new AppSettings(), _log.Enqueue, anyPort: true);
        Assert.True(license.IsChecking);
        Assert.False(api.IsAllowed);

        await api.ConfigureAsync(enabled: true, port: 0);

        Assert.Equal(ApiRunState.Off, api.Status.State); // not NotAllowed: nothing was refused yet
        Assert.False(File.Exists(ConnectionFile));
        Assert.False(File.Exists(TokenFile));

        var refresh = license.RefreshAsync();
        gate.SetResult(LicenseState.Trial(Start.AddHours(24)));
        await refresh;

        Assert.False(license.IsChecking);
        await api.ConfigureAsync(enabled: true, port: 0);
        Assert.Equal(ApiRunState.NotAllowed, api.Status.State); // the trial: now it is a refusal
        Assert.False(File.Exists(ConnectionFile));

        await api.StopAsync();
    }

    [Fact]
    public async Task TheLogNeverHoldsATokenAndEveryLineStartsWithTheApiTag()
    {
        await Api.ConfigureAsync(enabled: true, port: 0);
        var first = Api.Token!;
        await Api.RegenerateTokenAsync();
        var second = Api.Token!;
        _ = await StatusCodeAsync("/v1/status", "wrong-token");
        await Api.ConfigureAsync(enabled: false, port: 0);

        Assert.NotEmpty(_log);
        Assert.All(_log, l =>
        {
            Assert.StartsWith("[api]", l, StringComparison.Ordinal);
            Assert.DoesNotContain(first, l, StringComparison.Ordinal);
            Assert.DoesNotContain(second, l, StringComparison.Ordinal);
        });
    }
}

/// <summary>An OCR engine that reads nothing (the wiring tests only need its description).</summary>
internal sealed class StubOcrEngine : IceScreenTouch.Api.Ocr.IOcrEngine
{
    public IceScreenTouch.Api.Ocr.OcrEngineInfo Info { get; } = new(
        "stub", "Stub", "1", IceScreenTouch.Api.Ocr.OcrEngineKind.BuiltIn,
        new IceScreenTouch.Api.Ocr.OcrCapabilities(true, false, false, new IceScreenTouch.Api.Ocr.OcrImageHints(null, 1, null, 4096, 1)));

    public ValueTask<IceScreenTouch.Api.Ocr.OcrLanguageSet> GetLanguagesAsync(CancellationToken cancellationToken) =>
        ValueTask.FromResult(new IceScreenTouch.Api.Ocr.OcrLanguageSet(["ja"], "ja"));

    public ValueTask<string?> ResolveLanguageAsync(string? tag, CancellationToken cancellationToken) => ValueTask.FromResult<string?>("ja");

    public Task<IceScreenTouch.Api.Ocr.OcrRecognition> RecognizeAsync(IceScreenTouch.Api.Ocr.OcrRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new IceScreenTouch.Api.Ocr.OcrRecognition("ja", []));
}
