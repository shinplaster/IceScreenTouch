using System.ComponentModel;
using System.Runtime.InteropServices;
using IceScreenTouch.Core;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Low-level mouse hook (WH_MOUSE_LL) on its own thread with a message loop. Reports every left / right button
/// transition and wheel turn, so a fast click is never missed even while a BLE send is in progress. Cursor movement is
/// deliberately not reported: poll the cursor instead (SetCursorPos-style moves never reach the hook).
/// The hook never swallows events.
/// </summary>
public sealed class MouseHook : IDisposable
{
    private readonly Action<MouseInputEvent> _onInput;
    private readonly HookProc _proc; // kept alive: the OS holds a pointer to it
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _stopping;
    private nint _hook;
    private int _error;

    /// <summary>
    /// Creates the hook (not installed yet). <paramref name="onInput"/> runs on the hook thread and must be
    /// tiny (e.g. a channel write): Windows drops hooks that take too long.
    /// </summary>
    public MouseHook(Action<MouseInputEvent> onInput)
    {
        ArgumentNullException.ThrowIfNull(onInput);
        _onInput = onInput;
        _proc = Callback;
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
                try
                {
                    _onInput(Translate(message, info));
                }
#pragma warning disable CA1031 // An exception must never escape into the OS hook chain.
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }

        return CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>Turns a hooked mouse message into an event. Ctrl is read at the press; Shift turns the wheel sideways.</summary>
    internal static MouseInputEvent Translate(uint message, MsLlHookStruct info)
    {
        var at = new PointD(info.Pt.X, info.Pt.Y);
        switch (message)
        {
            case WmLButtonDown:
                return MouseInputEvent.Press(MouseButton.Left, at, Win32Desktop.IsVirtualKeyDown(VkControl));
            case WmLButtonUp:
                return MouseInputEvent.Release(MouseButton.Left, at);
            case WmRButtonDown:
                return MouseInputEvent.Press(MouseButton.Right, at);
            case WmRButtonUp:
                return MouseInputEvent.Release(MouseButton.Right, at);
            case WmMouseHWheel:
                return MouseInputEvent.Wheel(at, WheelDelta(info.MouseData), horizontal: true);
            default:
                // Shift + wheel scrolls sideways in most Windows apps: wheel down (negative) = right.
                var delta = WheelDelta(info.MouseData);
                return Win32Desktop.IsVirtualKeyDown(VkShift)
                    ? MouseInputEvent.Wheel(at, -delta, horizontal: true)
                    : MouseInputEvent.Wheel(at, delta);
        }
    }

    // The high word of mouseData is the signed wheel delta.
    private static int WheelDelta(uint mouseData) => unchecked((short)(mouseData >> 16));
}
