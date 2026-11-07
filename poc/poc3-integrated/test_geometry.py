"""Offline checks for geometry.py. Run: ..\\poc2-ble-hid\\.venv\\Scripts\\python.exe test_geometry.py"""

from geometry import (
    AXIS_MAX,
    clamp_position,
    fit_client_size,
    inside,
    mirror_point,
    parse_uxplay_line,
    to_axis,
    video_rect,
)


def approx(a, b, tol=0.51):
    return abs(a - b) <= tol


# --- letterbox math --------------------------------------------------------------------------
# 498x1080 picture in a 400x600 client area: height-limited, centered horizontally.
l, t, w, h = video_rect(400, 600, 498, 1080)
assert approx(h, 600) and approx(w, 498 * 600 / 1080) and approx(t, 0) and approx(l, (400 - w) / 2)

# wide window: width-limited would overflow, so height still limits
l, t, w, h = video_rect(1920, 1080, 498, 1080)
assert approx(h, 1080) and approx(w, 498) and approx(l, (1920 - 498) / 2)

# tall window: width-limited, centered vertically
l, t, w, h = video_rect(300, 1000, 498, 1080)
assert approx(w, 300) and approx(l, 0) and approx(t, (1000 - h) / 2)

assert video_rect(0, 600, 498, 1080) == (0.0, 0.0, 0.0, 0.0)

# --- point mapping ---------------------------------------------------------------------------
rect = (100.0, 50.0, 200.0, 400.0)
assert to_axis(100, 50, rect) == (0, 0)
assert to_axis(300, 450, rect) == (AXIS_MAX, AXIS_MAX)
assert to_axis(200, 250, rect) == (5000, 5000)
assert to_axis(-999, 9999, rect) == (0, AXIS_MAX)  # clamped
assert inside(100, 50, rect) and not inside(300, 450, rect) and not inside(99, 100, rect)
assert mirror_point(2500, 7000) == (7500, 3000)

# --- real UxPlay output (taken from the PoC 1 session) ---------------------------------------------
size_line = ("raop_rtp_mirror width_source = 498.000000 height_source = 1080.000000 "
             "x_offset = 711.000000 y_offset = 0.000000 width = 498.000000 height = 1080.000000")
assert parse_uxplay_line(size_line) == {"video_size": (498.0, 1080.0)}
dev_line = "connection request from Example Phone ぷろ (iPhone16,1) with deviceID = AA:BB:CC:DD:EE:FF"
assert parse_uxplay_line(dev_line) == {"device": "Example Phone ぷろ", "model": "iPhone16,1"}  # non-ASCII names must survive
assert parse_uxplay_line("Open connections: 1") is None

# --- patched uxplay: the real sink size, including a non-square pixel aspect ratio ----------------
assert parse_uxplay_line("iScrMirror: sink-caps 498x1080 par 1/1") == {"sink_size": (498.0, 1080.0)}
assert parse_uxplay_line("iScrMirror: sink-caps 1920x886 par 1/1") == {"sink_size": (1920.0, 886.0)}
assert parse_uxplay_line("iScrMirror: sink-caps 960x1080 par 2/1") == {"sink_size": (1920.0, 1080.0)}
assert parse_uxplay_line("iScrMirror: sink-caps 100x100 par 0/1") is None  # invalid ratio is ignored

# --- window follow: native size, scaled down only if it does not fit -----------------------------------
assert fit_client_size(498, 1080, 3440, 1400, 16, 39) == (498, 1080)          # fits as is
assert fit_client_size(1920, 886, 3440, 1400, 16, 39) == (1920, 886)          # landscape fits as is
cw, ch = fit_client_size(1920, 1080, 1280, 720, 16, 39)                       # small monitor: scaled down
assert cw + 16 <= 1280 * 0.9 + 1 and ch + 39 <= 720 * 0.9 + 1 and abs(cw / ch - 1920 / 1080) < 0.01
assert clamp_position(3000, 200, 600, 500, (0, 0, 3440, 1400)) == (2840, 200)   # pushed back inside
assert clamp_position(-50, 1300, 400, 500, (0, 0, 3440, 1400)) == (0, 900)
assert clamp_position(100, 100, 400, 500, (0, 0, 3440, 1400)) == (100, 100)    # already inside

# --- rotations: picture point -> digitizer point ------------------------------------------------------
from geometry import next_rotation, rotate_unit, to_unit, unit_to_axis  # noqa: E402

# The four corners must permute, and each rotation must be undone by the opposite one.
corners = {(0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0)}
for deg in (0, 90, 180, 270):
    assert {rotate_unit(u, v, deg) for u, v in corners} == corners, deg
assert rotate_unit(0.25, 0.75, 0) == (0.25, 0.75)
assert rotate_unit(0.75, 0.25, 90) == (0.75, 0.75)     # (1-v, u)
assert rotate_unit(0.75, 0.25, 180) == (0.25, 0.75)    # (1-u, 1-v)
assert rotate_unit(0.75, 0.25, 270) == (0.25, 0.25)    # (v, 1-u)
for u, v in ((0.2, 0.9), (0.6, 0.1)):  # 90 then 270 is the identity, 90 twice is 180
    assert tuple(round(c, 9) for c in rotate_unit(*rotate_unit(u, v, 90), 270)) == (u, v)
    assert tuple(round(c, 9) for c in rotate_unit(*rotate_unit(u, v, 90), 90)) == tuple(round(c, 9) for c in rotate_unit(u, v, 180))
assert to_unit(300, 450, (100.0, 50.0, 200.0, 400.0)) == (1.0, 1.0)
assert to_unit(-5, 9999, (100.0, 50.0, 200.0, 400.0)) == (0.0, 1.0)  # clamped
assert unit_to_axis(0.5, 1.2) == (5000, AXIS_MAX)
assert [next_rotation(r) for r in (90, 270, 0, 180)] == [270, 0, 180, 90]

print("geometry: all checks passed")
