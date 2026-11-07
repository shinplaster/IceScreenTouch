"""Minimal ctypes wrappers for the Win32 calls PoC 3 needs (no pywin32 dependency)."""

from __future__ import annotations

import ctypes
from ctypes import wintypes as wt

user32 = ctypes.WinDLL("user32", use_last_error=True)
winmm = ctypes.WinDLL("winmm")

HWND = wt.HWND
GA_ROOT = 2

VK_LBUTTON = 0x01
VK_CONTROL = 0x11
VK_H = 0x48
VK_R = 0x52

user32.GetCursorPos.argtypes = [ctypes.POINTER(wt.POINT)]
user32.GetCursorPos.restype = wt.BOOL
user32.GetAsyncKeyState.argtypes = [ctypes.c_int]
user32.GetAsyncKeyState.restype = ctypes.c_short
user32.GetForegroundWindow.restype = HWND
user32.WindowFromPoint.argtypes = [wt.POINT]
user32.WindowFromPoint.restype = HWND
user32.GetAncestor.argtypes = [HWND, wt.UINT]
user32.GetAncestor.restype = HWND
user32.GetClientRect.argtypes = [HWND, ctypes.POINTER(wt.RECT)]
user32.GetClientRect.restype = wt.BOOL
user32.ClientToScreen.argtypes = [HWND, ctypes.POINTER(wt.POINT)]
user32.ClientToScreen.restype = wt.BOOL
user32.IsWindowVisible.argtypes = [HWND]
user32.IsWindowVisible.restype = wt.BOOL
user32.IsIconic.argtypes = [HWND]
user32.IsIconic.restype = wt.BOOL
user32.GetWindowThreadProcessId.argtypes = [HWND, ctypes.POINTER(wt.DWORD)]
user32.GetWindowThreadProcessId.restype = wt.DWORD

_EnumProc = ctypes.WINFUNCTYPE(wt.BOOL, HWND, wt.LPARAM)
user32.EnumWindows.argtypes = [_EnumProc, wt.LPARAM]
user32.EnumWindows.restype = wt.BOOL


def enable_dpi_awareness() -> None:
    """Make cursor and window coordinates physical pixels on every monitor."""
    try:
        user32.SetProcessDpiAwarenessContext.argtypes = [ctypes.c_void_p]
        user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))  # PER_MONITOR_AWARE_V2
    except (AttributeError, OSError):
        try:
            ctypes.WinDLL("shcore").SetProcessDpiAwareness(2)
        except (AttributeError, OSError):
            pass


def set_timer_resolution(ms: int = 1) -> None:
    """Without this asyncio.sleep() granularity is ~15.6 ms on Windows."""
    winmm.timeBeginPeriod(ms)


def reset_timer_resolution(ms: int = 1) -> None:
    winmm.timeEndPeriod(ms)


def cursor_pos() -> tuple[int, int]:
    pt = wt.POINT()
    user32.GetCursorPos(ctypes.byref(pt))
    return pt.x, pt.y


def key_down(vk: int) -> bool:
    return bool(user32.GetAsyncKeyState(vk) & 0x8000)


def foreground_window() -> int | None:
    return user32.GetForegroundWindow()


def root_window_at(x: int, y: int) -> int | None:
    """Top-level window under a screen point (None if there is none)."""
    child = user32.WindowFromPoint(wt.POINT(x, y))
    return user32.GetAncestor(child, GA_ROOT) if child else None


def root_of(hwnd: int) -> int | None:
    return user32.GetAncestor(hwnd, GA_ROOT)


def client_rect_screen(hwnd: int) -> tuple[int, int, int, int] | None:
    """Client area as (left, top, width, height) in screen pixels."""
    rc = wt.RECT()
    if not user32.GetClientRect(hwnd, ctypes.byref(rc)):
        return None
    origin = wt.POINT(0, 0)
    if not user32.ClientToScreen(hwnd, ctypes.byref(origin)):
        return None
    return origin.x, origin.y, rc.right - rc.left, rc.bottom - rc.top


