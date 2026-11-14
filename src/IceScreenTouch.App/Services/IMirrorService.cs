// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Services;

/// <summary>State of the Bluetooth side.</summary>
public enum BleState
{
    /// <summary>Not started.</summary>
    Off,

    /// <summary>Checking the adapter and publishing the HID service.</summary>
    Starting,

    /// <summary>The adapter cannot act as a BLE peripheral (or there is none).</summary>
    Unsupported,

    /// <summary>Publishing failed (another process may publish the HID service).</summary>
    Error,

    /// <summary>Advertising; the iPhone has not subscribed yet.</summary>
    Advertising,

    /// <summary>The iPhone subscribed to the touch report: touches can be sent.</summary>
    Connected,

    /// <summary>Dry run: nothing goes over Bluetooth.</summary>
    DryRun,
}

/// <summary>State of the receiver (the program that shows the picture).</summary>
public enum ReceiverState
{
    /// <summary>Not started.</summary>
    Off,

    /// <summary>Running, waiting for the iPhone (no picture window yet).</summary>
    Waiting,

    /// <summary>The picture window is there.</summary>
    Showing,

    /// <summary>uxplay.exe (or the process to follow) was not found.</summary>
    NotFound,

    /// <summary>uxplay.exe exited.</summary>
    Exited,

    /// <summary>Starting the receiver failed.</summary>
    Error,
}

/// <summary>Everything the status window shows, gathered from the session.</summary>
public sealed record MirrorStatus
{
    /// <summary>Not started.</summary>
    public static MirrorStatus Idle { get; } = new();

    /// <summary>The session is running (between start and stop).</summary>
    public bool Running { get; init; }

    /// <summary>Bluetooth state.</summary>
    public BleState Ble { get; init; }

    /// <summary>Details for Bluetooth (error text, adapter).</summary>
    public string? BleDetail { get; init; }

    /// <summary>Receiver state.</summary>
    public ReceiverState Receiver { get; init; }

    /// <summary>Receiver name (UxPlay, or the process followed).</summary>
    public string ReceiverName { get; init; } = "UxPlay";

    /// <summary>Details for the receiver (path, exit code, error text).</summary>
    public string? ReceiverDetail { get; init; }

    /// <summary>The phone that connected to the receiver, e.g. "iPhone (iPhone16,1)".</summary>
    public string? Device { get; init; }

    /// <summary>
    /// The receiver runs and another machine is connected to it, but its mirror window was not found (minimized, or
    /// a window this app cannot follow). False when the receiver shows its window or has no connection.
    /// </summary>
    public bool DeviceWithoutWindow { get; init; }

    /// <summary>Where the picture size comes from ("sink", "log" or null).</summary>
    public string? SizeSource { get; init; }

    /// <summary>The bridge loop's view.</summary>
    public BridgeStatus Bridge { get; init; } = BridgeStatus.Initial;

    /// <summary>The devices connected over Bluetooth (subscribed to the touch report), oldest first.</summary>
    public IReadOnlyList<HidClient> Devices { get; init; } = [];

    /// <summary>The one device that gets touches and keys, and why.</summary>
    public TargetChoice Target { get; init; } = TargetChoice.None;
}

/// <summary>
/// The running mirror session, implemented by each platform head (Bluetooth, mouse hook, receiver). Every
/// member may be called from the UI thread; nothing blocks for long.
/// </summary>
public interface IMirrorService : IAsyncDisposable
{
    /// <summary>
    /// Starts (or restarts) the session with <paramref name="settings"/>. Expected failures (no adapter,
    /// receiver not found) do not throw; they show up in <see cref="GetStatus"/>.
    /// </summary>
    Task StartAsync(AppSettings settings, CancellationToken cancellationToken = default);

    /// <summary>Stops the session: lifts any finger, stops the receiver it started, stops advertising.</summary>
    Task StopAsync();

