namespace IceScreenTouch.Core;

/// <summary>
/// How the mouse wheel scrolls. While the wheel turns, one finger stays down and follows the wheel smoothly
/// (like a trackpad); when the wheel stops, the finger glides on by <see cref="Momentum"/>, slows to a stop,
/// rests and lifts. iOS's own fling is never used: lifting a moving finger flung the page by 4x to 150x the
/// travel for the same input (scroll probe on the iPhone, 2026-11-05), because BLE delivers reports in bunches.
/// </summary>
public sealed record WheelOptions
{
    /// <summary>Default settings.</summary>
    public static WheelOptions Default { get; } = new();

    /// <summary>
    /// Finger travel per wheel notch, as a fraction of the picture height (width for a horizontal wheel). The
    /// default (with <see cref="Momentum"/> 0.1) felt natural on a Kensington Slimblade Pro (user, 2026-11-05).
    /// </summary>
    public double StepPerNotch { get; init; } = 0.05;

    /// <summary>
    /// The finger stays this far (fraction of the picture) from every edge: swipes from an edge are system
    /// gestures on iOS (home, Notification Center, Control Center, back). At the margin it is put down again
    /// where the scroll started and carries on.
    /// </summary>
    public double EdgeMargin { get; init; } = 0.05;

    /// <summary>Time constant of the finger chasing the wheel: smaller is snappier, larger is smoother.</summary>
    public TimeSpan FollowTime { get; init; } = TimeSpan.FromMilliseconds(60);

    /// <summary>The scroll ends when the wheel has not turned for this long.</summary>
    public TimeSpan IdleEnd { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Glide after the wheel stops: the finger carries on by Momentum x (wheel speed over <see cref="SpeedWindow"/>)
    /// x <see cref="GlideTime"/>. 0 = it stops with the wheel; a quick spin glides further than a single notch.
    /// </summary>
    public double Momentum { get; init; } = 0.1;

    /// <summary>The wheel speed is the travel the wheel asked for during this window before it stopped.</summary>
    public TimeSpan SpeedWindow { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long the glide would last at the wheel's speed (Momentum = 1).</summary>
    public TimeSpan GlideTime { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Time constant of the glide slowing down (larger = a longer, softer stop).</summary>
    public TimeSpan GlideFollowTime { get; init; } = TimeSpan.FromMilliseconds(140);

    /// <summary>Rest at the margin before the finger is lifted and put down again (shorter than <see cref="Settle"/>).</summary>
    public TimeSpan RegrabSettle { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Without momentum, the finger rests this long before lifting, sending a tiny alternating offset every
    /// <see cref="HoldInterval"/>. Measured on the iPhone (2026-11-05): resting silently still flung (iOS keeps
    /// the last move's speed; 7 of 8), and resending the same position did not help; the wiggle never flung (0 of 16).
    /// </summary>
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>Interval of the reports sent while resting.</summary>
    public TimeSpan HoldInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    /// <summary>Size of the resting wiggle in digitizer units (10000 = full screen).</summary>
    public int JitterUnits { get; init; } = 4;

    /// <summary>Checks the values; returns this instance.</summary>
    public WheelOptions Validate()
    {
        if (!(StepPerNotch > 0 && StepPerNotch <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(StepPerNotch), StepPerNotch, "must be in (0, 1]");
        }

        if (!(EdgeMargin >= 0 && EdgeMargin < 0.4))
        {
            throw new ArgumentOutOfRangeException(nameof(EdgeMargin), EdgeMargin, "must be in [0, 0.4)");
        }

        if (!(Momentum >= 0 && Momentum <= 2))
        {
            throw new ArgumentOutOfRangeException(nameof(Momentum), Momentum, "must be in [0, 2]");
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(FollowTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(IdleEnd, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(SpeedWindow, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(GlideTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(GlideFollowTime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(RegrabSettle, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(Settle, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(HoldInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(JitterUnits);
        return this;
    }
}
