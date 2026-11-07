"""PoC 3: mirror an iPhone with UxPlay and control it by clicking the video window.

  video : iPhone --AirPlay--> UxPlay (d3d11videosink window, resized to follow rotation)
  touch : mouse over that window --> 0..10000 coordinates --> BLE HID digitizer --> iPhone

Controls (while the UxPlay window is under the cursor):
  left drag         one-finger touch / swipe
  Ctrl + left drag  two-finger pinch (second finger mirrored through the screen center)
  H                 home gesture (swipe up from the bottom edge), UxPlay window must be focused
"""

from __future__ import annotations

import argparse
import asyncio
import collections
import os
import subprocess
import sys
import threading
import time
from pathlib import Path
from typing import Callable

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "poc2-ble-hid"))  # reuse the PoC 2 HID peripheral

import winapi  # noqa: E402
from geometry import (  # noqa: E402
    fit_client_size,
    inside,
    mirror_point,
    next_rotation,
    parse_uxplay_line,
    rotate_unit,
    to_unit,
    unit_to_axis,
    video_rect,
)

TICK_SECONDS = 0.004
MOVE_INTERVAL = 0.016  # at most ~60 move frames per second; BLE cannot carry more and a backlog adds lag
MIN_HOLD = 0.070  # a finger stays down at least this long; shorter taps are unreliable on iOS
MIN_GAP = 0.025  # pause between lifting a finger and the next touch
FALLBACK_VIDEO_SIZE = (393.0, 852.0)  # iPhone portrait aspect, used until UxPlay reports the real size


# ---------------------------------------------------------------------------
# UxPlay process
# ---------------------------------------------------------------------------


def default_uxplay_exe() -> Path:
    base = HERE.parent / "poc1-uxplay/UxPlay"
    patched = base / "build-isr/uxplay.exe"  # built with poc/poc1-uxplay/patches (prints the sink size)
    return patched if patched.exists() else base / "build/uxplay.exe"


