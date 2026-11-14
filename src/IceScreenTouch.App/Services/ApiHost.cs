// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.App.Settings;
using IceScreenTouch.Core;

namespace IceScreenTouch.App.Services;

/// <summary>
/// The <see cref="IApiHost"/> of the application: it outlives the mirror sessions (the API server runs while the session is
/// stopped or restarted), so every call looks at the CURRENT session of the <see cref="IMirrorService"/>. Without a session
/// the sinks report "not subscribed" and the snapshot says it is not running; a request that got past that check loses its
/// lease when the session ends (the session's arbiter closes), so a late send into a restarted session cannot happen unnoticed.
/// </summary>
internal sealed class ApiHost : IApiHost
{
    /// <summary>The longest the bridge loop may take to answer a request for the input before the request is refused as busy.</summary>
    internal static readonly TimeSpan DefaultAcquireTimeout = TimeSpan.FromSeconds(2);

    private readonly IMirrorService _mirror;
    private readonly Func<AppSettings> _settings;
    private readonly Action<string>? _log;
    private readonly TimeSpan _acquireTimeout;
    private readonly TimeProvider _time;

    // A rotation the API asked for, shown by the snapshot until the real state catches up: the bridge applies a command on its
    // next tick, and the settings are saved on the UI thread, but the request's answer reads the rotation back at once.
    private readonly Lock _wantLock = new();
    private Rotation? _wantLandscape;
    private Rotation? _wantPortrait;
    private long? _wantAt; // when it was requested of a session; null = requested while stopped (clock starts when a session shows up)

    /// <summary>How long a requested rotation is shown while the bridge has not applied it (a bridge that never does, such as a dead loop, is believed again after this).</summary>
    internal static readonly TimeSpan WantedRotationLifetime = TimeSpan.FromSeconds(2);

    internal ApiHost(IMirrorService mirror, Func<AppSettings> settings, TimeProvider time, Action<string>? log, TimeSpan? acquireTimeout = null)
    {
        _mirror = mirror;
        _settings = settings;
        _time = time;
        _log = log;
        _acquireTimeout = acquireTimeout ?? DefaultAcquireTimeout;
        Touch = new TouchForwarder(this);
        Keyboard = new KeyboardForwarder(this);
        HomeButton = new HomeForwarder(this);
    }

    /// <summary>Raised when the API sets the landscape rotation while no session runs (the app saves it with the settings).</summary>
    internal event Action<Rotation>? LandscapeRotationSetWhileStopped;

    /// <summary>Raised when the API chose a target device (the app saves the choice like the status tab does).</summary>
    internal event Action<string?>? TargetChosen;

    public ITouchSink Touch { get; }

    public IKeyboardSink Keyboard { get; }

    public IHomeButtonSink HomeButton { get; }

    private IApiSession? Session => _mirror.ApiSession;

    public ApiHostSnapshot GetSnapshot()
    {
        var session = Session;
        if (session is null)
        {
            // The saved landscape rotation (or the one just requested, which is only being saved); the portrait one is the
            // normal portrait: it is never saved, and a request to change it while stopped is refused.
            var landscape = Landscape(_settings().Rotation);
            lock (_wantLock)
            {
                // Only a request made while stopped counts here; one made to a session that has ended is not believed.
                if (_wantAt is null && _wantLandscape is { } stopped)
                {
                    landscape = stopped;
                }
            }

            return new ApiHostSnapshot(false, false, false, false, false, null, landscape, Rotation.R0, null, false);
        }

        var bridge = session.Bridge;
        var (landscapeNow, portraitNow) = RotationsOf(bridge);
        return new ApiHostSnapshot(
            Running: true,
            ProbeSuspended: bridge.Suspended,
            Subscribed: session.Touch.IsSubscribed,
            HasKeyboard: session.Keyboard.HasKeyboard,
            HasHomeButton: session.HomeButton.HasHomeButton,
            Picture: PictureOf(bridge),
            LandscapeRotation: landscapeNow,
            PortraitRotation: portraitNow,
            LastUserInput: session.Input.LastUserInput,
            UserInputActive: session.Input.UserInputActive);
    }

    // The rotations of one bridge snapshot as the API shows them (the requested ones while they are on their way, see Wanted).
    private (Rotation Landscape, Rotation Portrait) RotationsOf(BridgeStatus bridge) =>
        Wanted(Landscape(bridge.LandscapeRotation), bridge.PortraitRotation == Rotation.R180 ? Rotation.R180 : Rotation.R0);

    // The bridge's rotations, except where a requested one is still on its way: a field the bridge shows as requested is done
    // (the real value is believed from then on), and so is one that has been waiting longer than WantedRotationLifetime.
    private (Rotation Landscape, Rotation Portrait) Wanted(Rotation landscape, Rotation portrait)
    {
        lock (_wantLock)
        {
            _wantAt ??= _time.GetTimestamp();
            var expired = _time.GetElapsedTime(_wantAt.Value) > WantedRotationLifetime;
            if (_wantLandscape is { } wantL)
            {
                if (wantL == landscape || expired)
                {
                    _wantLandscape = null;
                }
                else
                {
                    landscape = wantL;
                }
            }

            if (_wantPortrait is { } wantP)
            {
                if (wantP == portrait || expired)
                {
                    _wantPortrait = null;
                }
                else
                {
                    portrait = wantP;
                }
            }

            return (landscape, portrait);
        }
    }

