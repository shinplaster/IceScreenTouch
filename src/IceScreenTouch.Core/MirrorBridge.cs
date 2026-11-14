// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;

namespace IceScreenTouch.Core;

/// <summary>Settings of <see cref="MirrorBridge"/>.</summary>
public sealed record BridgeOptions
{
    /// <summary>Loop period. 4 ms as in PoC 3 (needs a 1 ms system timer resolution on Windows).</summary>
    public TimeSpan Tick { get; init; } = TimeSpan.FromMilliseconds(4);

    /// <summary>Resize the mirror window to the picture size whenever it changes (rotation).</summary>
    public bool FollowVideoSize { get; init; } = true;

    /// <summary>The picture size assumed until the receiver reports one (the device's screen shape).</summary>
    public SizeD FallbackVideoSize { get; init; } = Geometry.FallbackVideoSize;

    /// <summary>The right button is a long press.</summary>
    public bool RightButtonLongPress { get; init; } = true;

    /// <summary>The wheel scrolls (short swipes).</summary>
    public bool WheelScroll { get; init; } = true;

    /// <summary>Log every touch.</summary>
    public bool Verbose { get; init; }
}

/// <summary>Something the UI asks the bridge loop to do (the loop owns the engine; it is not thread-safe).</summary>
public enum BridgeCommandKind
{
    /// <summary>Home gesture (like the Home key, but works without the focus and without a window).</summary>
    Home,

    /// <summary>Landscape rotation 90 &lt;-&gt; 270 (like Ctrl+R).</summary>
    ToggleRotation,

    /// <summary>Set the landscape rotation to <see cref="BridgeCommand.Rotation"/>.</summary>
    SetRotation,

    /// <summary>Set the portrait rotation (0, or 180 for an upside-down iPad) to <see cref="BridgeCommand.Rotation"/>. Not saved, like the toggle.</summary>
    SetPortraitRotation,
}

/// <summary>A request to the bridge loop.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Rotation">The rotation for <see cref="BridgeCommandKind.SetRotation"/> and <see cref="BridgeCommandKind.SetPortraitRotation"/>.</param>
/// <param name="FromApi">
/// The command comes from the external API, not from the user: it is applied at once even while the API holds the
/// input (see <see cref="ApiInputArbiter"/>), and it does not count as user input.
/// </param>
public readonly record struct BridgeCommand(BridgeCommandKind Kind, Rotation Rotation = Rotation.R0, bool FromApi = false);

