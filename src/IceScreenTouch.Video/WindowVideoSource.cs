using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using IceScreenTouch.Core;
using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Video;

/// <summary>Settings of <see cref="WindowVideoSource"/>.</summary>
public sealed record WindowVideoSourceOptions
{
    /// <summary>Find the picture by looking at the window (letterbox bars). Off = assume a centered portrait picture.</summary>
    public bool DetectPicture { get; init; } = true;

    /// <summary>How often the window is captured while nothing changes.</summary>
    public TimeSpan CaptureInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>The phone's width / height in portrait until a capture tells otherwise.</summary>
    public double PortraitAspect { get; init; } = Geometry.FallbackVideoSize.Width / Geometry.FallbackVideoSize.Height;

    /// <summary>
    /// Ask the bridge to reshape the window when the picture's orientation and the window's shape disagree (a
    /// uxplay.exe started by hand keeps a tall window for a landscape picture). Needs <see cref="DetectPicture"/>.
    /// </summary>
    public bool FitWindow { get; init; }

    /// <summary>How long the picture and the window must disagree before the window is reshaped.</summary>
    public TimeSpan FitWindowSettleTime { get; init; } = WindowShapeFollower.DefaultSettleTime;

    /// <summary>How often the window is captured while a disagreement is waited out.</summary>
    public TimeSpan FitWindowCaptureInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// A bar across the bottom of the client area that is not picture, in pixels at 100 % display scale
    /// (<see cref="ReceiverQuirks.BottomToolbar"/>); scaled with the monitor.
    /// </summary>
    public int BottomToolbar { get; init; }

    /// <summary>Take the measured picture when it has the phone's shape (<see cref="ReceiverQuirks.UnevenMargins"/>).</summary>
    public bool TrustMeasuredPicture { get; init; }

    /// <summary>
    /// The window counts as the mirror window only while another machine is connected to the receiver
    /// (<see cref="ReceiverQuirks.ShowsStandbyScreen"/>): without a device it shows a standby screen, and clicks on
    /// it must not become touches.
    /// </summary>
    public bool OnlyWhileConnected { get; init; }

    /// <summary>How often the receiver's connections are looked at (<see cref="OnlyWhileConnected"/>).</summary>
    public TimeSpan ConnectionCheckInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// The largest visible window of an already running receiver (AirServer, a uxplay.exe started by hand, ...).
/// Such receivers do not say where the picture is, so a background thread captures the window now and then
/// (<see cref="WindowCapture"/>) and finds the picture in it (<see cref="PictureTracker"/>). A resize triggers a
/// capture at once; until it is done, the bridge gets a guess.
/// </summary>
public sealed class WindowVideoSource : IVideoSource, IDisposable
{
    private static readonly TimeSpan LookupInterval = TimeSpan.FromSeconds(1);

    private readonly string _processName;
    private readonly WindowVideoSourceOptions _options;
    private readonly Action<string> _log;
    private readonly WindowTracker _windows = new();
    private readonly TimeProvider _time;
    private readonly PictureTracker _picture;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread? _captureThread;
    private readonly WindowShapeFollower? _shape;
    private volatile nint _window;
    private int _processId;
    private long _lookedUpAt;
    private bool _lookedUp;
    private nint _trackedWindow;
    private static readonly TimeSpan OwnAddressesInterval = TimeSpan.FromSeconds(30);
    private long _connectionCheckedAt;
    private int _connectionPid;
    private bool _connected;
    private bool _connectionLogged;
    private readonly Lock _connectionLock = new();
    private HashSet<IPAddress>? _ownAddresses;
    private long _ownAddressesAt;

    /// <summary>Watches processes named <paramref name="processName"/> (without .exe).</summary>
    public WindowVideoSource(string processName, WindowVideoSourceOptions? options = null, Action<string>? log = null, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        _processName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? processName[..^4] : processName;
        _options = options ?? new WindowVideoSourceOptions();
        _log = log ?? (_ => { });
        _time = time ?? TimeProvider.System;
        _picture = new PictureTracker(_options.PortraitAspect, _log, PictureLocatorOptions.Default with { TrustMeasuredShape = _options.TrustMeasuredPicture });
        if (_options.DetectPicture)
        {
            _shape = _options.FitWindow ? new WindowShapeFollower(_options.FitWindowSettleTime, _time, _log) : null;
            _captureThread = new Thread(CaptureLoop) { IsBackground = true, Name = "IceScreen Touch window capture", Priority = ThreadPriority.BelowNormal };
            _captureThread.Start();
        }
    }

