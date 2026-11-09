# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Pure geometry / log-parsing helpers (no Windows or Bluetooth dependencies)."""

from __future__ import annotations

import re

AXIS_MAX = 10000  # touch digitizer range, must match poc/poc2-ble-hid/ble_hid_poc.py

Rect = tuple[float, float, float, float]  # (left, top, width, height)


def video_rect(client_w: float, client_h: float, video_w: float, video_h: float) -> Rect:
    """Where a video of size (video_w x video_h) is drawn inside a client area.

    The video keeps its aspect ratio and is centered (letterboxed), as d3d11videosink does.
    The result is relative to the client area's top-left corner.
    """
    if min(client_w, client_h, video_w, video_h) <= 0:
        return (0.0, 0.0, 0.0, 0.0)
    scale = min(client_w / video_w, client_h / video_h)
    w, h = video_w * scale, video_h * scale
    return ((client_w - w) / 2, (client_h - h) / 2, w, h)


def inside(px: float, py: float, rect: Rect) -> bool:
    left, top, w, h = rect
    return w > 0 and h > 0 and left <= px < left + w and top <= py < top + h


def to_axis(px: float, py: float, rect: Rect) -> tuple[int, int]:
    """Screen point -> 0..AXIS_MAX touch coordinates, clamped to the video rectangle."""
    left, top, w, h = rect
    nx = (px - left) / w * AXIS_MAX
    ny = (py - top) / h * AXIS_MAX
    return (max(0, min(AXIS_MAX, round(nx))), max(0, min(AXIS_MAX, round(ny))))


def to_unit(px: float, py: float, rect: Rect) -> tuple[float, float]:
    """Screen point -> (u, v) in 0..1 inside the picture (u to the right, v downward), clamped."""
    left, top, w, h = rect
    return (max(0.0, min(1.0, (px - left) / w)), max(0.0, min(1.0, (py - top) / h)))


# Picture -> digitizer rotations. The digitizer axes may be tied to the phone's portrait frame, so
# for a landscape picture the point has to be rotated before it is sent. Names match
# ble_hid_poc.orientation_report (what the probe page measures); the value is the quarter turn count.
ROTATIONS = (0, 90, 180, 270)


def rotate_unit(u: float, v: float, degrees: int) -> tuple[float, float]:
    """Picture point (u, v) -> digitizer point (sx, sy), both 0..1.

    0   : (u, v)        portrait, no rotation
    90  : (1-v, u)      page_x ~ +sent_y, page_y ~ -sent_x
    180 : (1-u, 1-v)
    270 : (v, 1-u)      page_x ~ -sent_y, page_y ~ +sent_x
    """
    if degrees == 0:
        return (u, v)
    if degrees == 90:
        return (1 - v, u)
    if degrees == 180:
        return (1 - u, 1 - v)
    if degrees == 270:
        return (v, 1 - u)
    raise ValueError(f"unsupported rotation: {degrees}")


def unit_to_axis(sx: float, sy: float) -> tuple[int, int]:
    return (max(0, min(AXIS_MAX, round(sx * AXIS_MAX))), max(0, min(AXIS_MAX, round(sy * AXIS_MAX))))


def next_rotation(degrees: int) -> int:
    """Cycle used by the R key; the two landscape candidates come first."""
    order = (90, 270, 0, 180)
    return order[(order.index(degrees) + 1) % len(order)]


def mirror_point(nx: int, ny: int) -> tuple[int, int]:
    """Second finger of a pinch: the point mirrored through the screen center."""
    return (AXIS_MAX - nx, AXIS_MAX - ny)


def fit_client_size(
    video_w: float, video_h: float, work_w: float, work_h: float,
    frame_w: float = 0, frame_h: float = 0, fraction: float = 0.9,
) -> tuple[int, int]:
    """Client size that shows the video 1:1, scaled down only if it would not fit on the monitor.

    work_w/work_h is the monitor work area, frame_w/frame_h the window chrome around the client
    area. The window may use at most `fraction` of the work area.
    """
    if min(video_w, video_h) <= 0:
        raise ValueError("video size must be positive")
    max_w = max(1.0, work_w * fraction - frame_w)
    max_h = max(1.0, work_h * fraction - frame_h)
    scale = min(1.0, max_w / video_w, max_h / video_h)
    return max(1, round(video_w * scale)), max(1, round(video_h * scale))


def clamp_position(x: int, y: int, w: int, h: int, work: tuple[int, int, int, int]) -> tuple[int, int]:
    """Move a window (x, y, w, h) the least amount needed to lie inside the work area (l, t, w, h)."""
    wl, wt, ww, wh = work
    x = min(x, wl + ww - w)
    y = min(y, wt + wh - h)
    return max(x, wl), max(y, wt)


# --- UxPlay log parsing --------------------------------------------------------------------

_RE_SIZE = re.compile(
    r"raop_rtp_mirror width_source = ([\d.]+) height_source = ([\d.]+) "
    r"x_offset = ([\d.]+) y_offset = ([\d.]+) width = ([\d.]+) height = ([\d.]+)"
)
_RE_DEVICE = re.compile(r"connection request from (.+?) \((.+?)\) with deviceID")
# Printed by the patched uxplay.exe (poc/poc1-uxplay/patches): the real frame size the video sink draws.
_RE_SINK = re.compile(r"iScrMirror: sink-caps (\d+)x(\d+) par (\d+)/(\d+)")


def parse_uxplay_line(line: str) -> dict | None:
    """Extract facts we care about from one line of `uxplay -d` output.

    "sink_size" (patched uxplay) is the ground truth of what is drawn and follows rotation.
    "video_size" comes from the stock debug line and is only a fallback.
    """
    m = _RE_SINK.search(line)
    if m:
        w, h, pn, pd = (int(g) for g in m.groups())
        if pn > 0 and pd > 0:
            return {"sink_size": (w * pn / pd, float(h))}  # pixel aspect ratio folded into the width
    m = _RE_SIZE.search(line)
    if m:
        return {"video_size": (float(m.group(5)), float(m.group(6)))}
    m = _RE_DEVICE.search(line)
    if m:
        return {"device": m.group(1), "model": m.group(2)}
    return None