class UxPlayProcess:
    """Runs the locally built uxplay.exe and tracks what it prints."""

    def __init__(self, exe: Path, name: str, sink: str, extra: list[str], log_path: Path | None,
                 verbose: bool = False) -> None:
        self.exe, self.name, self.sink, self.extra = exe, name, sink, extra
        self.log_path = log_path
        self.verbose = verbose
        self.codec_packets = 0  # how many times the phone (re)announced the stream geometry
        self._log = None
        self._log_lock = threading.Lock()
        self.proc: subprocess.Popen | None = None
        self.sink_size: tuple[float, float] | None = None  # ground truth (patched uxplay only)
        self.geom_size: tuple[float, float] | None = None  # fallback from the stock debug line
        self.device: str | None = None
        self.model: str | None = None
        self.tail: collections.deque[str] = collections.deque(maxlen=60)

    @property
    def video_size(self) -> tuple[float, float] | None:
        return self.sink_size or self.geom_size

    @property
    def size_source(self) -> str:
        return "sink" if self.sink_size else ("log" if self.geom_size else "-")

    @property
    def pid(self) -> int | None:
        return self.proc.pid if self.proc else None

    def _write_log(self, text: str) -> None:
        """Append a timestamped line to the log file (if any); safe to call from any thread."""
        if not self._log:
            return
        now = time.time()
        stamp = time.strftime("%H:%M:%S", time.localtime(now)) + f".{int(now * 1000) % 1000:03d}"
        with self._log_lock:
            if self._log:
                self._log.write(f"{stamp} {text}\n")
                self._log.flush()

    def mark(self, text: str) -> None:
        """Put a labelled marker into the log, e.g. right after rotating the phone."""
        self._write_log(f"### MARK {text}")

    def start(self) -> None:
        if self.log_path:
            self.log_path.parent.mkdir(parents=True, exist_ok=True)
            self._log = open(self.log_path, "w", encoding="utf-8")
        msys = Path.home() / "scoop/apps/msys2/current/ucrt64/bin"
        env = dict(os.environ, PATH=f"{msys};{os.environ.get('PATH', '')}")
        # -d is required: the stock picture size is only printed in debug output.
        args = [str(self.exe), "-n", self.name, "-nh", "-vs", self.sink, "-vsync", "no", "-d", *self.extra]
        self.proc = subprocess.Popen(
            args,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            env=env,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        threading.Thread(target=self._reader, daemon=True).start()

    def _reader(self) -> None:
        assert self.proc and self.proc.stdout
        try:
            for raw in self.proc.stdout:  # must be drained, otherwise uxplay blocks on a full pipe
                line = raw.decode("utf-8", errors="replace").rstrip()
                self.tail.append(line)
                self._write_log(line)
                info = parse_uxplay_line(line)
                if info:
                    # Experiment: does a 180-degree flip between the two landscapes make the phone send
                    # a new codec packet (even with an unchanged size)? If so the flip could be detected.
                    if "video_size" in info:
                        self.codec_packets += 1
                        if self.verbose:
                            print(f"[video] codec packet #{self.codec_packets}: "
                                  f"{info['video_size'][0]:.0f}x{info['video_size'][1]:.0f}")
                    if "sink_size" in info and self.verbose and info["sink_size"] != self.sink_size:
                        print(f"[video] sink size {info['sink_size'][0]:.0f}x{info['sink_size'][1]:.0f}")
                    self.sink_size = info.get("sink_size", self.sink_size)
                    self.geom_size = info.get("video_size", self.geom_size)
                    self.device = info.get("device", self.device)
                    self.model = info.get("model", self.model)
        finally:
            with self._log_lock:
                if self._log:
                    self._log.close()
                    self._log = None

    def alive(self) -> bool:
        return self.proc is not None and self.proc.poll() is None

    def stop(self) -> None:
        if self.alive():
            self.proc.terminate()
            try:
                self.proc.wait(3)
            except subprocess.TimeoutExpired:
                self.proc.kill()

    def find_window(self) -> int | None:
        return winapi.largest_visible_window_of_pid(self.proc.pid) if self.alive() else None


# ---------------------------------------------------------------------------
# HID back ends
# ---------------------------------------------------------------------------


class DryRunHid:
    """Same interface as HidPeripheral, but only records frames (no Bluetooth).

    send_delay simulates a slow BLE link (each frame blocks the caller for that long).
    """

    def __init__(self, send_delay: float = 0.0) -> None:
        self.frames: list[tuple[str, tuple]] = []
        self.times: list[float] = []
        self.send_delay = send_delay

    def subscribed(self, _report_id: int) -> int:
        return 1

    async def _rec(self, kind: str, data: tuple) -> None:
        self.frames.append((kind, data))
        self.times.append(time.monotonic())
        if self.send_delay:
            await asyncio.sleep(self.send_delay)

    async def touch_down(self, x: int, y: int, cid: int = 0) -> None:
        await self._rec("down", ((cid, x, y),))

    async def _touch(self, contacts: list[tuple[int, int, int]]) -> None:
        await self._rec("down", tuple(contacts))

    async def touch_up(self) -> None:
        await self._rec("up", ())

    async def swipe(self, x1: int, y1: int, x2: int, y2: int, ms: int = 300, steps: int = 20) -> None:
        await self._rec("swipe", ((x1, y1), (x2, y2)))


async def start_real_hid(loop: asyncio.AbstractEventLoop):
    import ble_hid_poc as hidmod
    from winrt.windows.devices.bluetooth import BluetoothAdapter

    adapter = await BluetoothAdapter.get_default_async()
    if adapter is None or not adapter.is_peripheral_role_supported:
        raise RuntimeError("no Bluetooth LE adapter with peripheral role support")
    hid = hidmod.HidPeripheral(loop)
    await hid.build()
    await hid.start()
    await asyncio.sleep(0.5)
    states = [s for _, s in hid.status()]
    if not all(s == "2" or s.upper().endswith("STARTED") for s in states):
        raise RuntimeError(
            f"GATT services are not advertising (status {states}). "
            "Another instance (poc/poc2-ble-hid or an older poc3 run) may still be running; quit it first."
        )
    return hid


# ---------------------------------------------------------------------------
# Mouse -> touch bridge
# ---------------------------------------------------------------------------


class TouchBridge:
    """Turns mouse drags over the mirror window into HID touch frames.

    Button transitions arrive as events (low-level hook), so a quick click is never missed even
    while a BLE send is in progress. Cursor movement is sampled with GetCursorPos.
    """

    def __init__(
        self,
        hid,
        find_window: Callable[[], int | None],
        get_video_size: Callable[[], tuple[float, float] | None],
        events: asyncio.Queue,
        verbose: bool = False,
        follow: bool = True,
    ) -> None:
        self.hid = hid
        self.find_window = find_window
        self.get_video_size = get_video_size
        self.events = events
        self.verbose = verbose
        self.follow = follow
        self.window: int | None = None
        self.rect_screen: tuple[float, float, float, float] | None = None  # video rect in screen px
        # The digitizer axes seem to stay tied to the phone's portrait frame, so a landscape picture
        # must be rotated before sending. Which landscape it is cannot be read from the video, so it
        # is a setting (R key / `rot` command); the default is a guess to be confirmed by `probe`.
        self.landscape_rot = 90
        # state
        self.rot = 0  # rotation used by the gesture in progress
        self.down = False
        self.pinch = False
        self.last: tuple[int, int] | None = None
        self.down_t = self.last_up_t = self.last_move_t = 0.0
        self._followed: tuple[float, float] | None = None
        self._prev_h = self._prev_r = False
        self.paused = False  # set while `probe` sends its own touches
        self.on_mark: Callable[[str], None] | None = None  # writes labelled markers into the uxplay log
        # statistics
        self.gestures = 0
        self.dropped = 0
        self.max_send_ms = 0.0
        self.last_hold_ms = 0.0

    # -- geometry -----------------------------------------------------------------------------

    def _maybe_follow(self, hwnd: int) -> None:
        """Resize the window to the new picture size after a rotation (once per size change)."""
        size = self.get_video_size()
        if not self.follow or not size or size == self._followed:
            return
        info = winapi.window_frame_and_work(hwnd)
        if not info:
            return
        frame_w, frame_h, work = info
        cw, ch = fit_client_size(size[0], size[1], work[2], work[3], frame_w, frame_h)
        winapi.resize_to_client(hwnd, cw, ch)
        self._followed = size
        print(f"[window] picture is now {size[0]:.0f}x{size[1]:.0f}; window client resized to {cw}x{ch}")

    def current_rot(self) -> int:
        """Rotation to apply now: none for a portrait picture, the chosen landscape otherwise."""
        w, h = self.get_video_size() or FALLBACK_VIDEO_SIZE
        return self.landscape_rot if w > h else 0

    def set_landscape_rotation(self, degrees: int) -> None:
        self.landscape_rot = degrees
        print(f"[rot ] landscape rotation = {degrees} deg")
        if self.on_mark:
            self.on_mark(f"bridge landscape_rot={degrees}")

    def _axis(self, x: int, y: int, rect, rot: int) -> tuple[int, int]:
        """Cursor position -> digitizer coordinates, rotated for the current orientation."""
        u, v = to_unit(x, y, rect)
        return unit_to_axis(*rotate_unit(u, v, rot))

    def _geometry(self, hwnd: int):
        client = winapi.client_rect_screen(hwnd)
        if not client:
            return None
        left, top, w, h = client
        vw, vh = self.get_video_size() or FALLBACK_VIDEO_SIZE
        vl, vt, vwid, vhgt = video_rect(w, h, vw, vh)
        return (left + vl, top + vt, vwid, vhgt)

    # -- HID output ---------------------------------------------------------------------------

    async def _send(self, nx: int, ny: int) -> None:
        t0 = time.perf_counter()
        if self.pinch:
            mx, my = mirror_point(nx, ny)
            await self.hid._touch([(0, nx, ny), (1, mx, my)])
        else:
            await self.hid.touch_down(nx, ny)
        self.last = (nx, ny)
        self.last_move_t = time.monotonic()
        self.max_send_ms = max(self.max_send_ms, (time.perf_counter() - t0) * 1000)

    async def _lift(self) -> None:
        t0 = time.perf_counter()
        await self.hid.touch_up()
        self.max_send_ms = max(self.max_send_ms, (time.perf_counter() - t0) * 1000)
        self.down = False
        self.last = None
        self.last_up_t = time.monotonic()

    # -- event handlers -----------------------------------------------------------------------

    async def _on_down(self, hwnd: int, rect, x: int, y: int, ctrl: bool) -> None:
        if self.down or self.paused:
            return
        over = inside(x, y, rect) and winapi.root_window_at(x, y) == winapi.root_of(hwnd)
        if not over:
            return
        if self.hid.subscribed(2) == 0:
            self.dropped += 1
            return
        gap = MIN_GAP - (time.monotonic() - self.last_up_t)
        if gap > 0:
            await asyncio.sleep(gap)
        self.down, self.pinch = True, ctrl
        self.rot = self.current_rot()  # fixed for the whole gesture
        self.down_t = time.monotonic()
        self.gestures += 1
        nx, ny = self._axis(x, y, rect, self.rot)
        if self.verbose:
            print(f"[touch] down  ({nx:5d},{ny:5d}) pinch={ctrl} rot={self.rot}")
        await self._send(nx, ny)

    async def _on_up(self, rect, x: int, y: int) -> None:
        if not self.down:
            return
        nx, ny = self._axis(x, y, rect, self.rot)
        if (nx, ny) != self.last:  # deliver the final position before lifting
            await self._send(nx, ny)
        hold = MIN_HOLD - (time.monotonic() - self.down_t)
        if hold > 0:
            await asyncio.sleep(hold)
        self.last_hold_ms = (time.monotonic() - self.down_t) * 1000
        await self._lift()
        if self.verbose:
            print(f"[touch] up    held {self.last_hold_ms:.0f} ms")

    async def _on_tick(self, hwnd: int, rect) -> None:
        if self.down:
            x, y = winapi.cursor_pos()
            nx, ny = self._axis(x, y, rect, self.rot)
            if (nx, ny) != self.last and time.monotonic() - self.last_move_t >= MOVE_INTERVAL:
                await self._send(nx, ny)
        # Keys only count while the mirror window has the keyboard focus.
        focused = winapi.root_of(winapi.foreground_window() or 0) == winapi.root_of(hwnd)
        h = winapi.key_down(winapi.VK_H) and focused
        if h and not self._prev_h and not self.down and self.hid.subscribed(2) > 0:
            # Home = swipe up from the bottom edge of the PICTURE, rotated like any other touch.
            rot = self.current_rot()
            start = unit_to_axis(*rotate_unit(0.5, 0.995, rot))
            end = unit_to_axis(*rotate_unit(0.5, 0.65, rot))
            if self.verbose:
                print(f"[key ] H -> home swipe {start} -> {end} rot={rot}")
            await self.hid.swipe(*start, *end, ms=220)
        self._prev_h = h
        r = winapi.key_down(winapi.VK_R) and focused
        if r and not self._prev_r:
            self.set_landscape_rotation(next_rotation(self.landscape_rot))
        self._prev_r = r

    # -- main loop ----------------------------------------------------------------------------

    async def run(self) -> None:
        while True:
            await asyncio.sleep(TICK_SECONDS)
            hwnd = self.find_window()
            self.window = hwnd
            if hwnd is None:
                self.rect_screen = None
                if self.down:
                    await self._lift()
                while not self.events.empty():  # nothing to click on
                    self.events.get_nowait()
                continue
            self._maybe_follow(hwnd)
            rect = self._geometry(hwnd)
            self.rect_screen = rect
            if rect is None:
                continue
            while not self.events.empty():
                kind, x, y, ctrl, _t = self.events.get_nowait()
                if kind == "down":
                    await self._on_down(hwnd, rect, x, y, ctrl)
                else:
                    await self._on_up(rect, x, y)
            await self._on_tick(hwnd, rect)


# ---------------------------------------------------------------------------
# Status + main
# ---------------------------------------------------------------------------


CONSOLE_HELP = """console commands:
  rot [0|90|180|270]   show / set the rotation used while the picture is landscape (same as the R key)
  probe                measure how touches land on a Safari page (see README); do not touch the mouse meanwhile
  mark <label>         write a labelled marker into the uxplay log (use with --collect, see README)
  help | quit"""


def start_console(loop: asyncio.AbstractEventLoop, lines: asyncio.Queue) -> None:
    """Read stdin on a daemon thread so that Ctrl+C / exit never waits for a keypress."""

    def reader() -> None:
        if sys.stdin is None:  # e.g. started without a console
            return
        try:
            for line in sys.stdin:
                loop.call_soon_threadsafe(lines.put_nowait, line.strip())
        except (OSError, ValueError, RuntimeError):  # stdin closed or loop already gone
            pass

    threading.Thread(target=reader, name="console", daemon=True).start()


async def console_loop(bridge: TouchBridge, hid, lines: asyncio.Queue, dry_run: bool, probe_port: int,
                       stop: asyncio.Event, ux: UxPlayProcess | None = None) -> None:
    web = None
    while True:
        parts = (await lines.get()).split()
        if not parts:
            continue
        cmd = parts[0].lower()
        if cmd in ("quit", "exit"):
            stop.set()
            return
        if cmd == "help":
            print(CONSOLE_HELP)
        elif cmd == "mark":
            label = " ".join(parts[1:]) or "mark"
            if ux and ux._log:
                ux.mark(label)
                print(f"[mark] {label}")
            else:
                print("no uxplay log is being written; start with --collect (or --uxplay-log FILE)")
        elif cmd == "rot":
            from geometry import ROTATIONS
            if len(parts) == 2 and parts[1].isdigit() and int(parts[1]) in ROTATIONS:
                bridge.set_landscape_rotation(int(parts[1]))
            else:
                print(f"[rot ] landscape rotation = {bridge.landscape_rot} deg (choices: {ROTATIONS})")
        elif cmd == "probe":
            if dry_run:
                print("probe needs the real BLE link; run without --dry-run")
                continue
            from probe_server import ProbeServer
            import ble_hid_poc as hidmod
            if web is None:
                web = ProbeServer(probe_port)
                try:
                    web.start()
                except OSError as exc:
                    print("cannot start the probe page:", exc)
                    web = None
                    continue
            if not web.snapshot():
                for ip in ProbeServer.lan_ips():
                    print(f"[probe] open http://{ip}:{probe_port}/ in iPhone Safari (current orientation), then type `probe` again")
                continue
            bridge.paused = True
            try:
                await hidmod.run_probe(hid, web)
            finally:
                bridge.paused = False
        else:
            print("unknown command; type `help`")


async def status_loop(bridge: TouchBridge, ux: UxPlayProcess | None, hid) -> None:
    last = ""
    while True:
        await asyncio.sleep(1.0)
        parts = []
        if ux:
            parts.append(f"uxplay={'up' if ux.alive() else 'DOWN'}")
            parts.append(f"iphone={ux.model or '-'}")
            size = "x".join(f"{v:.0f}" for v in ux.video_size) if ux.video_size else "-"
            parts.append(f"video={size}({ux.size_source})")
            if ux.video_size and ux.video_size[0] > ux.video_size[1]:
                parts.append(f"landscape_rot={bridge.landscape_rot}")
        parts.append(f"window={'yes' if bridge.window else 'no'}")
        parts.append(f"hid_subscribed={hid.subscribed(2)}")
        key = "  ".join(parts)
        if key != last:  # print only when something other than the counters changed
            print("[status]", key)
            last = key


async def amain(ns: argparse.Namespace) -> int:
    winapi.enable_dpi_awareness()
    winapi.set_timer_resolution(1)
    loop = asyncio.get_running_loop()

    hid = DryRunHid() if ns.dry_run else await start_real_hid(loop)
    print("HID:", "dry-run (nothing is sent)" if ns.dry_run else "advertising as a BLE HID peripheral")

    ux: UxPlayProcess | None = None
    if ns.no_uxplay:
        print("UxPlay not started; looking for a window of an already running uxplay.exe")
        find_window = lambda: winapi.largest_visible_window_of_pid(_find_pid("uxplay.exe"))  # noqa: E731
        get_size = lambda: None  # noqa: E731
    else:
        exe = Path(ns.uxplay_exe) if ns.uxplay_exe else default_uxplay_exe()
        if not exe.exists():
            print(f"ERROR: {exe} not found (build UxPlay first, see poc/poc1-uxplay/README.md)", file=sys.stderr)
            return 2
        ux = UxPlayProcess(exe, ns.name, ns.sink, ns.uxplay_arg, Path(ns.uxplay_log) if ns.uxplay_log else None,
                           verbose=ns.verbose)
        ux.start()
        print(f"UxPlay started (pid {ux.pid}, {exe.parent.name}). "
              f"On the iPhone: Control Center > Screen Mirroring > {ns.name}")
        find_window, get_size = ux.find_window, (lambda: ux.video_size)

    events: asyncio.Queue = asyncio.Queue()
    hook = winapi.MouseHook(
        lambda kind, x, y, ctrl: loop.call_soon_threadsafe(events.put_nowait, (kind, x, y, ctrl, time.monotonic()))
    )
    hook.start()

    bridge = TouchBridge(hid, find_window, get_size, events, verbose=ns.verbose, follow=not ns.no_follow)
    bridge.landscape_rot = ns.landscape_rot
    if ux:
        bridge.on_mark = ux.mark
    print("Zoom must be ON on the iPhone (Settings > Accessibility > Zoom).")
    print("Left drag = touch, Ctrl + left drag = pinch, H = home, R = cycle landscape rotation.")
    print("Type `help` here for console commands. Ctrl+C to quit.\n")

    stop = asyncio.Event()
    lines: asyncio.Queue = asyncio.Queue()
    start_console(loop, lines)
    tasks = [
        asyncio.create_task(bridge.run()),
        asyncio.create_task(status_loop(bridge, ux, hid)),
        asyncio.create_task(console_loop(bridge, hid, lines, ns.dry_run, ns.probe_port, stop, ux)),
    ]
    waiter = asyncio.create_task(stop.wait())
    try:
        done, _ = await asyncio.wait({waiter, *tasks}, timeout=ns.duration, return_when=asyncio.FIRST_COMPLETED)
        for t in done:
            if t is not waiter and not t.cancelled() and t.exception():
                raise t.exception()  # surface a crash in a worker instead of hanging silently
    finally:
        for t in (waiter, *tasks):
            t.cancel()
        hook.stop()
        if ux:
            ux.stop()
        winapi.reset_timer_resolution(1)
        print(f"[summary] gestures={bridge.gestures} dropped={bridge.dropped} "
              f"max_send={bridge.max_send_ms:.0f}ms")
    return 0


def _find_pid(image: str) -> int:
    out = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {image}", "/FO", "CSV", "/NH"],
                         capture_output=True, text=True).stdout
    for row in out.splitlines():
        cols = [c.strip('"') for c in row.split('","')]
        if len(cols) > 1 and cols[0].strip('"').lower() == image.lower():
            return int(cols[1])
    return -1


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--name", default="iScrMirror", help="AirPlay name shown on the iPhone")
    ap.add_argument("--sink", default="d3d11videosink", help="GStreamer video sink for UxPlay")
    ap.add_argument("--uxplay-exe", help="path to uxplay.exe (default: the patched build-isr, else build)")
    ap.add_argument("--uxplay-arg", action="append", default=[], help="extra uxplay argument (repeatable)")
    ap.add_argument("--uxplay-log", help="write the full uxplay output to this file")
    ap.add_argument("--no-uxplay", action="store_true", help="attach to an already running uxplay.exe")
    ap.add_argument("--no-follow", action="store_true", help="do not resize the window when the picture size changes")
    ap.add_argument("--landscape-rot", type=int, default=90, choices=(0, 90, 180, 270),
                    help="initial rotation for a landscape picture (change at runtime with R / `rot`)")
    ap.add_argument("--probe-port", type=int, default=8000, help="port of the `probe` page (default 8000)")
    ap.add_argument("--dry-run", action="store_true", help="do not use Bluetooth; just print what would be sent")
    ap.add_argument("--verbose", "-v", action="store_true", help="print every touch event")
    ap.add_argument("--duration", type=float, help="exit after this many seconds (for tests)")
    ap.add_argument("--collect", action="store_true",
                    help="record a timestamped uxplay log with the phone's performance reports (-FPSdata) under logs/, "
                         "for studying what changes when the phone rotates (use `mark <label>` while testing)")
    ns = ap.parse_args()
    if ns.collect:
        ns.uxplay_arg = [*ns.uxplay_arg, "-FPSdata"]
        if not ns.uxplay_log:
            ns.uxplay_log = str(HERE / "logs" / f"uxplay-{time.strftime('%Y%m%d-%H%M%S')}.log")
        print(f"collecting the uxplay log in {ns.uxplay_log}")
    try:
        sys.exit(asyncio.run(amain(ns)))
    except KeyboardInterrupt:
        print("bye")


if __name__ == "__main__":
    main()
