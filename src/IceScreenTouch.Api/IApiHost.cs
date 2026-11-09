// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Api;

/// <summary>
/// The picture of the phone as the receiver shows it (the rectangle a screenshot would cut out). The screenshot (A4) must
/// crop exactly this same rectangle.
/// </summary>
/// <param name="Width">Width in PC pixels.</param>
/// <param name="Height">Height in PC pixels.</param>
public sealed record PictureInfo(int Width, int Height)
{
    /// <summary>Wider than tall (a square counts as portrait, as <see cref="SizeD.IsLandscape"/>).</summary>
    public bool IsLandscape => Width > Height;

    /// <summary>"landscape" or "portrait".</summary>
    public string Orientation => IsLandscape ? "landscape" : "portrait";

    /// <summary>Width divided by height.</summary>
    public double Aspect => (double)Width / Height;
}

/// <summary>
/// A snapshot of what the application knows, taken when an operation starts. Immutable; the controller never
/// asks twice for the same decision.
/// </summary>
/// <param name="Running">The mirroring session is running.</param>
/// <param name="ProbeSuspended">The bridge is suspended by the measurement page (probe): the input belongs to it.</param>
/// <param name="Subscribed">The chosen target device subscribed to the touch report (<see cref="ITouchSink.IsSubscribed"/>).</param>
/// <param name="HasKeyboard">The published HID map has the keyboard.</param>
/// <param name="HasHomeButton">The published HID map has the consumer keys (home).</param>
/// <param name="Picture">The picture, or null when there is none (receiver closed, minimized, not found).</param>
/// <param name="LandscapeRotation">The assumed landscape rotation: <see cref="Rotation.R90"/> or <see cref="Rotation.R270"/>.</param>
/// <param name="PortraitRotation">The assumed portrait rotation: <see cref="Rotation.R0"/> or <see cref="Rotation.R180"/>.</param>
/// <param name="LastUserInput">When the user last used the mirror (mouse, wheel, keys, home, rotate), measured with the same <see cref="TimeProvider"/> as the controller; null if never.</param>
/// <param name="UserInputActive">The user is holding a mouse button or a key on the mirror right now (a gesture is in progress); input operations are refused with <c>user_active</c>.</param>
public sealed record ApiHostSnapshot(
    bool Running,
    bool ProbeSuspended,
    bool Subscribed,
    bool HasKeyboard,
    bool HasHomeButton,
    PictureInfo? Picture,
    Rotation LandscapeRotation,
    Rotation PortraitRotation,
    DateTimeOffset? LastUserInput,
    bool UserInputActive);

/// <summary>
/// The right to send input to the device while the user's own input is held back. Disposing it gives the input
/// back to the bridge. The API must have lifted every finger and released every key BEFORE it disposes the lease
/// (the bridge may start forwarding the user's mouse at once).
/// </summary>
public interface IInputLease : IAsyncDisposable
{
    /// <summary>
    /// Cancelled when the user touches or types on the mirror while the lease is held (the user has priority).
    /// The bridge does not forward that input until the lease is disposed; it only reports that it happened.
    /// </summary>
    CancellationToken UserInput { get; }
}

/// <summary>One device of the status.</summary>
/// <param name="Id">The device id used by <c>PUT /v1/target</c>.</param>
/// <param name="Name">The name the device advertises.</param>
/// <param name="Model">The model, when known.</param>
/// <param name="Subscribed">The device subscribed to the touch report.</param>
public sealed record ApiDevice(string Id, string Name, string? Model, bool Subscribed);

/// <summary>The fields of <c>GET /v1/status</c> (docs/API-DESIGN.md, chapter 7). The host fills everything but <see cref="Operating"/>.</summary>
/// <param name="Version">The application version.</param>
/// <param name="ApiVersion">The API version (1).</param>
/// <param name="Running">The mirroring session is running.</param>
/// <param name="BleState">A short description of the BLE state (for example "advertising", "connected").</param>
/// <param name="Devices">The devices that connected.</param>
/// <param name="TargetDeviceId">The device that gets the input (null when none).</param>
/// <param name="Receiver">The AirPlay receiver the picture comes from (null when none).</param>
/// <param name="Picture">The picture, or null.</param>
/// <param name="Rotation">The assumed rotations.</param>
/// <param name="Operating">An API operation or session is running (filled by the controller).</param>
public sealed record ApiStatus(
    string Version,
    int ApiVersion,
    bool Running,
    string BleState,
    IReadOnlyList<ApiDevice> Devices,
    string? TargetDeviceId,
    string? Receiver,
    PictureInfo? Picture,
    RotationInfo Rotation,
    bool Operating);

/// <summary>The assumed rotations (degrees): landscape 90 or 270, portrait 0 or 180.</summary>
/// <param name="Landscape">90 or 270.</param>
/// <param name="Portrait">0 or 180.</param>
public sealed record RotationInfo(int Landscape, int Portrait);

/// <summary>
/// Everything the API needs from the application. The application implements it (MirrorBridge and the session in
/// stage A3); the tests fake it.
/// </summary>
public interface IApiHost
{
    /// <summary>The sink for touch reports: it goes to the chosen target device.</summary>
    ITouchSink Touch { get; }

    /// <summary>The sink for keyboard reports (the chosen target device).</summary>
    IKeyboardSink Keyboard { get; }

    /// <summary>The home button of the chosen target device.</summary>
    IHomeButtonSink HomeButton { get; }

    /// <summary>The current state. Cheap; called once per operation.</summary>
    ApiHostSnapshot GetSnapshot();

    /// <summary>The status for <c>GET /v1/status</c> (with <see cref="ApiStatus.Operating"/> false).</summary>
    ApiStatus GetStatus();

    /// <summary>
    /// Takes the input: while the lease is held the bridge does not forward the user's mouse and keys to the device
    /// but reports them through <see cref="IInputLease.UserInput"/>.
    /// <para>
    /// If the user's gesture is in progress at this moment (a mouse button or key held, see
    /// <see cref="ApiHostSnapshot.UserInputActive"/>; the snapshot may be a moment old) the host must not start the
    /// API's input on top of it: it either fails the call with <see cref="InputRefusedException"/> (the controller turns
    /// it into <c>user_active</c>, or <c>busy</c> for a suspended bridge) or ends the user's gesture first (lifts the
    /// user's finger). <see cref="ArbiterInput"/> does the former.
    /// <see cref="IInputLease.UserInput"/> may be cancelled from any thread (the mouse hook, the UI thread); the
    /// controller only queues work from its callback.
    /// </para>
    /// </summary>
    ValueTask<IInputLease> AcquireInputAsync(CancellationToken cancellationToken);

    /// <summary>Sets the assumed rotation (saved, like Ctrl+R). A null argument leaves that one as it is.</summary>
    void SetRotation(Rotation? landscape, Rotation? portrait);

    /// <summary>Chooses the target device (null = automatic). Returns false when the id is unknown.</summary>
    bool ChooseTarget(string? deviceId);
}
