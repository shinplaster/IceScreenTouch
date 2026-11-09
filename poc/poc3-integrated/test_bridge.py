# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""End-to-end check of TouchBridge using real Win32 input against a stand-in window.

A tkinter window plays the role of the UxPlay window. Synthetic mouse input is injected with
SetCursorPos/mouse_event and travels through the real low-level mouse hook, so the real cursor
moves for a few seconds. HID output is recorded by DryRunHid (no Bluetooth). Expected
coordinates are computed from tkinter's own geometry, an independent source from the Win32
calls the bridge uses.
"""

from __future__ import annotations

import asyncio
import time
import tkinter as tk

import winapi
from geometry import AXIS_MAX
from mirror_touch import MIN_HOLD, MOVE_INTERVAL, DryRunHid, TouchBridge

VIDEO = (498.0, 1080.0)
CLIENT_W, CLIENT_H = 400, 600  # letterboxed: the video is narrower than the window


def expected_axis(px: int, py: int, root: tk.Tk, video=VIDEO) -> tuple[int, int]:
    """Axis value of a screen pixel using tkinter's geometry only."""
    cx, cy = root.winfo_rootx(), root.winfo_rooty()
    cw, ch = root.winfo_width(), root.winfo_height()
    scale = min(cw / video[0], ch / video[1])
    vw, vh = video[0] * scale, video[1] * scale
    vl, vt = cx + (cw - vw) / 2, cy + (ch - vh) / 2
    clamp = lambda v: max(0, min(AXIS_MAX, round(v)))  # noqa: E731
    return clamp((px - vl) / vw * AXIS_MAX), clamp((py - vt) / vh * AXIS_MAX)