    public ApiStatus GetStatus()
    {
        var status = _mirror.GetStatus();
        var snapshot = GetSnapshot();
        var receiver = status.Running && status.Receiver is ReceiverState.Waiting or ReceiverState.Showing ? status.ReceiverName : null;
        return new ApiStatus(
            AppInfo.Version,
            1,
            status.Running,
            BleName(status.Ble),
            [.. status.Devices.Select(d => new ApiDevice(d.Id, d.Name ?? string.Empty, d.Model, true))],
            status.Target.Client?.Id,
            receiver,
            snapshot.Picture,
            new RotationInfo((int)snapshot.LandscapeRotation, (int)snapshot.PortraitRotation),
            false);
    }

    public async ValueTask<IInputLease> AcquireInputAsync(CancellationToken cancellationToken)
    {
        var session = Session ?? throw new InputRefusedException(ApiErrorCodes.NotRunning, "the mirroring session is not running");

        // The bridge answers on its next tick (4 ms); a loop that has died would never answer, and the request must not hang.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_acquireTimeout);
        try
        {
            return await session.Input.AcquireAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log?.Invoke("[api] the bridge did not hand over the input in time");
            throw new InputRefusedException(ApiErrorCodes.Busy, "the mirror bridge did not answer in time");
        }
    }

    /// <summary>
    /// Sets the assumed rotation. With a session the bridge is asked (a command of the API; it never blocks) and the snapshot
    /// shows the request at once. Without one only the landscape rotation can be set: it is a saved setting that the next session
    /// starts with, and the snapshot shows it at once. The portrait rotation (180 = upside down) belongs to a running session and
    /// is never saved, so asking for it while stopped is refused as <c>not_running</c> (nothing at all is changed then). While a
    /// measurement suspends the bridge it ignores commands: refused as <c>busy</c>.
    /// </summary>
    /// <exception cref="InputRefusedException">The request cannot be carried out now.</exception>
    public void SetRotation(Rotation? landscape, Rotation? portrait)
    {
        if (Session is { } session)
        {
            if (session.Bridge.Suspended)
            {
                throw new InputRefusedException(ApiErrorCodes.Busy, "a measurement is running");
            }

            lock (_wantLock)
            {
                _wantLandscape = landscape ?? _wantLandscape;
                _wantPortrait = portrait ?? _wantPortrait;
                _wantAt = _time.GetTimestamp();
            }

            session.SetRotation(landscape, portrait);
            return;
        }

        if (portrait is not null)
        {
            throw new InputRefusedException(ApiErrorCodes.NotRunning, "the portrait rotation can only be set while the mirroring session is running (it is not saved)");
        }

        if (landscape is { } value)
        {
            lock (_wantLock)
            {
                _wantLandscape = value;
                _wantPortrait = null;
                _wantAt = null; // no clock runs while stopped: it stays until a session shows the real rotation
            }

            LandscapeRotationSetWhileStopped?.Invoke(value);
        }
    }

    public bool ChooseTarget(string? deviceId)
    {
        if (deviceId is not null && !_mirror.GetStatus().Devices.Any(d => d.Id == deviceId))
        {
            return false;
        }

        _mirror.ChooseTarget(deviceId);
        TargetChosen?.Invoke(deviceId);
        return true;
    }

    private static Rotation Landscape(Rotation r) => r == Rotation.R270 ? Rotation.R270 : Rotation.R90;

    // The rectangle a screenshot would cut out, in PC pixels (the bridge's picture, the one the mouse is mapped onto). The screenshot
    // rounds it the same way (PictureCrop), so its size is this size.
    private static PictureInfo? PictureOf(BridgeStatus bridge) => PictureCrop.InfoOf(bridge.Picture);

    public async ValueTask<ApiPictureFrame?> CapturePictureAsync(CancellationToken cancellationToken)
    {
        if (Session is not { } session)
        {
            return null;
        }

        if (await session.CapturePictureAsync(cancellationToken).ConfigureAwait(false) is not { } shot)
        {
            return null;
        }

        // Orientation and rotation from the very snapshot the crop was cut by (not from a later one): the rotation shown as the
        // API shows it in the status, the requested one while the bridge has not applied it yet.
        var landscape = PictureOf(shot.Status)?.IsLandscape ?? shot.Width > shot.Height;
        var (landscapeRotation, portraitRotation) = RotationsOf(shot.Status);
        return new ApiPictureFrame(shot.Bgra, shot.Width, shot.Height, shot.Stride, landscape, landscape ? landscapeRotation : portraitRotation);
    }

    private static string BleName(BleState state) => state switch
    {
        BleState.Starting => "starting",
        BleState.Unsupported => "unsupported",
        BleState.Error => "error",
        BleState.Advertising => "advertising",
        BleState.Connected => "connected",
        BleState.DryRun => "dry-run",
        _ => "off",
    };

    private sealed class TouchForwarder(ApiHost host) : ITouchSink
    {
        public bool IsSubscribed => host.Session?.Touch.IsSubscribed ?? false;

        public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken) =>
            host.Session is { } session ? session.Touch.SendAsync(contacts, cancellationToken) : ValueTask.CompletedTask;
    }

    private sealed class KeyboardForwarder(ApiHost host) : IKeyboardSink
    {
        public bool HasKeyboard => host.Session?.Keyboard.HasKeyboard ?? false;

        public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken) =>
            host.Session is { } session ? session.Keyboard.SendKeyboardAsync(modifiers, keys, cancellationToken) : ValueTask.CompletedTask;
    }

    private sealed class HomeForwarder(ApiHost host) : IHomeButtonSink
    {
        public bool HasHomeButton => host.Session?.HomeButton.HasHomeButton ?? false;

        public ValueTask PressHomeAsync(CancellationToken cancellationToken) =>
            host.Session is { } session ? session.HomeButton.PressHomeAsync(cancellationToken) : ValueTask.CompletedTask;
    }
}