def largest_visible_window_of_pid(pid: int) -> int | None:
    """The biggest visible, non-minimized top-level window owned by a process."""
    best: tuple[int, int] | None = None  # (area, hwnd)

    def cb(hwnd, _lparam):
        nonlocal best
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value != pid or not user32.IsWindowVisible(hwnd) or user32.IsIconic(hwnd):
            return True
        rc = client_rect_screen(hwnd)
        if rc and rc[2] > 0 and rc[3] > 0:
            area = rc[2] * rc[3]
            if best is None or area > best[0]:
                best = (area, hwnd)
        return True

    user32.EnumWindows(_EnumProc(cb), 0)
    return best[1] if best else None


# --- window placement ------------------------------------------------------------------------------

SWP_NOZORDER = 0x0004
SWP_NOACTIVATE = 0x0010
MONITOR_DEFAULTTONEAREST = 2


class _MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("rcMonitor", wt.RECT), ("rcWork", wt.RECT), ("dwFlags", wt.DWORD)]


user32.GetWindowRect.argtypes = [HWND, ctypes.POINTER(wt.RECT)]
user32.GetWindowRect.restype = wt.BOOL
user32.SetWindowPos.argtypes = [HWND, HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wt.UINT]
user32.SetWindowPos.restype = wt.BOOL
user32.MonitorFromWindow.argtypes = [HWND, wt.DWORD]
user32.MonitorFromWindow.restype = wt.HANDLE
user32.GetMonitorInfoW.argtypes = [wt.HANDLE, ctypes.POINTER(_MONITORINFO)]
user32.GetMonitorInfoW.restype = wt.BOOL


def work_area_of(hwnd: int) -> tuple[int, int, int, int]:
    """Work area (excluding the taskbar) of the monitor holding the window, as (l, t, w, h)."""
    mon = user32.MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
    info = _MONITORINFO()
    info.cbSize = ctypes.sizeof(_MONITORINFO)
    user32.GetMonitorInfoW(mon, ctypes.byref(info))
    r = info.rcWork
    return r.left, r.top, r.right - r.left, r.bottom - r.top


def resize_to_client(hwnd: int, client_w: int, client_h: int) -> tuple[int, int] | None:
    """Resize a top-level window so that its client area becomes client_w x client_h.

    The window keeps its top-left corner unless it would leave the monitor's work area.
    Returns the resulting (client_w, client_h), or None if the window could not be measured.
    """
    from geometry import clamp_position  # local import keeps this module importable on its own

    root = root_of(hwnd) or hwnd
    wr = wt.RECT()
    client = client_rect_screen(root)
    if not user32.GetWindowRect(root, ctypes.byref(wr)) or not client:
        return None
    frame_w = (wr.right - wr.left) - client[2]
    frame_h = (wr.bottom - wr.top) - client[3]
    total_w, total_h = client_w + frame_w, client_h + frame_h
    x, y = clamp_position(wr.left, wr.top, total_w, total_h, work_area_of(root))
    user32.SetWindowPos(root, None, x, y, total_w, total_h, SWP_NOZORDER | SWP_NOACTIVATE)
    return client_w, client_h


def window_frame_and_work(hwnd: int) -> tuple[int, int, tuple[int, int, int, int]] | None:
    """(frame_w, frame_h, work_area) for sizing a window; None if it cannot be measured."""
    root = root_of(hwnd) or hwnd
    wr = wt.RECT()
    client = client_rect_screen(root)
    if not user32.GetWindowRect(root, ctypes.byref(wr)) or not client:
        return None
    return (wr.right - wr.left) - client[2], (wr.bottom - wr.top) - client[3], work_area_of(root)


# --- low-level mouse hook --------------------------------------------------------------------------

import threading  # noqa: E402
import time  # noqa: E402

kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
kernel32.GetModuleHandleW.argtypes = [wt.LPCWSTR]
kernel32.GetModuleHandleW.restype = wt.HMODULE
kernel32.GetCurrentThreadId.restype = wt.DWORD

WH_MOUSE_LL = 14
WM_MOUSEMOVE, WM_LBUTTONDOWN, WM_LBUTTONUP, WM_QUIT = 0x0200, 0x0201, 0x0202, 0x0012
_LRESULT = ctypes.c_ssize_t
_HOOKPROC = ctypes.WINFUNCTYPE(_LRESULT, ctypes.c_int, wt.WPARAM, wt.LPARAM)


