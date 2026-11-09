// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace IceScreenTouch.Core;

/// <summary>
/// Decides when the window of an already running receiver should be reshaped to its picture. Receivers such as a
/// uxplay.exe started by hand keep their window's shape and letterbox the picture after the phone rotates, so a
/// landscape picture ends up as a thin band in a tall window. When the picture's orientation and the window's
/// shape disagree for <see cref="SettleTime"/>, <see cref="Desired"/> becomes a client size with the window's long
/// side and the picture's shape (the bridge resizes the window to it, capped by the work area).
/// </summary>
/// <remarks>
/// It is someone else's window, so it only acts when the picture's orientation changes, or once for a window it
/// has not decided on yet: a window the user reshapes by hand is left alone. A receiver that reshapes its own
/// window after a rotation (AirServer in a window) agrees again before the settle time, so nothing happens.
/// Maximized and full screen windows are never acted on, and a look whose orientation is a guess (a dark screen,
/// or no capture result) never starts a wait, though it may end one.
/// Not thread-safe except <see cref="Desired"/>, which may be read from any thread.
/// </remarks>
public sealed class WindowShapeFollower
{
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private volatile Box? _desired;
    private nint _window;
    private bool? _decidedLandscape;
    private bool? _pendingLandscape;
    private long _pendingSince;
    private SizeD _pendingClient;
    private long? _actedAt;

    /// <summary>Waits <paramref name="settleTime"/> (default <see cref="DefaultSettleTime"/>) before acting on a disagreement.</summary>
    public WindowShapeFollower(TimeSpan? settleTime = null, TimeProvider? time = null, Action<string>? log = null, TimeSpan? cooldown = null)
    {
        SettleTime = settleTime ?? DefaultSettleTime;
        Cooldown = cooldown ?? TimeSpan.FromSeconds(1.5);
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
    }

    private sealed record Box(SizeD Size);

    /// <summary>
    /// The default settle time: long enough for a receiver that reshapes its own window after a rotation, short
    /// enough not to feel slow (1 s was reported as slow on the iPhone).
    /// </summary>
    public static TimeSpan DefaultSettleTime { get; } = TimeSpan.FromMilliseconds(400);

    /// <summary>How long the picture and the window must disagree before the window is reshaped.</summary>
    public TimeSpan SettleTime { get; }

    /// <summary>After a reshape, how long a disagreement is not waited out (the receiver catches up with the new shape).</summary>
    public TimeSpan Cooldown { get; }

    /// <summary>The current window was resized by someone else while a disagreement was waited out; it is left alone.</summary>
    public bool ReshapesItself { get; private set; }

    /// <summary>A disagreement is being waited out (the caller may look at the window more often meanwhile).</summary>
    public bool Pending => _pendingLandscape is not null;

    /// <summary>
    /// The client size the window should get, or null while nothing needs to change. A new value (a different
    /// size) each time the window should be reshaped.
    /// </summary>
    public SizeD? Desired => _desired?.Size;

    /// <summary>
    /// Feeds one look at the window: its client size, where the picture was found in it, whether that finding is
    /// a guess (<paramref name="ambiguous"/>: a dark screen), and whether the window may be resized at all
    /// (<paramref name="resizable"/>: false when maximized or full screen).
    /// </summary>
    public void Observe(nint window, SizeD client, RectD picture, bool ambiguous, bool resizable)
    {
        if (window != _window)
        {
            _window = window;
            _decidedLandscape = null;
            _pendingLandscape = null;
            _actedAt = null;
            ReshapesItself = false;
        }

        if (ReshapesItself)
        {
            return;
        }

        if (!resizable || !picture.HasArea || client.Width <= 0 || client.Height <= 0)
        {
            _pendingLandscape = null;
            return;
        }

        if (_pendingLandscape is not null && client != _pendingClient)
        {
            // Resized while we waited, and not by us (we resize only after deciding): the receiver follows the
            // picture itself (AirServer with an iPad is slower than the settle time), or the user took over.
            // Reshaping too would fight it: seen as a window flipping between portrait and landscape.
            ReshapesItself = true;
            _pendingLandscape = null;
            _log(string.Create(CultureInfo.InvariantCulture, $"[window] the receiver resized its window itself ({_pendingClient.Width:0}x{_pendingClient.Height:0} -> {client.Width:0}x{client.Height:0}); leaving its shape to it"));
            return;
        }

        var landscape = picture.IsLandscape;
        if (_decidedLandscape == landscape)
        {
            _pendingLandscape = null;
            return; // already decided for this orientation (acted, or the shapes agreed)
        }

        if (!Disagrees(client, picture))
        {
            if (!ambiguous)
            {
                _decidedLandscape = landscape;
            }

            _pendingLandscape = null;
            return;
        }

        if (_pendingLandscape != landscape)
        {
            // A guess (a dark screen keeps the previous orientation) never starts a wait, but it does not end one
            // either: dark frames often follow a rotation (a home screen with a dark wallpaper). Right after a
            // reshape, the receiver may still show a frame of the old orientation in the new shape: no wait then.
            var cooling = _actedAt is { } acted && _time.GetElapsedTime(acted) < Cooldown;
            if (!ambiguous && !cooling)
            {
                _pendingLandscape = landscape;
                _pendingSince = _time.GetTimestamp();
                _pendingClient = client;
            }

            return;
        }

        if (_time.GetElapsedTime(_pendingSince) < SettleTime)
        {
            return;
        }

        var size = Reshape(client, picture);
        _decidedLandscape = landscape;
        _pendingLandscape = null;
        _actedAt = _time.GetTimestamp();
        _desired = new Box(size);
        _log(string.Create(
            CultureInfo.InvariantCulture,
            $"[window] {(landscape ? "landscape" : "portrait")} picture {picture.Width:0}x{picture.Height:0} in a {client.Width:0}x{client.Height:0} window; reshaping the window to {size.Width:0}x{size.Height:0}"));
    }

    /// <summary>
    /// The picture's orientation differs from the window's shape and the picture fills little of the window (a
    /// rotated phone picture fills about a fifth of it).
    /// </summary>
    public static bool Disagrees(SizeD client, RectD picture) =>
        picture.IsLandscape != (client.Width > client.Height)
        && picture.Width * picture.Height < 0.6 * client.Width * client.Height;

    /// <summary>A client size with the window's long side and the picture's shape.</summary>
    public static SizeD Reshape(SizeD client, RectD picture)
    {
        var longSide = Math.Max(client.Width, client.Height);
        return picture.IsLandscape
            ? new SizeD(longSide, Math.Round(longSide * picture.Height / picture.Width))
            : new SizeD(Math.Round(longSide * picture.Width / picture.Height), longSide);
    }
}