    /// <inheritdoc/>
    public string Name => _processName;

    /// <inheritdoc/>
    /// <remarks>Unknown: the receiver does not report it (and the bridge must not resize its window).</remarks>
    public SizeD? VideoSize => null;

    /// <inheritdoc/>
    /// <remarks>
    /// Only with <see cref="WindowVideoSourceOptions.FitWindow"/>: see <see cref="WindowShapeFollower"/>. The follower
    /// sees the client area without the bottom toolbar, so the toolbar is added back here.
    /// </remarks>
    public SizeD? WindowSize => _shape?.Desired is { } d ? new SizeD(d.Width, d.Height + _picture.BottomInset) : null;

    /// <summary>The process currently followed, or 0.</summary>
    public int ProcessId => _processId;

    /// <summary>The picture tracker (orientation, learned aspect).</summary>
    public PictureTracker Picture => _picture;

    /// <summary>The device's width / height in portrait; a change looks at the window again at once.</summary>
    public double PortraitAspect
    {
        get => _picture.PortraitAspect;
        set
        {
            if (value != _picture.PortraitAspect)
            {
                _picture.PortraitAspect = value;
                Wake();
            }
        }
    }

    /// <summary>Captures done and the time of the last one (for the log).</summary>
    public (int Count, TimeSpan Last) CaptureStats { get; private set; }

    /// <inheritdoc/>
    public nint FindWindow()
    {
        // Enumerating processes is slow; refresh the process ID at most once a second unless it has a window.
        var window = _windows.Find(_processId);
        if (window == 0 && (!_lookedUp || _time.GetElapsedTime(_lookedUpAt) >= LookupInterval))
        {
            _processId = Lookup();
            _lookedUp = true;
            _lookedUpAt = _time.GetTimestamp();
            window = _windows.Find(_processId);
        }

        if (window != 0 && _options.OnlyWhileConnected && !HasConnectedDevice)
        {
            // The standby screen: no window to click on, nothing to capture. The picture is looked for afresh
            // once a device connects (the standby screen may have passed for a portrait picture).
            window = 0;
            _trackedWindow = 0;
        }

        if (window != _trackedWindow && window != 0)
        {
            _trackedWindow = window;
            _picture.Reset();
            Wake();
        }

        _window = window;
        return window;
    }

    /// <inheritdoc/>
    public RectD? LocatePicture(nint window, SizeD client)
    {
        var picture = _picture.Locate(window, client, out var fresh);
        if (!fresh)
        {
            Wake(); // resized or rotated: look again now rather than at the next interval
        }

        return picture;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_stop.IsCancellationRequested)
        {
            return;
        }

        _stop.Cancel();
        _captureThread?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
        _wake.Dispose();
    }

    private void Wake()
    {
        if (_captureThread is null)
        {
            return;
        }

        try
        {
            if (_wake.CurrentCount == 0)
            {
                _wake.Release();
            }
        }
        catch (Exception ex) when (ex is SemaphoreFullException or ObjectDisposedException)
        {
            // Already awake, or shutting down.
        }
    }

