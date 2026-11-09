using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IceScreenTouch.Core;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Low-level mouse hook (WH_MOUSE_LL) on its own thread with a message loop. Reports every left / right button
/// transition and wheel turn, so a fast click is never missed even while a BLE send is in progress. Cursor movement is
/// deliberately not reported: poll the cursor instead (SetCursorPos-style moves never reach the hook).
/// Every event is reported, but it is passed on to the windows only unless <see cref="SwallowZone"/> says to keep a
/// button press from them (<see cref="ClickSwallower"/>); that is off by default.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly Action<MouseInputEvent> _onInput;
    private readonly Action<nint>? _onSwallowedPress;
    private readonly ClickSwallower _swallower;
    private readonly HookProc _proc; // kept alive: the OS holds a pointer to it
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _stopping;
    private volatile SwallowZone? _zone;
    private readonly Func<int, bool>? _keyDown;
    private nint _hook;
    private int _error;

    /// <summary>
    /// Creates the hook (not installed yet). <paramref name="onInput"/> runs on the hook thread and must be
    /// tiny (e.g. a channel write): Windows drops hooks that take too long. So must <paramref name="onSwallowedPress"/>,
    /// which gets the zone's top-level window when a press was swallowed (to bring it to the front off this thread).
    /// </summary>
    public MouseHook(Action<MouseInputEvent> onInput, Action<nint>? onSwallowedPress = null, ClickSwallower? swallower = null, Func<int, bool>? keyDown = null)
    {
        ArgumentNullException.ThrowIfNull(onInput);
        _onInput = onInput;
        _onSwallowedPress = onSwallowedPress;
        _swallower = swallower ?? new ClickSwallower();
        _keyDown = keyDown;
        _proc = Callback;
    }

    /// <summary>
    /// Where button presses are swallowed, or null for nowhere. Set from any thread; the hook thread reads the
    /// latest value without waiting.
    /// </summary>
    public SwallowZone? SwallowZone
    {
        get => _zone;
        set => _zone = value;
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    /// <summary>Installs the hook. Throws <see cref="Win32Exception"/> if Windows refuses.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("the hook is already started");
        }

        _thread = new Thread(Run) { Name = "mouse-hook", IsBackground = true };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(2)) || _hook == 0)
        {
            throw new Win32Exception(_error, "SetWindowsHookEx(WH_MOUSE_LL) failed");
        }
    }

    /// <summary>Removes the hook and ends its thread.</summary>
    public void Dispose()
    {
        _zone = null; // the presses go back to the windows at once
        _stopping = true;
        if (_threadId != 0)
        {
            _ = PostThreadMessage(_threadId, WmQuit, 0, 0);
        }

        // A thread that is still starting will set _ready later: keep it until the thread has ended.
        if (_thread is null || _thread.Join(TimeSpan.FromSeconds(1)))
        {
            _ready.Dispose();
        }
    }

    private void Run() => RunHookLoop(
        () => SetWindowsHookEx(WhMouseLl, Marshal.GetFunctionPointerForDelegate(_proc), GetModuleHandle(null), 0),
        id => _threadId = id,
        (hook, error) =>
        {
            _hook = hook;
            _error = error;
            _ready.Set();
        },
        () => _stopping);

    private nint Callback(int code, nint wParam, nint lParam)
    {
        if (code >= 0)
        {
            var message = (uint)wParam;
            if (message is WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or WmMouseWheel or WmMouseHWheel)
            {
                var info = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
                if (Process(message, info, Stopwatch.GetTimestamp()))
                {
                    return 1; // a nonzero result keeps the event from the rest of the chain and from the window
                }
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>
    /// What the hook does with one mouse message: reports it (<c>onInput</c>), decides whether it is swallowed
    /// (<see cref="ClickSwallower"/>, with the zone as it is at <paramref name="now"/>, a Stopwatch timestamp), and for a
    /// swallowed press tells <c>onSwallowedPress</c> the zone's top-level window. A callback that throws changes nothing
    /// else: the event is still reported or swallowed as decided, and no exception reaches the OS hook chain.
    /// </summary>
    internal bool Process(uint message, MsLlHookStruct info, long now)
    {
        var e = Translate(message, info, _keyDown);
        var zone = _zone;
        var swallow = Guarded(() => _swallower.ShouldSwallow(e, zone, now));
        _ = Guarded(() =>
        {
            _onInput(e);
            return true;
        });
        if (swallow && e.Kind == MouseInputKind.Press && zone is not null && _onSwallowedPress is { } swallowed)
        {
            _ = Guarded(() =>
            {
                swallowed(zone.Root);
                return true;
            });
        }

        return swallow;
    }

    // An exception must never escape into the OS hook chain: a callback that throws counts as "false".
    private static bool Guarded(Func<bool> action)
    {
        try
        {
            return action();
        }
#pragma warning disable CA1031 // See above.
        catch (Exception)
#pragma warning restore CA1031
        {
            return false;
        }
    }

    /// <summary>Turns a hooked mouse message into an event. Ctrl and Alt are read at the press; Shift turns the wheel sideways.</summary>
    internal static MouseInputEvent Translate(uint message, MsLlHookStruct info, Func<int, bool>? keyDown = null)
    {
        keyDown ??= Win32Desktop.IsVirtualKeyDown;
        var at = new PointD(info.Pt.X, info.Pt.Y);
        switch (message)
        {
            case WmLButtonDown:
                return MouseInputEvent.Press(MouseButton.Left, at, keyDown(VkControl), keyDown(VkMenu));
            case WmLButtonUp:
                return MouseInputEvent.Release(MouseButton.Left, at);
            case WmRButtonDown:
                return MouseInputEvent.Press(MouseButton.Right, at, alt: keyDown(VkMenu));
            case WmRButtonUp:
                return MouseInputEvent.Release(MouseButton.Right, at);
            case WmMouseHWheel:
                return MouseInputEvent.Wheel(at, WheelDelta(info.MouseData), horizontal: true);
            default:
                // Shift + wheel scrolls sideways in most Windows apps: wheel down (negative) = right.
                var delta = WheelDelta(info.MouseData);
                return keyDown(VkShift)
                    ? MouseInputEvent.Wheel(at, -delta, horizontal: true)
                    : MouseInputEvent.Wheel(at, delta);
        }
    }

    // The high word of mouseData is the signed wheel delta.
    private static int WheelDelta(uint mouseData) => unchecked((short)(mouseData >> 16));
}