/// <summary>What the bridge saw in its last tick (immutable; safe to read from any thread).</summary>
/// <param name="Window">The mirror window (0 = none).</param>
/// <param name="Picture">The picture in screen pixels, or null when there is nothing to click on.</param>
/// <param name="VideoSize">The picture size reported by the receiver, if any.</param>
/// <param name="LandscapeRotation">Rotation used while the picture is landscape.</param>
/// <param name="IsDown">A touch is in progress.</param>
/// <param name="Gestures">Touches started (left and right button).</param>
/// <param name="LongPresses">Long presses started (right button).</param>
/// <param name="Scrolls">Wheel swipes sent.</param>
/// <param name="Dropped">Presses and wheel turns lost because the phone was not subscribed.</param>
/// <param name="MaxSend">Longest single send.</param>
/// <param name="Suspended">Mouse input and commands are ignored (another sender, such as the probe, uses the phone).</param>
/// <param name="PortraitRotation">Rotation used while the picture is portrait (180 = upside down).</param>
/// <param name="Client">The client area of <paramref name="Window"/> on screen that <paramref name="Picture"/> was measured in (it stays with the picture while the bridge is suspended), or null.</param>
public sealed record BridgeStatus(
    nint Window,
    RectD? Picture,
    SizeD? VideoSize,
    Rotation LandscapeRotation,
    bool IsDown,
    int Gestures,
    int LongPresses,
    int Scrolls,
    int Dropped,
    TimeSpan MaxSend,
    bool Suspended = false,
    Rotation PortraitRotation = Rotation.R0,
    RectI? Client = null)
{
    /// <summary>Before the first tick.</summary>
    public static BridgeStatus Initial { get; } = new(0, null, null, Rotation.R90, false, 0, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The picture is landscape (the landscape rotation applies).</summary>
    public bool IsLandscape => Picture is { } p ? p.IsLandscape : VideoSize is { IsLandscape: true };
}

/// <summary>
/// The mouse-to-touch bridge loop, ported from <c>TouchBridge.run</c> in poc/poc3-integrated/mirror_touch.py.
/// Every tick it finds the mirror window, works out where the picture is on screen, feeds the buffered
/// mouse events and the polled cursor position to the <see cref="TouchGestureEngine"/>, handles the
/// Home / Ctrl+R shortcuts and runs the commands posted by the UI. Button transitions and wheel turns arrive as
/// events (a low-level hook) so that a quick click is never lost while a BLE send or the minimum hold is
/// in progress.
/// </summary>
/// <remarks>
/// Run <see cref="RunAsync"/> (or call <see cref="TickAsync"/>) from one loop. <see cref="Post"/> and
/// <see cref="Status"/> may be used from any thread.
/// </remarks>
public sealed class MirrorBridge
{
    private readonly IVideoSource _video;
    private readonly IDesktop _desktop;
    private readonly ChannelReader<MouseInputEvent> _input;
    private readonly BridgeOptions _options;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly ConcurrentQueue<BridgeCommand> _commands = new();
    private nint _followedWindow;
    private MouseButton _activeButton;
    private bool _prevHome;
    private bool _prevRotate;
    private volatile BridgeStatus _status = BridgeStatus.Initial;
    private readonly Lock _suspendLock = new();
    private int _suspendRequests;
    private TaskCompletionSource? _suspendAck;
    private bool _suspended;
    private bool _publishFailureLogged;
    private readonly ApiInputArbiter? _arbiter;

    // What the user did while the external API held the input; processed in order once it is given back.
    private readonly Queue<MouseInputEvent> _replay = new();
    private readonly List<BridgeCommand> _heldCommands = [];
    private int _heldHome;
    private int _heldRotate;

    /// <summary>
    /// Creates a bridge. <paramref name="input"/> is written by the OS hook. With an <paramref name="arbiter"/>, the
    /// bridge gives the input to the external API on request (and keeps the user's input back meanwhile); without one it
    /// behaves as before.
    /// </summary>
    public MirrorBridge(
        TouchGestureEngine engine,
        IVideoSource video,
        IDesktop desktop,
        ChannelReader<MouseInputEvent> input,
        BridgeOptions? options = null,
        TimeProvider? time = null,
        Action<string>? log = null,
        ApiInputArbiter? arbiter = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(video);
        ArgumentNullException.ThrowIfNull(desktop);
        ArgumentNullException.ThrowIfNull(input);
        Engine = engine;
        _video = video;
        _desktop = desktop;
        _input = input;
        _options = options ?? new BridgeOptions();
        _time = time ?? TimeProvider.System;
        _log = log ?? (_ => { });
        _arbiter = arbiter;
    }

    /// <summary>The engine that turns presses and moves into touch reports. Use it from the bridge loop only.</summary>
    public TouchGestureEngine Engine { get; }

    /// <summary>The mirror window found in the last tick (0 = none).</summary>
    public nint Window { get; private set; }

    /// <summary>The picture in screen pixels in the last tick, or null when there is nothing to click on.</summary>
    public RectD? Picture { get; private set; }

    // The client area on screen the picture was measured in (set with Picture; screenshots detect a window that moved or resized since).
    private RectI? _pictureClient;

    /// <summary>The picture size the window was last resized to.</summary>
    public SizeD? FollowedSize { get; private set; }

    /// <summary>A snapshot taken at the end of the last tick (any thread).</summary>
    public BridgeStatus Status => _status;

    /// <summary>Raised (on the bridge loop) after Ctrl+R or a command changed the landscape rotation.</summary>
    public event EventHandler<Rotation>? LandscapeRotationChanged;

    /// <summary>
    /// Raised (on the bridge loop) at the end of every tick with the new <see cref="Status"/>, for a listener that
    /// must keep a copy of it up to date for another thread (the mouse hook's swallow zone). Keep the handler tiny.
    /// </summary>
    public event Action<BridgeStatus>? StatusPublished;

    /// <summary>Queues a command for the next tick (any thread).</summary>
    public void Post(BridgeCommand command) => _commands.Enqueue(command);

    /// <summary>
    /// Stops sending touches from the mouse until <see cref="Resume"/> (any thread): a finger that is down is
    /// lifted, and clicks, wheel turns, Home / Ctrl+R and posted commands are dropped meanwhile. The task completes once
    /// the loop has done so, after which another sender may use the engine's sink. Calls nest.
    /// </summary>
    public Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        lock (_suspendLock)
        {
            _suspendRequests++;
            _arbiter?.SetSuspendWanted(true);
            if (_suspended && _suspendAck is null)
            {
                return Task.CompletedTask;
            }

            _suspendAck ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _suspendAck.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Undoes one <see cref="SuspendAsync"/> (any thread). Extra calls are ignored.</summary>
    public void Resume()
    {
        lock (_suspendLock)
        {
            if (_suspendRequests > 0)
            {
                _suspendRequests--;
                _arbiter?.SetSuspendWanted(_suspendRequests > 0 || _suspended);
            }
        }
    }

    /// <summary>Runs <see cref="TickAsync"/> every <see cref="BridgeOptions.Tick"/> until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_options.Tick, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await TickAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown. The caller lifts a finger still down with Engine.AbortAsync.
        }
        finally
        {
            // Nobody answers the external API any more (stopped or failed): refuse it, interrupt a lease it holds.
            _arbiter?.Close();
        }
    }

    /// <summary>One iteration of the loop.</summary>
    public async ValueTask TickAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TickCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            PublishStatus();
        }
    }

    private async ValueTask TickCoreAsync(CancellationToken cancellationToken)
    {
        if (await SuspendedTickAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var window = _video.FindWindow();
        Window = window;
        if (window == 0)
        {
            // Follow again when a window shows up (uxplay recreates it after a reconnect; restored from minimized).
            _followedWindow = 0;
        }

        _pictureClient = null;
        var picture = window == 0 ? null : MeasurePicture(window);
        Picture = picture;
        if (_arbiter is not null && await ApiStageAsync(window, picture, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (picture is not { } rect)
        {
            // Nothing to click on: lift a finger that is down and drop the pending clicks.
            if (Engine.IsDown || Engine.IsScrolling)
            {
                await Engine.AbortAsync(cancellationToken).ConfigureAwait(false);
                Verbose("[touch] window lost; finger lifted");
            }

            while (_input.TryRead(out _))
            {
            }

            _replay.Clear();
            _heldHome = _heldRotate = 0;
            _prevHome = _prevRotate = false;
            DrainShortcutPresses();
            await RunCommandsAsync(null, cancellationToken).ConfigureAwait(false);
            return;
        }

        while (TryReadEvent(out var e))
        {
            switch (e.Kind)
            {
                case MouseInputKind.Press:
                    await OnPressAsync(window, rect, e, cancellationToken).ConfigureAwait(false);
                    break;
                case MouseInputKind.Release when Engine.IsDown && e.Button == _activeButton:
                    _arbiter?.StampUserInput();
                    await Engine.ReleaseAsync(e.Position, rect, cancellationToken).ConfigureAwait(false);
                    Verbose(string.Create(CultureInfo.InvariantCulture, $"[touch] up    held {Engine.Stats.LastHold.TotalMilliseconds:0} ms"));
                    break;
                case MouseInputKind.Wheel:
                    await OnWheelAsync(window, rect, e, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (Engine.IsScrolling)
        {
            await Engine.ScrollTickAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (Engine.IsDown)
        {
            await Engine.MoveAsync(_desktop.CursorPosition, rect, cancellationToken).ConfigureAwait(false);
        }

        await HandleKeysAsync(window, rect, cancellationToken).ConfigureAwait(false);
        await RunCommandsAsync(rect, cancellationToken).ConfigureAwait(false);
    }

    // Returns true while suspended (the tick does nothing else).
    private async ValueTask<bool> SuspendedTickAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource? ack;
        bool wanted;
        var resumed = false;
        lock (_suspendLock)
        {
            wanted = _suspendRequests > 0;
            ack = _suspendAck;

            // Cleared under the lock: SuspendAsync answers at once while _suspended is set, so a new request in
            // between must not see "still suspended" just before this tick runs normally.
            if (!wanted && _suspended)
            {
                _suspended = false;
                resumed = true;
                _arbiter?.SetSuspendWanted(false);
            }
        }

        if (!wanted)
        {
            if (resumed)
            {
                _log("[bridge] resumed");
            }

            ack?.TrySetCanceled(CancellationToken.None); // suspended and resumed before the loop noticed
            lock (_suspendLock)
            {
                if (_suspendAck == ack)
                {
                    _suspendAck = null;
                }
            }

            return false;
        }

        if (!_suspended)
        {
            if (_arbiter is { HoldsInput: true })
            {
                // The probe wants the phone while the external API has it: interrupt the API like a user would, and
                // suspend once it has given the input back (the tick goes on as a normal one).
                _arbiter.InterruptForProbe();
                return false;
            }

            if (Engine.IsDown || Engine.IsScrolling)
            {
                await Engine.AbortAsync(cancellationToken).ConfigureAwait(false);
            }

            _suspended = true;
            _log("[bridge] suspended: mouse input is ignored");
        }

        while (_input.TryRead(out _))
        {
        }

        DropHeldInput();

        while (_commands.TryDequeue(out var dropped))
        {
            _log($"[cmd ] {dropped.Kind} ignored while suspended");
        }

        // A key held now counts as already pressed, so it does not fire when the bridge resumes.
        _prevHome = _desktop.IsKeyDown(ShortcutKey.Home);
        _prevRotate = _desktop.IsKeyDown(ShortcutKey.Rotate);
        DrainShortcutPresses();
        lock (_suspendLock)
        {
            if (_suspendAck is { } pending)
            {
                _suspendAck = null;
                pending.TrySetResult();
            }
        }

        return true;
    }

    private async ValueTask OnPressAsync(nint window, RectD picture, MouseInputEvent e, CancellationToken cancellationToken)
    {
        if (!PressCounts(window, picture, e))
        {
            return;
        }

        _arbiter?.StampUserInput();
        var longPress = e.Button == MouseButton.Right;
        if (Engine.IsScrolling)
        {
            // A click ends the wheel scroll at once.
            await Engine.EndScrollAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Engine.IsDown)
        {
            return;
        }

        var pinch = e.Control && !longPress;
        var result = await Engine.PressAsync(e.Position, picture, pinch, longPress, cancellationToken).ConfigureAwait(false);
        if (result == PressResult.Started)
        {
            _activeButton = e.Button;
            var kind = longPress ? "long" : pinch ? "pinch" : "touch";
            // Not where: touch positions on the phone's passcode pad or on-screen keyboard spell out what was typed,
            // so the log keeps them no more than the keys (security review, 2026-11-07).
            Verbose(string.Create(CultureInfo.InvariantCulture, $"[touch] down  {kind} rot={(int)Engine.ActiveRotation}"));
        }
        else if (result == PressResult.NotSubscribed)
        {
            Verbose("[touch] dropped: the iPhone has not subscribed to the touch report");
        }
    }

    private async ValueTask OnWheelAsync(nint window, RectD picture, MouseInputEvent first, CancellationToken cancellationToken)
    {
        // Merge the wheel turns that queued up meanwhile (a fast spin, or while a send was slow), as long as
        // nothing else came between.
        double x = 0, y = 0;
        Add(first);
        while (TryPeekEvent(out var next) && next.Kind == MouseInputKind.Wheel && TryReadEvent(out next))
        {
            Add(next);
        }

        if (!_options.WheelScroll || (Engine.IsDown && !Engine.IsScrolling) || !picture.Contains(first.Position) || !_desktop.IsVisibleAt(window, first.Position))
        {
            return;
        }

        _arbiter?.StampUserInput();

        var started = !Engine.IsScrolling;
        var result = await Engine.WheelAsync(first.Position, picture, x, y, cancellationToken).ConfigureAwait(false);
        if (result == PressResult.Started)
        {
            Verbose(string.Create(CultureInfo.InvariantCulture, $"[wheel] {(started ? "start" : "more ")} raw={first.WheelDelta}{(first.Horizontal ? "h" : "v")} notches x={x:0.##} y={y:0.##} rot={(int)Engine.ActiveRotation}"));
        }
        else if (result == PressResult.NotSubscribed)
        {
            Verbose("[wheel] dropped: the iPhone has not subscribed to the touch report");
        }

        void Add(MouseInputEvent e)
        {
            var notches = (double)e.WheelDelta / MouseInputEvent.WheelNotch;
            if (e.Horizontal)
            {
                x += notches;
            }
            else
            {
                y += notches;
            }
        }
    }

    private async ValueTask HandleKeysAsync(nint window, RectD picture, CancellationToken cancellationToken)
    {
        var focused = _desktop.IsForeground(window);

        // Counted presses are taken first, every time: one made while the mirror was not in front is used up here
        // instead of firing later. Null = this desktop does not count, and the edge of the held state decides.
        var homePresses = _desktop.TakeShortcutPresses(ShortcutKey.Home);
        var rotatePresses = _desktop.TakeShortcutPresses(ShortcutKey.Rotate);

        // With counted presses, each press fires once, whatever the held state says: a second press made while this
        // loop was busy sending the first (the hold and the send take ~110 ms) must not be mistaken for the old hold,
        // and a press the desktop saw as held a tick before it was counted must not fire on both ticks (review,
        // 2026-11-07).
        var home = _desktop.IsKeyDown(ShortcutKey.Home) && focused;
        var homeFires = Fires(homePresses, focused, home, _prevHome) + _heldHome; // held: pressed while the API had the input
        _heldHome = 0;
        if (homeFires > 0)
        {
            _arbiter?.StampUserInput();
        }

        for (var i = 0; i < homeFires; i++)
        {
            if (await Engine.HomeAsync(picture, cancellationToken).ConfigureAwait(false))
            {
                Verbose(string.Create(CultureInfo.InvariantCulture, $"[key ] Home -> home {(Engine.HomeIsKey ? "key" : "swipe")} rot={(int)Engine.RotationFor(picture)}"));
            }
        }

        _prevHome = home;

        var rotate = _desktop.IsKeyDown(ShortcutKey.Rotate) && focused;
        var rotateFires = Fires(rotatePresses, focused, rotate, _prevRotate) + _heldRotate;
        _heldRotate = 0;
        if (rotateFires > 0)
        {
            _arbiter?.StampUserInput();
        }

        for (var i = 0; i < rotateFires; i++)
        {
            ToggleRotation(picture, "Ctrl+R");
        }

        _prevRotate = rotate;
    }

    // How many times a shortcut fires this tick: once per counted press while the mirror is in front, or, without a
    // counter, once on the edge of the held state (which already includes the focus).
    private static int Fires(int? presses, bool focused, bool held, bool wasHeld) =>
        presses is { } counted ? (focused ? counted : 0) : (held && !wasHeld ? 1 : 0);

    // Presses counted while the bridge looks at nothing (no window, suspended) are not meant for it.
    private void DrainShortcutPresses()
    {
        _ = _desktop.TakeShortcutPresses(ShortcutKey.Home);
        _ = _desktop.TakeShortcutPresses(ShortcutKey.Rotate);
    }

    private async ValueTask RunCommandsAsync(RectD? picture, CancellationToken cancellationToken)
    {
        if (_heldCommands.Count > 0)
        {
            // The user's commands from while the API had the input come first.
            var held = _heldCommands.ToArray();
            _heldCommands.Clear();
            foreach (var command in held)
            {
                await ApplyCommandAsync(command, picture, cancellationToken).ConfigureAwait(false);
            }
        }

        while (_commands.TryDequeue(out var command))
        {
            await ApplyCommandAsync(command, picture, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ApplyCommandAsync(BridgeCommand command, RectD? picture, CancellationToken cancellationToken)
    {
        if (!command.FromApi)
        {
            _arbiter?.StampUserInput();
        }

        switch (command.Kind)
        {
            case BridgeCommandKind.Home:
                // Without a window, the reported picture size still tells portrait from landscape.
                var size = _video.VideoSize ?? _options.FallbackVideoSize;
                var rect = picture ?? new RectD(0, 0, size.Width, size.Height);
                var sent = await Engine.HomeAsync(rect, cancellationToken).ConfigureAwait(false);
                _log(string.Create(CultureInfo.InvariantCulture, $"[cmd ] home {(Engine.HomeIsKey ? "key" : "swipe")} {(sent ? "sent" : "not sent (touch in progress or iPhone not subscribed)")} rot={(int)Engine.RotationFor(rect)}"));
                break;
            case BridgeCommandKind.ToggleRotation:
                ToggleRotation(picture, "command");
                break;
            case BridgeCommandKind.SetRotation when Enum.IsDefined(command.Rotation):
                Engine.LandscapeRotation = command.Rotation;
                SetRotation(command.Rotation, "command");
                break;
            case BridgeCommandKind.SetPortraitRotation when command.Rotation is Rotation.R0 or Rotation.R180:
                Engine.PortraitRotation = command.Rotation;
                _log(string.Create(CultureInfo.InvariantCulture, $"[rot ] portrait rotation = {(int)command.Rotation} deg (command)"));
                break;
        }
    }

    // ---- the external API holds the input ----------------------------------------------------------------------

    // The user's input is held back while the API has the input: it waits in _replay (mouse), _heldHome / _heldRotate
    // (shortcuts) and _heldCommands (UI commands) and is processed, in this order, by the first normal tick.
    private bool Holding => _replay.Count > 0 || _heldHome > 0 || _heldRotate > 0 || _heldCommands.Count > 0;

    // Answers a pending acquisition, or, while the API holds the input, keeps the user's input back and reports it.
    // Returns true when this tick must do nothing else.
    private async ValueTask<bool> ApiStageAsync(nint window, RectD? picture, CancellationToken cancellationToken)
    {
        var arbiter = _arbiter!;
        var phase = arbiter.Phase;
        if (phase == ApiInputArbiter.ApiPhase.Idle)
        {
            return false;
        }

        if (phase == ApiInputArbiter.ApiPhase.Acquiring)
        {
            // The bridge must be idle: no gesture of the user, nothing of theirs waiting. A refused acquisition leaves
            // what was read in the queues for the normal tick that follows.
            if (Engine.IsDown || Engine.IsScrolling || Holding)
            {
                return arbiter.CompleteAcquire(false);
            }

            CollectUserInput(window, picture);
            var idle = !Holding && _commands.All(c => c.FromApi);
            if (arbiter.CompleteAcquire(idle))
            {
                Verbose("[api] the external API has the input");
                return true;
            }

            return false;
        }

        var userInput = CollectUserInput(window, picture);
        while (_commands.TryDequeue(out var command))
        {
            if (command.FromApi)
            {
                await ApplyCommandAsync(command, picture, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _heldCommands.Add(command);
                userInput = true;
            }
        }

        if (userInput)
        {
            Verbose("[api] user input while the external API has the input: held back");
            arbiter.NotifyUserInput();
        }

        return true;
    }

    // Moves what the user did into the held queues: from the first user input that would reach the device on, every
    // mouse event is kept (in order). Returns true when there is such input in this tick.
    private bool CollectUserInput(nint window, RectD? picture)
    {
        if (picture is not { } rect)
        {
            // Nothing to click on: as in a normal tick, the pending clicks and shortcuts are dropped (the user's
            // commands, which work without a window, stay).
            while (_input.TryRead(out _))
            {
            }

            _replay.Clear();
            _heldHome = _heldRotate = 0;
            _prevHome = _prevRotate = false;
            DrainShortcutPresses();
            return false;
        }

        var found = false;
        while (_input.TryRead(out var e))
        {
            var counts = e.Kind switch
            {
                MouseInputKind.Press => PressCounts(window, rect, e),
                MouseInputKind.Wheel => WheelCounts(window, rect, e),
                _ => false,
            };
            found |= counts;
            if (counts || Holding)
            {
                _replay.Enqueue(e);
            }
        }

        var focused = _desktop.IsForeground(window);
        var homePresses = _desktop.TakeShortcutPresses(ShortcutKey.Home);
        var rotatePresses = _desktop.TakeShortcutPresses(ShortcutKey.Rotate);
        var home = _desktop.IsKeyDown(ShortcutKey.Home) && focused;
        var homeFires = Fires(homePresses, focused, home, _prevHome);
        _prevHome = home;
        var rotate = _desktop.IsKeyDown(ShortcutKey.Rotate) && focused;
        var rotateFires = Fires(rotatePresses, focused, rotate, _prevRotate);
        _prevRotate = rotate;
        _heldHome += homeFires;
        _heldRotate += rotateFires;
        return found || homeFires > 0 || rotateFires > 0;
    }

    // A press the bridge turns into a touch: not one made with Alt (that is for the receiver), not a right press when the
    // long press is off, and over the picture where no other window covers it.
    private bool PressCounts(nint window, RectD picture, MouseInputEvent e)
    {
        if (e.Alt || (e.Button == MouseButton.Right && !_options.RightButtonLongPress))
        {
            return false;
        }

        return picture.Contains(e.Position) && _desktop.IsVisibleAt(window, e.Position);
    }

    private bool WheelCounts(nint window, RectD picture, MouseInputEvent e) =>
        _options.WheelScroll && picture.Contains(e.Position) && _desktop.IsVisibleAt(window, e.Position);

    // The user's input that was held back, then the channel (the hook only ever writes to it).
    private bool TryReadEvent(out MouseInputEvent e) => _replay.TryDequeue(out e) || _input.TryRead(out e);

    private bool TryPeekEvent(out MouseInputEvent e) => _replay.TryPeek(out e) || _input.TryPeek(out e);

    // The probe takes the phone: what was held back is dropped, like the rest of the input.
    private void DropHeldInput()
    {
        _replay.Clear();
        _heldCommands.Clear();
        _heldHome = _heldRotate = 0;
    }

    // A portrait picture turns upside down (0 <-> 180; not saved: an iPad is rarely kept that way); a landscape one,
    // or none, switches to the other landscape (saved through LandscapeRotationChanged).
    private void ToggleRotation(RectD? picture, string by)
    {
        if (picture is { IsLandscape: false })
        {
            var rotation = Engine.TogglePortraitRotation();
            _log(string.Create(CultureInfo.InvariantCulture, $"[rot ] portrait rotation = {(int)rotation} deg ({by})"));
            return;
        }

        SetRotation(Engine.ToggleLandscapeRotation(), by);
    }

    private void SetRotation(Rotation rotation, string by)
    {
        _log(string.Create(CultureInfo.InvariantCulture, $"[rot ] landscape rotation = {(int)rotation} deg ({by})"));
        LandscapeRotationChanged?.Invoke(this, rotation);
    }

    private RectD? MeasurePicture(nint window)
    {
        var size = _video.VideoSize;
        if (_options.FollowVideoSize && _video.WindowSize is { } s && (s != FollowedSize || window != _followedWindow))
        {
            Follow(window, s);
        }

        if (_desktop.GetClientRect(window) is not { } client)
        {
            return null;
        }

        var inClient = _video.LocatePicture(window, new SizeD(client.Width, client.Height))
            ?? Geometry.VideoRect(client.Width, client.Height, size ?? _options.FallbackVideoSize);
        var picture = inClient.Offset(client.Left, client.Top);
        _pictureClient = client;
        return picture.HasArea ? picture : null;
    }

    // Resize the window to the new picture size after a rotation (once per size and window).
    private void Follow(nint window, SizeD size)
    {
        // uxplay may print a 0x0 size (before the stream starts): nothing to fit, and FitClientSize would throw in
        // the bridge loop and end it.
        if (size.Width <= 0 || size.Height <= 0 || _desktop.GetMetrics(window) is not { } m)
        {
            return;
        }

        // A maximized or full-screen window keeps its size (resizing it would shrink it at every rotation).
        // Counted as followed, so that it is not asked again on every tick.
        if (_desktop.IsMaximizedOrFullScreen(window))
        {
            FollowedSize = size;
            _followedWindow = window;
            return;
        }

        var (w, h) = Geometry.FitClientSize(size, m.WorkArea.Width, m.WorkArea.Height, m.FrameWidth, m.FrameHeight);
        _desktop.SetClientSize(window, w, h);
        FollowedSize = size;
        _followedWindow = window;
        _log(string.Create(CultureInfo.InvariantCulture, $"[window] picture is now {size.Width:0}x{size.Height:0}; window client resized to {w}x{h}"));    }

    private void PublishStatus()
    {
        _arbiter?.SetTouchActive(Engine.IsDown || Engine.IsScrolling || Holding);
        var stats = Engine.Stats;
        _status = new BridgeStatus(
            Window,
            Picture,
            _video.VideoSize,
            Engine.LandscapeRotation,
            Engine.IsDown || Engine.IsScrolling,
            stats.Gestures,
            stats.LongPresses,
            stats.Scrolls,
            stats.Dropped,
            stats.MaxSend,
            _suspended,
            Engine.PortraitRotation,
            Picture is null ? null : _pictureClient);
        try
        {
            StatusPublished?.Invoke(_status);
        }
#pragma warning disable CA1031 // A listener must not replace the tick's own exception nor end the loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (!_publishFailureLogged)
            {
                _publishFailureLogged = true;
                _log($"[bridge] a status listener failed (logged once): {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void Verbose(string message)
    {
        if (_options.Verbose)
        {
            _log(message);
        }
    }
}