    /// <summary>The current state.</summary>
    MirrorStatus GetStatus();

    /// <summary>Sends the home gesture.</summary>
    void Home();

    /// <summary>Switches the landscape rotation 90 &lt;-&gt; 270.</summary>
    void ToggleRotation();

    /// <summary>
    /// Sends touches and keys to <paramref name="deviceId"/> (a <see cref="HidClient.Id"/>) while it is connected;
    /// null = automatic (the device the mirror window shows, else the one that connected last). Takes effect at
    /// once, between gestures.
    /// </summary>
    void ChooseTarget(string? deviceId);

    /// <summary>Raised (on any thread) when the landscape rotation changed (Ctrl+R or <see cref="ToggleRotation"/>).</summary>
    event EventHandler<Rotation>? LandscapeRotationChanged;

    /// <summary>
    /// The running session as the external API uses it (its sinks, input arbitration and bridge state), or null while the
    /// session is stopped. The object belongs to one session: after a restart this returns a new one. Any thread.
    /// </summary>
    IApiSession? ApiSession => null;
}

/// <summary>
/// What the external API needs from one running mirror session: where touches, keys and the home button go (the chosen
/// target device), the arbiter that lets the API take the input from the user's mouse and keys, and the bridge's view of the
/// picture. Everything may be called from any thread.
/// </summary>
public interface IApiSession
{
    /// <summary>Touch reports go to the chosen target device (<see cref="ITouchSink.IsSubscribed"/>: some device subscribed).</summary>
    ITouchSink Touch { get; }

    /// <summary>Keyboard reports; <see cref="IKeyboardSink.HasKeyboard"/> is false when the session has no HID keyboard (dry run, no Bluetooth).</summary>
    IKeyboardSink Keyboard { get; }

    /// <summary>The home button; <see cref="IHomeButtonSink.HasHomeButton"/> is false without the consumer keys.</summary>
    IHomeButtonSink HomeButton { get; }

    /// <summary>The session's input arbitration (created with the same clock as the API controller).</summary>
    ArbiterInput Input { get; }

    /// <summary>What the bridge saw in its last tick: the picture in screen pixels, the rotations, whether a measurement suspended it.</summary>
    BridgeStatus Bridge { get; }

    /// <summary>
    /// Asks the bridge to set the assumed rotation (landscape 90 or 270 and/or portrait 0 or 180) as a command of the API (it does
    /// not count as user input and is applied while the API holds the input). A landscape change is reported through
    /// <see cref="IMirrorService.LandscapeRotationChanged"/> (and saved) like Ctrl+R. Only posts the command and returns (the bridge applies it on its
    /// next tick, a few milliseconds later); the API shows the requested value until the bridge has caught up.
    /// </summary>
    void SetRotation(Rotation? landscape, Rotation? portrait);

    /// <summary>
    /// Copies the pixels of the picture rectangle out of the receiver window for a screenshot (see <see cref="CapturedPicture"/>), or returns null
    /// when there is no picture (no window, minimized, no picture rectangle, the capture failed or timed out). Reads pixels only, so it works
    /// while a measurement suspends the bridge and while the API holds the input. Any thread; the default is null (a session without pixels).
    /// </summary>
    ValueTask<CapturedPicture?> CapturePictureAsync(CancellationToken cancellationToken) => new((CapturedPicture?)null);
}

/// <summary>
/// The pixels of one screenshot with the bridge state they were cut by (one <see cref="BridgeStatus"/> snapshot: its picture rectangle was the
/// crop, its rotations and orientation belong to the same moment).
/// </summary>
/// <param name="Bgra">The cropped pixels, 32-bit BGRA, top-down (a copy the caller owns).</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Stride">Bytes per row.</param>
/// <param name="Status">The bridge snapshot the crop was computed from.</param>
public sealed record CapturedPicture(byte[] Bgra, int Width, int Height, int Stride, BridgeStatus Status);