    private void CaptureLoop()
    {
        using var capture = new WindowCapture();
        var token = _stop.Token;
        bool? fromScreen = null;
        try
        {
            while (!token.IsCancellationRequested)
            {
                _wake.Wait(_shape is { Pending: true } ? _options.FitWindowCaptureInterval : _options.CaptureInterval, token);
                var window = _window;
                if (window == 0)
                {
                    continue;
                }

                var started = Stopwatch.GetTimestamp();
                if (_options.BottomToolbar > 0)
                {
                    _picture.BottomInset = (int)Math.Round(_options.BottomToolbar * Win32Windows.MonitorScaleOf(window));
                }

                var found = capture.Capture(window, (bgra, width, height, stride) => (Estimate: _picture.Update(window, bgra, width, height, stride), Client: new SizeD(width, height)));
                if (found.Client.Width > 0 && capture.LastFromScreen != fromScreen)
                {
                    fromScreen = capture.LastFromScreen;
                    _log(fromScreen.Value
                        ? "[video] capturing the window from the screen (nothing covers it)"
                        : "[video] capturing the window with PrintWindow (something covers it)");
                }
                if (_shape is not null && found.Client.Width > 0)
                {
                    ObserveShape(window, found.Client, found.Estimate);
                }

                var (count, _) = CaptureStats;
                CaptureStats = (count + 1, Stopwatch.GetElapsedTime(started));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Disposed.
        }
        catch (Exception ex)
        {
            _log($"[video] window capture stopped: {ex.Message}");
        }
    }

    private void ObserveShape(nint window, SizeD fullClient, PictureEstimate? estimate)
    {
        var resizable = !Win32Windows.IsMaximizedOrFullScreen(window);
        var client = _picture.PictureArea(fullClient);
        if (estimate is not null)
        {
            _shape!.Observe(window, client, estimate.Picture, estimate.Ambiguous, resizable);
        }
        else if (_picture.Locate(window, fullClient, out var fresh) is var last && fresh)
        {
            // The capture told nothing: a look at the last picture of this window and size, as a guess.
            _shape!.Observe(window, client, last, ambiguous: true, resizable);
        }
    }

    /// <summary>
    /// Whether another machine is connected to the receiver: a connection on a port the receiver listens on (its
    /// AirPlay port), leaving out this PC's own addresses. Looked at every
    /// <see cref="WindowVideoSourceOptions.ConnectionCheckInterval"/>; any thread. A device that is connected while
    /// no window is found means the picture is not where it can be seen (minimized, or a window this app cannot
    /// follow).
    /// </summary>
    public bool HasConnectedDevice
    {
        get
        {
            lock (_connectionLock)
            {
                return CheckConnection();
            }
        }
    }

    private bool CheckConnection()
    {
        if (_connectionCheckedAt != 0 && _time.GetElapsedTime(_connectionCheckedAt) < _options.ConnectionCheckInterval && _connectionPid == _processId)
        {
            return _connected;
        }

        _connectionCheckedAt = _time.GetTimestamp();
        _connectionPid = _processId;
        if (_ownAddresses is null || _time.GetElapsedTime(_ownAddressesAt) >= OwnAddressesInterval)
        {
            _ownAddressesAt = _time.GetTimestamp();
            try
            {
                _ownAddresses = NetworkInterface.GetAllNetworkInterfaces()
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                    .Select(a => a.Address)
                    .ToHashSet();
            }
            catch (NetworkInformationException ex)
            {
                // This runs in the bridge loop: an exception would end it. Keep the last list (none yet = empty).
                _ownAddresses ??= [];
                _log($"[video] cannot list this PC's addresses: {ex.Message}");
            }
        }

        var own = _ownAddresses;
        var peers = TcpPeers.IncomingAddresses(_processId).Where(a => !own.Contains(a)).ToList();
        var connected = peers.Count > 0;
        if (_options.OnlyWhileConnected && (connected != _connected || !_connectionLogged))
        {
            _connectionLogged = true;
            _log(connected
                ? $"[video] {_processName} shows a device (connected from {string.Join(", ", peers)})"
                : $"[video] {_processName} shows no device (its standby screen): clicks and keys stay with Windows");
        }

        _connected = connected;
        return connected;
    }

    private int Lookup()
    {
        var processes = Process.GetProcessesByName(_processName);
        try
        {
            // Several processes may share the name (two receivers, or test hosts): prefer one that shows a window.
            foreach (var p in processes)
            {
                if (Win32Windows.LargestVisibleWindowOf(p.Id) != 0)
                {
                    return p.Id;
                }
            }

            return processes.Length > 0 ? processes[0].Id : 0;
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }
    }
}
