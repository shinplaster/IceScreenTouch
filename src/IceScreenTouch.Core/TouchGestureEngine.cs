namespace IceScreenTouch.Core;

/// <summary>Timing rules of the mouse-to-touch bridge (all verified on a real iPhone in PoC 3).</summary>
/// <param name="MinHold">A finger stays down at least this long; shorter taps are unreliable on iOS.</param>
/// <param name="MoveInterval">At most one move frame per interval; BLE cannot carry more and a backlog adds lag.</param>
/// <param name="MinGap">Pause between lifting a finger and the next touch.</param>
public sealed record TouchTiming(TimeSpan MinHold, TimeSpan MoveInterval, TimeSpan MinGap)
{
    /// <summary>The PoC 3 values: hold 70 ms, move every 16 ms, gap 25 ms; long press 600 ms.</summary>
    public static TouchTiming Default { get; } =
        new(TimeSpan.FromMilliseconds(70), TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(25));

    /// <summary>Minimum hold of a long press (right button). iOS opens context menus after about 500 ms.</summary>
    public TimeSpan LongPressHold { get; init; } = TimeSpan.FromMilliseconds(600);
}

/// <summary>What happened to a button press (or a wheel turn).</summary>
public enum PressResult
{
    /// <summary>A touch started.</summary>
    Started,

    /// <summary>Not a touch: outside the picture, already touching, or paused.</summary>
    Ignored,

    /// <summary>Over the picture, but the phone is not subscribed; counted in <see cref="TouchGestureStats.Dropped"/>.</summary>
    NotSubscribed,
}

/// <summary>Counters for the status display.</summary>
public sealed class TouchGestureStats
{
    /// <summary>Touches started (left or right button).</summary>
    public int Gestures { get; internal set; }

    /// <summary>Long presses started (right button); also counted in <see cref="Gestures"/>.</summary>
    public int LongPresses { get; internal set; }

    /// <summary>Wheel scrolls started.</summary>
    public int Scrolls { get; internal set; }

    /// <summary>Presses and wheel turns over the picture lost because the phone was not subscribed.</summary>
    public int Dropped { get; internal set; }

    /// <summary>Longest single send.</summary>
    public TimeSpan MaxSend { get; internal set; }

    /// <summary>How long the last touch was held (after the minimum-hold rule).</summary>
    public TimeSpan LastHold { get; internal set; }
}

/// <summary>
/// Turns mouse presses, moves, releases and wheel turns over the picture into touch reports. Ported from
/// <c>TouchBridge</c> in poc/poc3-integrated/mirror_touch.py, without any Windows dependency: the caller
/// supplies the picture rectangle in screen pixels and decides whether the point is over the mirror
/// window (not covered by another window).
/// </summary>
/// <remarks>
/// Not thread-safe; drive it from one loop. The methods wait (minimum gap, minimum hold) instead of
/// queueing, as the PoC did, so button events must be buffered by the caller meanwhile.
/// </remarks>
public sealed class TouchGestureEngine
{
    private static readonly PointD HomeStart = new(0.5, 0.995);
    private static readonly PointD HomeEnd = new(0.5, 0.65);
    private static readonly TimeSpan HomeDuration = TimeSpan.FromMilliseconds(220);

    private readonly TouchPlayer _player;
    private readonly TimeProvider _time;
    private readonly TouchTiming _timing;

    // Wheel scroll tuning, in picture units (0..1) and units per ms.
    private const double ArrivedDistance = 0.0005;

    // The shortest worthwhile stroke when the finger is put down again (a fraction of the picture).
    private const double MinStroke = 0.1;
    private const double MinStep = 0.002;
    private Rotation _landscapeRotation = Rotation.R90;
    private Rotation _portraitRotation = Rotation.R0;
    private WheelOptions _wheel = WheelOptions.Default;
    private TimeSpan _hold;
    private AxisPoint? _last;
    private long _downAt;
    private long _lastMoveAt;
    private long? _lastUpAt;
    private ScrollSession? _scroll;