async def main() -> int:
    winapi.enable_dpi_awareness()
    winapi.set_timer_resolution(1)
    loop = asyncio.get_running_loop()
    root = tk.Tk()
    root.geometry(f"{CLIENT_W}x{CLIENT_H}+300+150")
    root.attributes("-topmost", True)
    root.configure(bg="#223")
    root.update()
    hwnd = winapi.root_of(root.winfo_id())

    current_queue: dict[str, asyncio.Queue] = {"q": asyncio.Queue()}
    hook = winapi.MouseHook(
        lambda kind, x, y, ctrl: loop.call_soon_threadsafe(
            current_queue["q"].put_nowait, (kind, x, y, ctrl, time.monotonic())
        )
    )
    hook.start()

    video_size = {"v": VIDEO}
    task: asyncio.Task | None = None
    saved = winapi.cursor_pos()
    failures: list[str] = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print(("PASS " if ok else "FAIL ") + name + (f"  [{detail}]" if detail else ""))
        if not ok:
            failures.append(name)

    async def pump(seconds: float) -> None:
        end = loop.time() + seconds
        while loop.time() < end:
            root.update()
            await asyncio.sleep(0.005)

    def start_bridge(hid: DryRunHid, follow: bool = False) -> TouchBridge:
        nonlocal task
        if task:
            task.cancel()
        current_queue["q"] = asyncio.Queue()
        bridge = TouchBridge(hid, lambda: hwnd, lambda: video_size["v"], current_queue["q"], follow=follow)
        task = asyncio.create_task(bridge.run())
        return bridge

    async def drag(path, ctrl: bool = False, hold: float = 0.04, step: float = 0.03) -> None:
        winapi.set_cursor_pos(*path[0])
        await pump(0.05)
        if ctrl:
            winapi.key_event(winapi.VK_CONTROL, True)
            await pump(0.03)
        winapi.mouse_left(True)
        await pump(hold)
        for p in path[1:]:
            winapi.set_cursor_pos(*p)
            await pump(step)
        winapi.mouse_left(False)
        await pump(0.25)  # lets the bridge finish its minimum hold + lift
        if ctrl:
            winapi.key_event(winapi.VK_CONTROL, False)
            await pump(0.03)

    try:
        hid = DryRunHid()
        bridge = start_bridge(hid)
        await pump(0.3)
        rect = bridge.rect_screen
        check("bridge sees the window and computes a video rect", rect is not None, str(rect))
        if rect is None:
            return 1
        l, t, w, h = rect
        cx, cy = round(l + w / 2), round(t + h / 2)

        # 1. tap in the middle of the picture
        hid.frames.clear()
        await drag([(cx, cy)])
        kinds = [k for k, _ in hid.frames]
        ex = expected_axis(cx, cy, root)
        got = hid.frames[0][1][0][1:] if hid.frames else None
        check("tap: down...up sequence", kinds == ["down", "up"], str(kinds))
        check("tap: center maps to ~(5000,5000)", got is not None and abs(got[0] - 5000) < 60 and abs(got[1] - 5000) < 60, str(got))
        check("tap: matches independent tkinter geometry", got is not None and abs(got[0] - ex[0]) <= 2 and abs(got[1] - ex[1]) <= 2, f"got={got} expected={ex}")

        # 2. drag from 25% to 75% of the picture
        hid.frames.clear()
        p1 = (round(l + w * 0.25), round(t + h * 0.25))
        p2 = (round(l + w * 0.75), round(t + h * 0.75))
        path = [(round(p1[0] + (p2[0] - p1[0]) * i / 8), round(p1[1] + (p2[1] - p1[1]) * i / 8)) for i in range(9)]
        await drag(path)
        downs = [f[1][0][1:] for f in hid.frames if f[0] == "down"]
        mono = all(b[0] >= a[0] and b[1] >= a[1] for a, b in zip(downs, downs[1:]))
        check("drag: many frames, monotonic, ends with up", len(downs) >= 6 and mono and hid.frames[-1][0] == "up", f"{len(downs)} frames")
        check("drag: start ~(2500,2500) end ~(7500,7500)", abs(downs[0][0] - 2500) < 60 and abs(downs[-1][0] - 7500) < 60 and abs(downs[0][1] - 2500) < 60 and abs(downs[-1][1] - 7500) < 60, f"{downs[0]} -> {downs[-1]}")

        # 3. click in the black letterbox bar (inside the window, outside the picture)
        hid.frames.clear()
        bar_x = round(root.winfo_rootx() + 5)
        await drag([(bar_x, cy)])
        check("click in letterbox bar is ignored", len(hid.frames) == 0, str(hid.frames))

        # 4. press outside the picture, drag in: must not start a touch
        hid.frames.clear()
        await drag([(bar_x, cy), (cx, cy)])
        check("drag entering from outside is ignored", len(hid.frames) == 0, str(hid.frames))

        # 5. drag out of the picture: clamps to the edge, still ends with up
        hid.frames.clear()
        await drag([(cx, cy), (round(l + w + 60), cy)])
        downs = [f[1][0][1:] for f in hid.frames if f[0] == "down"]
        check("drag leaving the picture clamps to the edge", bool(downs) and downs[-1][0] == AXIS_MAX and hid.frames[-1][0] == "up", str(downs[-1:]))

        # 6. Ctrl + drag = pinch with a mirrored second finger
        hid.frames.clear()
        await drag([(round(l + w * 0.6), cy), (round(l + w * 0.8), cy)], ctrl=True)
        pinch = [f[1] for f in hid.frames if f[0] == "down"]
        two = all(len(c) == 2 and c[0][1] + c[1][1] == AXIS_MAX and c[0][2] + c[1][2] == AXIS_MAX for c in pinch)
        check("ctrl+drag sends two mirrored contacts", bool(pinch) and two, str(pinch[:2]))

        # 7. keyboard: H = home swipe (needs keyboard focus on the window)
        hid.frames.clear()
        root.focus_force()
        await pump(0.2)
        winapi.key_event(winapi.VK_H, True)
        await pump(0.06)
        winapi.key_event(winapi.VK_H, False)
        await pump(0.1)
        if winapi.root_of(winapi.foreground_window() or 0) == hwnd:
            check("H key sends the home swipe", hid.frames[:1] == [("swipe", ((5000, 9950), (5000, 6500)))], str(hid.frames))
        else:
            print("SKIP H key test: the stand-in window could not take keyboard focus")

        # 8. THE REPORTED SYMPTOM: fast repeated clicks while the BLE link is slow must all arrive.
        slow = DryRunHid(send_delay=0.06)
        start_bridge(slow)
        await pump(0.2)
        winapi.set_cursor_pos(cx, cy)
        await pump(0.05)
        n = 8
        for _ in range(n):
            winapi.mouse_left(True)
            await pump(0.012)  # a very short 12 ms click
            winapi.mouse_left(False)
            await pump(0.025)
        await pump(1.5)  # let the backlog drain
        kinds = [k for k, _ in slow.frames]
        check(f"{n} fast clicks on a slow link: none lost, strictly alternating", kinds == ["down", "up"] * n, f"{kinds.count('down')} down / {kinds.count('up')} up")
        pairs = [slow.times[i + 1] - slow.times[i] for i in range(0, len(slow.times) - 1, 2)]
        check(f"every tap is held at least ~{MIN_HOLD * 1000:.0f} ms", bool(pairs) and min(pairs) >= MIN_HOLD - 0.006, f"min {min(pairs) * 1000:.0f} ms" if pairs else "no frames")

        # 9. a very fast mouse sweep must not flood the link
        fast = DryRunHid()
        start_bridge(fast)
        await pump(0.2)
        a = (round(l + w * 0.2), cy)
        winapi.set_cursor_pos(*a)
        await pump(0.05)
        winapi.mouse_left(True)
        await pump(0.03)
        t_start = time.monotonic()
        for i in range(1, 61):  # 60 position changes in ~120 ms
            winapi.set_cursor_pos(round(a[0] + (w * 0.6) * i / 60), cy)
            await pump(0.002)
        sweep = time.monotonic() - t_start
        final_x = round(a[0] + w * 0.6)
        winapi.mouse_left(False)
        await pump(0.3)
        n_moves = sum(1 for k, _ in fast.frames if k == "down")
        limit = int(sweep / MOVE_INTERVAL) + 4
        last_down = [f[1][0][1:] for f in fast.frames if f[0] == "down"][-1]
        check("fast sweep is rate-limited", n_moves <= limit, f"{n_moves} frames in {sweep * 1000:.0f} ms (limit {limit})")
        check("fast sweep still ends exactly at the final position", abs(last_down[0] - expected_axis(final_x, cy, root)[0]) <= 2 and fast.frames[-1][0] == "up", str(last_down))

        # 10. window follows a rotation: portrait -> landscape -> portrait
        follow_hid = DryRunHid()
        bridge = start_bridge(follow_hid, follow=True)
        video_size["v"] = (1920.0, 886.0)
        await pump(0.4)
        cw, ch = root.winfo_width(), root.winfo_height()
        check("window resizes to the landscape picture", abs(cw - 1920) <= 1 and abs(ch - 886) <= 1, f"{cw}x{ch}")
        video_size["v"] = VIDEO
        await pump(0.4)
        cw, ch = root.winfo_width(), root.winfo_height()
        check("window returns to the portrait picture", abs(cw - 498) <= 1 and abs(ch - 1080) <= 1, f"{cw}x{ch}")
        # after following, the picture fills the window, so the mapping must be exact
        r = bridge.rect_screen
        check("after following there are no letterbox bars", r is not None and abs(r[2] - cw) <= 1 and abs(r[3] - ch) <= 1, str(r))
        video_size["v"] = (1920.0, 886.0)  # landscape again to check the click mapping there
        await pump(0.4)
        r = bridge.rect_screen
        lx, ly = round(r[0] + r[2] * 0.75), round(r[1] + r[3] * 0.25)
        bridge.landscape_rot = 0  # no rotation: plain picture-to-axis mapping
        follow_hid.frames.clear()
        await drag([(lx, ly)])
        got = follow_hid.frames[0][1][0][1:] if follow_hid.frames else None
        ex = expected_axis(lx, ly, root, video=(1920.0, 886.0))
        check("landscape click maps to the right point", got is not None and abs(got[0] - ex[0]) <= 2 and abs(got[1] - ex[1]) <= 2, f"got={got} expected={ex}")

        # 11. landscape rotations: the click is rotated into the phone's portrait frame.
        # Expected values come from the formulas worked out by hand, using the picture point of the pixel
        # as tkinter sees it (independent of the bridge's own geometry).
        ux_, vx_ = (lambda e: (e[0] / AXIS_MAX, e[1] / AXIS_MAX))(ex)  # picture (u, v) of the clicked pixel
        hand = {90: (1 - vx_, ux_), 180: (1 - ux_, 1 - vx_), 270: (vx_, 1 - ux_)}
        for deg, (hx, hy) in hand.items():
            bridge.landscape_rot = deg
            follow_hid.frames.clear()
            await drag([(lx, ly)])
            got = follow_hid.frames[0][1][0][1:] if follow_hid.frames else None
            exp = (round(hx * AXIS_MAX), round(hy * AXIS_MAX))
            check(f"landscape rotation {deg}: click lands at the rotated point", got is not None and abs(got[0] - exp[0]) <= 2 and abs(got[1] - exp[1]) <= 2, f"got={got} expected={exp}")

        # a portrait picture is never rotated, whatever the landscape setting is
        video_size["v"] = VIDEO
        await pump(0.4)
        r = bridge.rect_screen
        px, py = round(r[0] + r[2] * 0.25), round(r[1] + r[3] * 0.75)
        bridge.landscape_rot = 90
        follow_hid.frames.clear()
        await drag([(px, py)])
        got = follow_hid.frames[0][1][0][1:] if follow_hid.frames else None
        exp = expected_axis(px, py, root)
        check("portrait picture ignores the landscape rotation", got is not None and abs(got[0] - exp[0]) <= 2 and abs(got[1] - exp[1]) <= 2, f"got={got} expected={exp}")

        # R cycles the rotation, H swipes from the bottom edge of the rotated picture
        video_size["v"] = (1920.0, 886.0)
        await pump(0.4)
        bridge.landscape_rot = 90
        follow_hid.frames.clear()
        root.focus_force()
        await pump(0.2)
        if winapi.root_of(winapi.foreground_window() or 0) == hwnd:
            winapi.key_event(winapi.VK_R, True)
            await pump(0.06)
            winapi.key_event(winapi.VK_R, False)
            await pump(0.1)
            check("R key cycles the landscape rotation 90 -> 270", bridge.landscape_rot == 270, str(bridge.landscape_rot))
            bridge.landscape_rot = 90
            winapi.key_event(winapi.VK_H, True)
            await pump(0.06)
            winapi.key_event(winapi.VK_H, False)
            await pump(0.1)
            check("H in landscape (rotation 90) swipes up along the rotated bottom edge",
                  follow_hid.frames[:1] == [("swipe", ((50, 5000), (3500, 5000)))], str(follow_hid.frames[:1]))
        else:
            print("SKIP R / H landscape tests: the stand-in window could not take keyboard focus")
    finally:
        winapi.mouse_left(False)
        winapi.key_event(winapi.VK_CONTROL, False)
        winapi.set_cursor_pos(*saved)
        if task:
            task.cancel()
        hook.stop()
        winapi.reset_timer_resolution(1)
        root.destroy()

    print("\nRESULT:", "ALL PASSED" if not failures else f"{len(failures)} FAILED: {failures}")
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
