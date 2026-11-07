using System.ComponentModel;
using System.Runtime.InteropServices;
using static IceScreenTouch.Platform.Windows.NativeMethods;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// Low-level keyboard hook (WH_KEYBOARD_LL) on its own thread with a message loop. Every key transition goes to a
/// <see cref="KeyboardForwarder"/>, which decides whether the key is swallowed (sent to the device, or a shortcut)
/// or left to Windows.
/// </summary>
public sealed class KeyboardHook : IDisposable
{
    private readonly KeyboardForwarder _forwarder;
    private readonly HookProc _proc; // kept alive: the OS holds a pointer to it
    private readonly ManualResetEventSlim _ready = new();
    private Thread? _thread;
    private volatile uint _threadId;
    private volatile bool _stopping;
    private nint _hook;
    private int _error;

    /// <summary>Creates the hook (not installed yet). The forwarder is called on the hook thread.</summary>
    public KeyboardHook(KeyboardForwarder forwarder)
    {
        ArgumentNullException.ThrowIfNull(forwarder);
        _forwarder = forwarder;
        _proc = Callback;
    }

    private delegate nint HookProc(int code, nint wParam, nint lParam);

    /// <summary>The forwarder the hook feeds.</summary>
    public KeyboardForwarder Forwarder => _forwarder;

    /// <summary>Installs the hook. Throws <see cref="Win32Exception"/> if Windows refuses.</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            throw new InvalidOperationException("the hook is already started");
        }

        _thread = new Thread(Run) { Name = "keyboard-hook", IsBackground = true };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(2)) || _hook == 0)
        {
            throw new Win32Exception(_error, "SetWindowsHookEx(WH_KEYBOARD_LL) failed");
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

    /// <summary>
    /// The character the keyboard layout of the focused window types for <paramref name="vk"/> with only Shift
    /// considered (no Ctrl, Alt or Caps Lock), or null for a non-character or dead key. Leaves the keyboard state
    /// alone (ToUnicodeEx flag 4, Windows 10 1607 and later).
    /// </summary>
    public static unsafe char? ToChar(int vk, int scanCode, bool shift)
    {
        var state = stackalloc byte[256];
        if (shift)
        {
            state[VirtualKeys.Shift] = 0x80;
        }

        var buffer = stackalloc char[4];
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        var n = ToUnicodeEx((uint)vk, (uint)scanCode, state, buffer, 4, ToUnicodeNoStateChange, layout);
        return n == 1 && !char.IsControl(buffer[0]) ? buffer[0] : null;
    }

    private void Run() => RunHookLoop(
        () => SetWindowsHookEx(WhKeyboardLl, Marshal.GetFunctionPointerForDelegate(_proc), GetModuleHandle(null), 0),
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
            if (message is WmKeyDown or WmKeyUp or WmSysKeyDown or WmSysKeyUp)
            {
                var info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                try
                {
                    if (_forwarder.OnKey((int)info.VkCode, (int)info.ScanCode, message is WmKeyDown or WmSysKeyDown))
                    {
                        return 1; // swallowed
                    }
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
}