    /// <summary>Creates an engine that sends to <paramref name="sink"/>.</summary>
    public TouchGestureEngine(ITouchSink sink, TimeProvider? time = null, TouchTiming? timing = null)
    {
        _time = time ?? TimeProvider.System;
        _player = new TouchPlayer(sink, _time);
        _timing = timing ?? TouchTiming.Default;
    }

    /// <summary>
    /// Rotation used while the picture is landscape. Which landscape is shown cannot be read from
    /// the stream, so this is a setting (Ctrl+R). 90 matches the landscape a game starts in.
    /// </summary>
    public Rotation LandscapeRotation
    {
        get => _landscapeRotation;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "unsupported rotation");
            }

            _landscapeRotation = value;
        }
    }

    /// <summary>
    /// Rotation used while the picture is portrait: <see cref="Rotation.R0"/>, or <see cref="Rotation.R180"/> for an
    /// iPad held upside down (the picture looks the same either way, so this is chosen by hand with Ctrl+R).
    /// </summary>
    public Rotation PortraitRotation
    {
        get => _portraitRotation;
        set
        {
            if (value is not (Rotation.R0 or Rotation.R180))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "a portrait rotation is 0 or 180");
            }

            _portraitRotation = value;
        }
    }

    /// <summary>How a wheel turn becomes a swipe.</summary>
    public WheelOptions Wheel
    {
        get => _wheel;
        set => _wheel = (value ?? throw new ArgumentNullException(nameof(value))).Validate();
    }

    /// <summary>The timing rules.</summary>
    public TouchTiming Timing => _timing;

    /// <summary>While true, presses are ignored (e.g. while a probe sends its own touches).</summary>
    public bool Paused { get; set; }

    /// <summary>True while a touch is in progress.</summary>
    public bool IsDown { get; private set; }

    /// <summary>True when the touch in progress is a two-finger pinch.</summary>
    public bool IsPinch { get; private set; }

    /// <summary>True when the touch in progress is a long press (held at least <see cref="TouchTiming.LongPressHold"/>).</summary>
    public bool IsLongPress { get; private set; }

    /// <summary>Rotation of the touch in progress; fixed when the touch starts.</summary>
    public Rotation ActiveRotation { get; private set; }

    /// <summary>Counters for the status display.</summary>
    public TouchGestureStats Stats { get; } = new();

    /// <summary>The player that owns the finger state; scripted gestures (probe) should go through it too.</summary>
    public TouchPlayer Player => _player;

    /// <summary>Rotation to apply for a picture shown at <paramref name="picture"/>.</summary>
    public Rotation RotationFor(RectD picture) => picture.IsLandscape ? _landscapeRotation : _portraitRotation;

    /// <summary>Switches the landscape rotation to the other landscape (90 &lt;-&gt; 270).</summary>
    public Rotation ToggleLandscapeRotation() => LandscapeRotation = Geometry.ToggleLandscape(_landscapeRotation);

    /// <summary>Turns the portrait rotation upside down (0 &lt;-&gt; 180).</summary>
    public Rotation TogglePortraitRotation() => PortraitRotation = _portraitRotation == Rotation.R0 ? Rotation.R180 : Rotation.R0;

    /// <summary>
    /// The left button went down at <paramref name="screen"/>. Starts a touch if the point is inside
    /// the picture. With <paramref name="pinch"/>, a second finger is mirrored through the screen center.
    /// </summary>
    /// <remarks>
    /// If the send fails, the touch stays "down" on purpose, so that the matching
    /// <see cref="ReleaseAsync"/> (or <see cref="AbortAsync"/>) still sends a lift to the phone.
    /// </remarks>
    public ValueTask<PressResult> PressAsync(PointD screen, RectD picture, bool pinch, CancellationToken cancellationToken = default) =>
        PressAsync(screen, picture, pinch, longPress: false, cancellationToken);

    /// <summary>
    /// Like <see cref="PressAsync(PointD, RectD, bool, CancellationToken)"/>; with <paramref name="longPress"/>
    /// the finger stays down at least <see cref="TouchTiming.LongPressHold"/> (the right button).
    /// </summary>
    public async ValueTask<PressResult> PressAsync(PointD screen, RectD picture, bool pinch, bool longPress, CancellationToken cancellationToken = default)
    {
        if (IsDown || IsScrolling || Paused || !picture.Contains(screen))
        {
            return PressResult.Ignored;
        }

        if (!_player.Sink.IsSubscribed)
        {
            Stats.Dropped++;
            return PressResult.NotSubscribed;
        }

        await WaitGapAsync(cancellationToken).ConfigureAwait(false);

        IsDown = true;
        IsPinch = pinch;
        IsLongPress = longPress;
        _hold = longPress ? _timing.LongPressHold : _timing.MinHold;
        ActiveRotation = RotationFor(picture);
        _downAt = _time.GetTimestamp();
        Stats.Gestures++;
        if (longPress)
        {
            Stats.LongPresses++;
        }

        await SendAsync(Geometry.ScreenToAxis(screen, picture, ActiveRotation), cancellationToken).ConfigureAwait(false);
        return PressResult.Started;
    }

    /// <summary>
    /// The cursor is at <paramref name="screen"/> (call every tick while down). Sends a move frame if
    /// the position changed and the move interval has passed. Outside the picture, it clamps to the edge.
    /// </summary>
    public async ValueTask MoveAsync(PointD screen, RectD picture, CancellationToken cancellationToken = default)
    {
        if (!IsDown || IsScrolling || !picture.HasArea)
        {
            return;
        }

        var p = Geometry.ScreenToAxis(screen, picture, ActiveRotation);
        if (p != _last && _time.GetElapsedTime(_lastMoveAt) >= _timing.MoveInterval)
        {
            await SendAsync(p, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The button went up at <paramref name="screen"/>. Delivers the final position (even if the move
    /// interval has not passed), keeps the finger down for the minimum hold (the long-press hold for the
    /// right button), then lifts it.
    /// </summary>
    public async ValueTask ReleaseAsync(PointD screen, RectD picture, CancellationToken cancellationToken = default)
    {
        if (!IsDown || IsScrolling)
        {
            return;
        }

        try
        {
            if (picture.HasArea)
            {
                var p = Geometry.ScreenToAxis(screen, picture, ActiveRotation);
                if (p != _last)
                {
                    await SendAsync(p, cancellationToken).ConfigureAwait(false);
                }
            }

            await DelayAsync(_hold - _time.GetElapsedTime(_downAt), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Lift even if the final move failed, so the phone does not keep a finger down.
            Stats.LastHold = _time.GetElapsedTime(_downAt);
            await LiftAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Lifts the finger at once (the window disappeared). Does nothing if no touch is in progress.</summary>
    public async ValueTask AbortAsync(CancellationToken cancellationToken = default)
    {
        if (IsScrolling)
        {
            await EndScrollAsync(cancellationToken).ConfigureAwait(false);
        }
        else if (IsDown)
        {
            await LiftAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>True while a wheel scroll is in progress (between the first wheel turn and the lift).</summary>
    public bool IsScrolling => _scroll is not null;

    /// <summary>
    /// The wheel turned with the cursor at <paramref name="screen"/>. Starts a scroll there (one finger down) or
    /// adds to the one in progress; <see cref="ScrollTickAsync"/> then moves the finger. <paramref name="notchesX"/>
    /// and <paramref name="notchesY"/> are wheel notches (fractions allowed), positive = right / up. Wheel up shows
    /// what is above, so the finger moves down; tilt right shows what is on the right, so the finger moves left.
    /// </summary>
    public async ValueTask<PressResult> WheelAsync(PointD screen, RectD picture, double notchesX, double notchesY, CancellationToken cancellationToken = default)
    {
        var wheel = _wheel;
        var delta = new PointD(-notchesX * wheel.StepPerNotch, notchesY * wheel.StepPerNotch);
        var now = _time.GetTimestamp();
        if (_scroll is { } s)
        {
            // More wheel: the finger (still down, or about to be put down again) has further to go.
            s.Target = Add(s.Target, delta);
            s.Recent.Enqueue((now, delta));
            s.LastWheelAt = now;
            s.Glided = false;
            if (s.Phase == ScrollPhase.Resting && !s.RegrabAfterRest)
            {
                s.Phase = ScrollPhase.Moving;
            }

            return PressResult.Started;
        }

        if (IsDown || Paused || !picture.Contains(screen) || (delta.X == 0 && delta.Y == 0))
        {
            return PressResult.Ignored;
        }

        if (!_player.Sink.IsSubscribed)
        {
            Stats.Dropped++;
            return PressResult.NotSubscribed;
        }

        await WaitGapAsync(cancellationToken).ConfigureAwait(false);
        now = _time.GetTimestamp();
        var anchor = Clamp(Geometry.ToUnit(screen, picture), wheel.EdgeMargin);
        _scroll = new ScrollSession
        {
            Anchor = anchor,
            Position = anchor,
            Target = Add(anchor, delta),
            LastWheelAt = now,
            LastFrameAt = now,
        };
        _scroll.Recent.Enqueue((now, delta));
        IsDown = true;
        IsPinch = false;
        IsLongPress = false;
        _hold = TimeSpan.Zero;
        ActiveRotation = RotationFor(picture);
        _downAt = now;
        Stats.Scrolls++;
        await SendUnitAsync(anchor, cancellationToken).ConfigureAwait(false);
        return PressResult.Started;
    }

    /// <summary>
    /// Advances the wheel scroll (call every tick; it sends at most one frame per move interval). The finger
    /// chases the wheel; when the wheel stops it glides on (<see cref="WheelOptions.Momentum"/>) and slows to a
    /// stop. It never lifts while moving: iOS would fling by an unpredictable amount (BLE delivers reports in
    /// bunches, so the speed iOS measures is unreliable). At the margin it stops, rests, lifts and is put down
    /// again where the scroll started.
    /// </summary>
    public async ValueTask ScrollTickAsync(CancellationToken cancellationToken = default)
    {
        if (_scroll is not { } s)
        {
            return;
        }

        var now = _time.GetTimestamp();
        var dt = _time.GetElapsedTime(s.LastFrameAt, now);
        if (dt < _timing.MoveInterval)
        {
            return;
        }

        var wheel = _wheel;
        s.LastFrameAt = now;
        switch (s.Phase)
        {
            case ScrollPhase.Regrab:
                if (_time.GetElapsedTime(s.PhaseAt, now) >= _timing.MinGap)
                {
                    // Put the finger down where the scroll started, keeping the distance still to go. If the scroll
                    // started at (or just before) the margin it is heading for (the cursor near the bottom, wheel
                    // up), strokes from there move little or nothing on that axis: start from the middle instead, or
                    // the finger would be put down and lifted over and over (for ever at the margin) (code review,
                    // 2026-11-07).
                    var rest = Sub(s.Target, s.Position);
                    var reach = Sub(Clamp(Add(s.Anchor, rest), wheel.EdgeMargin), s.Anchor);
                    s.Anchor = new PointD(
                        ShortStroke(rest.X, reach.X) ? 0.5 : s.Anchor.X,
                        ShortStroke(rest.Y, reach.Y) ? 0.5 : s.Anchor.Y);
                    s.Target = Add(s.Anchor, rest);
                    s.Position = s.Anchor;
                    s.Phase = ScrollPhase.Moving;
                    IsDown = true;
                    _downAt = now;
                    await SendUnitAsync(s.Anchor, cancellationToken).ConfigureAwait(false);
                }

                return;

            case ScrollPhase.Moving:
                var idle = _time.GetElapsedTime(s.LastWheelAt, now) >= wheel.IdleEnd;
                if (idle && !s.Glided)
                {
                    // Our own momentum: carry on in proportion to how fast the wheel was turning.
                    s.Glided = true;
                    var speed = WheelSpeed(s, s.LastWheelAt);
                    s.Target = Add(s.Target, Scale(speed, wheel.Momentum * wheel.GlideTime.TotalMilliseconds));
                }

                // This stroke ends at the margin; the rest of the way needs another stroke.
                var goal = Clamp(s.Target, wheel.EdgeMargin);
                var remaining = Sub(goal, s.Position);
                if (Length(remaining) < ArrivedDistance)
                {
                    var more = Length(Sub(s.Target, goal)) >= ArrivedDistance;
                    if (more || idle)
                    {
                        s.Phase = ScrollPhase.Resting;
                        s.RegrabAfterRest = more;
                        s.PhaseAt = now;
                    }

                    return;
                }

                var follow = s.Glided ? wheel.GlideFollowTime : wheel.FollowTime;
                var k = 1 - Math.Exp(-dt.TotalMilliseconds / follow.TotalMilliseconds);
                var step = Scale(remaining, k);
                if (Length(step) < MinStep)
                {
                    step = Length(remaining) <= MinStep ? remaining : Scale(remaining, MinStep / Length(remaining));
                }

                s.Position = Add(s.Position, step);
                await SendUnitAsync(s.Position, cancellationToken).ConfigureAwait(false);
                return;

            case ScrollPhase.Resting:
                var settle = s.RegrabAfterRest ? wheel.RegrabSettle : wheel.Settle;
                if (_time.GetElapsedTime(s.PhaseAt, now) + wheel.HoldInterval > settle)
                {
                    // Lift exactly at the end position.
                    var end = UnitToAxis(s.Position);
                    if (_last != end)
                    {
                        await SendAsync(end, cancellationToken).ConfigureAwait(false);
                    }

                    if (s.RegrabAfterRest)
                    {
                        await LiftAsync(cancellationToken).ConfigureAwait(false);
                        s.RegrabAfterRest = false;
                        s.Phase = ScrollPhase.Regrab;
                        s.PhaseAt = _time.GetTimestamp();
                    }
                    else
                    {
                        await EndScrollAsync(cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }

                // A tiny wiggle tells iOS the finger is (almost) still; silence would keep the last speed.
                s.Odd = !s.Odd;
                var j = s.Odd ? wheel.JitterUnits : 0;
                var at = UnitToAxis(s.Position);
                await SendAsync(TouchPlayer.Axis(at.X + j, at.Y + j), cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Ends a wheel scroll at once (a click came, or the window went away).</summary>
    public async ValueTask EndScrollAsync(CancellationToken cancellationToken = default)
    {
        if (_scroll is not { } s)
        {
            return;
        }

        _scroll = null;
        Stats.LastHold = _time.GetElapsedTime(_downAt);
        if (s.Phase != ScrollPhase.Regrab && IsDown)
        {
            await LiftAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // How fast the wheel moved the target (units per ms) over the window before the last turn.
    private PointD WheelSpeed(ScrollSession s, long lastWheelAt)
    {
        var window = _wheel.SpeedWindow;
        var sum = default(PointD);
        foreach (var (at, delta) in s.Recent)
        {
            if (_time.GetElapsedTime(at, lastWheelAt) <= window)
            {
                sum = Add(sum, delta);
            }
        }

        s.Recent.Clear();
        return Scale(sum, 1 / window.TotalMilliseconds);
    }

    private ValueTask SendUnitAsync(PointD unit, CancellationToken cancellationToken) => SendAsync(UnitToAxis(unit), cancellationToken);

    private AxisPoint UnitToAxis(PointD unit) => Geometry.UnitToAxis(Geometry.Rotate(unit, ActiveRotation));

    private static PointD Add(PointD a, PointD b) => new(a.X + b.X, a.Y + b.Y);

    private static PointD Sub(PointD a, PointD b) => new(a.X - b.X, a.Y - b.Y);

    private static PointD Scale(PointD a, double k) => new(a.X * k, a.Y * k);

    // A stroke that covers neither the rest of the way nor MinStroke of the picture (on one axis).
    private static bool ShortStroke(double rest, double reach) =>
        Math.Abs(rest) >= ArrivedDistance && Math.Abs(reach) < Math.Min(Math.Abs(rest), MinStroke) - ArrivedDistance;

    private static double Length(PointD a) => Math.Sqrt((a.X * a.X) + (a.Y * a.Y));

    private static PointD Clamp(PointD p, double margin) =>
        new(Math.Clamp(p.X, margin, 1 - margin), Math.Clamp(p.Y, margin, 1 - margin));

    private enum ScrollPhase
    {
        Moving,
        Resting,
        Regrab,
    }

    // State of the wheel scroll in progress, in picture units (0..1, before rotation).
    private sealed class ScrollSession
    {
        public PointD Anchor { get; set; }

        public PointD Position { get; set; }

        public PointD Target { get; set; }

        public ScrollPhase Phase { get; set; }

        public long LastWheelAt { get; set; }

        public long LastFrameAt { get; set; }

        public long PhaseAt { get; set; }

        // Wheel turns since the last glide, for the wheel speed.
        public Queue<(long At, PointD Delta)> Recent { get; } = new();

        // The momentum glide for the current wheel input has been added.
        public bool Glided { get; set; }

        // The rest is at the margin: lift and put the finger down again afterwards.
        public bool RegrabAfterRest { get; set; }

        public bool Odd { get; set; }
    }

    /// <summary>Home goes through the home button (a HID key) rather than the swipe.</summary>
    public bool HomeIsKey => _player.Sink is IHomeButtonSink { HasHomeButton: true };

    /// <summary>
    /// Home: the home button when the sink has one (<see cref="IHomeButtonSink"/>), else a swipe up from the bottom
    /// edge of the PICTURE, rotated like any other touch. Returns false (and sends nothing) while a touch is in
    /// progress or the phone is not subscribed.
    /// </summary>
    public async ValueTask<bool> HomeAsync(RectD picture, CancellationToken cancellationToken = default)
    {
        if (IsDown || IsScrolling || !_player.Sink.IsSubscribed)
        {
            return false;
        }

        if (_player.Sink is IHomeButtonSink { HasHomeButton: true } button)
        {
            await button.PressHomeAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var (start, end) = HomeSwipe(picture);
        await _player.SwipeAsync(start, end, HomeDuration, cancellationToken: cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>The home swipe's start and end for a picture shown at <paramref name="picture"/>.</summary>
    public (AxisPoint Start, AxisPoint End) HomeSwipe(RectD picture)
    {
        var rotation = RotationFor(picture);
        return (Geometry.UnitToAxis(Geometry.Rotate(HomeStart, rotation)), Geometry.UnitToAxis(Geometry.Rotate(HomeEnd, rotation)));
    }

    private async ValueTask SendAsync(AxisPoint p, CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        if (IsPinch)
        {
            await _player.DownAsync(p, Geometry.MirrorPoint(p), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _player.DownAsync(p, cancellationToken).ConfigureAwait(false);
        }

        _last = p;
        _lastMoveAt = _time.GetTimestamp();
        RecordSend(started);
    }

    private async ValueTask LiftAsync(CancellationToken cancellationToken)
    {
        var started = _time.GetTimestamp();
        try
        {
            await _player.LiftAsync(cancellationToken).ConfigureAwait(false);
            RecordSend(started);
        }
        finally
        {
            // Never get stuck "down", even if the link failed.
            IsDown = false;
            IsPinch = false;
            IsLongPress = false;
            _last = null;
            _lastUpAt = _time.GetTimestamp();
        }
    }

    private async ValueTask WaitGapAsync(CancellationToken cancellationToken)
    {
        if (_lastUpAt is { } upAt)
        {
            await DelayAsync(_timing.MinGap - _time.GetElapsedTime(upAt), cancellationToken).ConfigureAwait(false);
        }
    }

    private void RecordSend(long started)
    {
        var elapsed = _time.GetElapsedTime(started);
        if (elapsed > Stats.MaxSend)
        {
            Stats.MaxSend = elapsed;
        }
    }

    private async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _time, cancellationToken).ConfigureAwait(false);
        }
    }
}
