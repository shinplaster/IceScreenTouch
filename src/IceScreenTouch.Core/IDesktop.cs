namespace IceScreenTouch.Core;

/// <summary>Keyboard shortcuts of the bridge. They only count while the mirror window has the focus.</summary>
public enum ShortcutKey
{
    /// <summary>Home gesture (the Home key on Windows).</summary>
    Home,

    /// <summary>Switch the landscape rotation between 90 and 270 (Ctrl+R on Windows).</summary>
    Rotate,
}

/// <summary>Mouse buttons the bridge turns into touches.</summary>
public enum MouseButton
{
    /// <summary>Touch (with Ctrl: pinch).</summary>
    Left,

    /// <summary>Long press: the finger stays down at least <see cref="TouchTiming.LongPressHold"/>.</summary>
    Right,
}

/// <summary>What a <see cref="MouseInputEvent"/> reports.</summary>
public enum MouseInputKind
{
    /// <summary>A button went down.</summary>
    Press,

    /// <summary>A button went up.</summary>
    Release,

    /// <summary>The wheel turned (<see cref="MouseInputEvent.WheelDelta"/>).</summary>
    Wheel,
}

/// <summary>A button transition or a wheel turn reported by the OS (a low-level hook on Windows).</summary>
/// <param name="Kind">Press, release or wheel.</param>
/// <param name="Button">The button (press and release only).</param>
/// <param name="Position">Cursor position in physical screen pixels.</param>
/// <param name="Control">Ctrl was held at the press (pinch).</param>
/// <param name="Alt">Alt (either one) was held at the press: the press belongs to the receiver, not to the phone.</param>
/// <param name="WheelDelta">Wheel movement; 120 is one notch. Positive = away from the user (up) or to the right.</param>
/// <param name="Horizontal">A horizontal wheel (tilt, or Shift + wheel).</param>
public readonly record struct MouseInputEvent(
    MouseInputKind Kind,
    MouseButton Button,
    PointD Position,
    bool Control = false,
    int WheelDelta = 0,
    bool Horizontal = false,
    bool Alt = false)
{
    /// <summary>One notch of a standard wheel.</summary>
    public const int WheelNotch = 120;

    /// <summary>A button went down at <paramref name="position"/>.</summary>
    public static MouseInputEvent Press(MouseButton button, PointD position, bool control = false, bool alt = false) =>
        new(MouseInputKind.Press, button, position, control, Alt: alt);

    /// <summary>A button went up at <paramref name="position"/>.</summary>
    public static MouseInputEvent Release(MouseButton button, PointD position) =>
        new(MouseInputKind.Release, button, position);

    /// <summary>The wheel turned by <paramref name="delta"/> with the cursor at <paramref name="position"/>.</summary>
    public static MouseInputEvent Wheel(PointD position, int delta, bool horizontal = false) =>
        new(MouseInputKind.Wheel, MouseButton.Left, position, WheelDelta: delta, Horizontal: horizontal);
}

/// <summary>What is needed to size a window: its frame (chrome) and the work area of its monitor.</summary>
/// <param name="FrameWidth">Window width minus client width.</param>
/// <param name="FrameHeight">Window height minus client height.</param>
/// <param name="WorkArea">Work area (without the taskbar) of the monitor that holds the window.</param>
public readonly record struct WindowMetrics(int FrameWidth, int FrameHeight, RectI WorkArea);

/// <summary>
/// The desktop the bridge looks at: cursor, keys and the mirror window. Win32 on Windows; a fake in
/// tests. All coordinates are physical screen pixels. Window handles are opaque (0 = none).
/// </summary>
public interface IDesktop
{
    /// <summary>Current cursor position. Polled, because synthetic moves (SetCursorPos) never reach a hook.</summary>
    PointD CursorPosition { get; }

    /// <summary>True while the shortcut key is held.</summary>
    bool IsKeyDown(ShortcutKey key);

    /// <summary>
    /// How many times the shortcut was pressed since the last call, or null when this desktop does not count presses
    /// (the bridge then fires on the edge of <see cref="IsKeyDown"/>). A hook that swallows the key counts every
    /// press, even one that is up again before the bridge looks, and a press made while the bridge was busy with the
    /// one before: the held state alone would show both as one long hold (review, 2026-11-07). Called every tick, so
    /// a press made while the mirror is not in front is used up and does not fire later.
    /// </summary>
    int? TakeShortcutPresses(ShortcutKey key) => null;

    /// <summary>True when <paramref name="window"/> (its top-level window) has the keyboard focus.</summary>
    bool IsForeground(nint window);

    /// <summary>True when the top-level window under <paramref name="screen"/> is <paramref name="window"/> (not covered).</summary>
    bool IsVisibleAt(nint window, PointD screen);

    /// <summary>Client area of <paramref name="window"/> in screen pixels, or null if it cannot be measured.</summary>
    RectI? GetClientRect(nint window);

    /// <summary>Frame size and monitor work area of <paramref name="window"/>, or null if it cannot be measured.</summary>
    WindowMetrics? GetMetrics(nint window);

    /// <summary>
    /// Resizes <paramref name="window"/> so that its client area becomes width x height. The window keeps its
    /// top-left corner unless it would leave the monitor's work area.
    /// </summary>
    void SetClientSize(nint window, int width, int height);

    /// <summary>True when <paramref name="window"/> is maximized or covers its monitor: its size is the user's.</summary>
    bool IsMaximizedOrFullScreen(nint window) => false;
}

/// <summary>Where the picture is: one implementation per receiver kind (UxPlay, any window).</summary>
public interface IVideoSource
{
    /// <summary>Short name for the status display.</summary>
    string Name { get; }

    /// <summary>
    /// Size of the picture as drawn (follows rotation), or null if the receiver has not reported it;
    /// the bridge then assumes <see cref="Geometry.FallbackVideoSize"/>.
    /// </summary>
    SizeD? VideoSize { get; }

    /// <summary>
    /// The window that shows the picture, or 0 if there is none (not running, minimized). A top-level window, or
    /// the content window inside the frame of a UWP app (<see cref="IDesktop"/> then answers for its top-level window).
    /// </summary>
    nint FindWindow();

    /// <summary>
    /// Where the picture is inside the client area of <paramref name="window"/> (relative to its top-left corner),
    /// for receivers that do not report the picture size but whose window can be looked at. Null (the default)
    /// makes the bridge center a picture of <see cref="VideoSize"/> in the client area.
    /// </summary>
    RectD? LocatePicture(nint window, SizeD client) => null;

    /// <summary>
    /// The client size the bridge gives the window when it follows the picture (the bridge caps it by the work
    /// area and resizes once per value), or null to leave the window alone. The default is <see cref="VideoSize"/>;
    /// a receiver that does not report it can still ask for a shape (see <see cref="WindowShapeFollower"/>).
    /// </summary>
    SizeD? WindowSize => VideoSize;
}