class _MSLLHOOKSTRUCT(ctypes.Structure):
    _fields_ = [("pt", wt.POINT), ("mouseData", wt.DWORD), ("flags", wt.DWORD),
                ("time", wt.DWORD), ("dwExtraInfo", ctypes.c_size_t)]


user32.SetWindowsHookExW.argtypes = [ctypes.c_int, _HOOKPROC, wt.HMODULE, wt.DWORD]
user32.SetWindowsHookExW.restype = wt.HANDLE
user32.CallNextHookEx.argtypes = [wt.HANDLE, ctypes.c_int, wt.WPARAM, wt.LPARAM]
user32.CallNextHookEx.restype = _LRESULT
user32.UnhookWindowsHookEx.argtypes = [wt.HANDLE]
user32.UnhookWindowsHookEx.restype = wt.BOOL
user32.GetMessageW.argtypes = [ctypes.POINTER(wt.MSG), HWND, wt.UINT, wt.UINT]
user32.GetMessageW.restype = ctypes.c_int
user32.PostThreadMessageW.argtypes = [wt.DWORD, wt.UINT, wt.WPARAM, wt.LPARAM]
user32.PostThreadMessageW.restype = wt.BOOL


class MouseHook:
    """Receives every mouse button transition from the OS, so a fast click can never be missed.

    on_button(kind, x, y, ctrl) is called from the hook thread for 'down' / 'up' of the left
    button (physical pixels); keep it tiny (e.g. loop.call_soon_threadsafe). Cursor movement is
    deliberately not tracked here: poll cursor_pos() instead (SetCursorPos-style moves do not
    reach low-level hooks). The hook never swallows events.
    """

    def __init__(self, on_button) -> None:
        self.on_button = on_button
        self._thread: threading.Thread | None = None
        self._thread_id = 0
        self._ready = threading.Event()
        self._hook = None
        self._proc = _HOOKPROC(self._callback)  # keep a reference or it is garbage collected

    def _callback(self, n_code, w_param, l_param):
        try:
            if n_code >= 0:
                info = ctypes.cast(l_param, ctypes.POINTER(_MSLLHOOKSTRUCT)).contents
                x, y = info.pt.x, info.pt.y
                if w_param == WM_LBUTTONDOWN:
                    self.on_button("down", x, y, bool(user32.GetAsyncKeyState(VK_CONTROL) & 0x8000))
                elif w_param == WM_LBUTTONUP:
                    self.on_button("up", x, y, False)
        except Exception:  # an exception must never escape into the OS hook chain
            pass
        return user32.CallNextHookEx(None, n_code, w_param, l_param)

    def _run(self) -> None:
        self._thread_id = kernel32.GetCurrentThreadId()
        self._hook = user32.SetWindowsHookExW(WH_MOUSE_LL, self._proc, kernel32.GetModuleHandleW(None), 0)
        self._ready.set()
        if not self._hook:
            return
        msg = wt.MSG()
        while user32.GetMessageW(ctypes.byref(msg), None, 0, 0) > 0:
            pass  # the hook runs inside GetMessage; no window messages are expected
        user32.UnhookWindowsHookEx(self._hook)

    def start(self) -> None:
        self._thread = threading.Thread(target=self._run, name="mouse-hook", daemon=True)
        self._thread.start()
        if not self._ready.wait(2.0) or not self._hook:
            raise RuntimeError(f"SetWindowsHookEx failed (error {ctypes.get_last_error()})")

    def stop(self) -> None:
        if self._thread_id:
            user32.PostThreadMessageW(self._thread_id, WM_QUIT, 0, 0)
        if self._thread:
            self._thread.join(1.0)


# --- synthetic input (used by the self-test only) ----------------------------------------------

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004
KEYEVENTF_KEYUP = 0x0002


def set_cursor_pos(x: int, y: int) -> None:
    user32.SetCursorPos(int(x), int(y))


def mouse_left(down: bool) -> None:
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN if down else MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)


def key_event(vk: int, down: bool) -> None:
    user32.keybd_event(vk, 0, 0 if down else KEYEVENTF_KEYUP, 0)
