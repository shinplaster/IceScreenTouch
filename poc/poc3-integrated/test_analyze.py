# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Check analyze_orientation_log.py against a synthetic log with a known orientation-dependent field."""

import subprocess
import sys
import tempfile
from pathlib import Path

PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>sessionID</key><integer>{sid}</integer>
<key>orientation</key><integer>{orient}</integer>
<key>fps</key><real>{fps}</real>
<key>locked</key><false/>
</dict></plist>"""


def block(ts: int, orient: int, header_byte: str, fps: float, size: str = "1920x886") -> str:
    return "\n".join([
        f"00:00:{ts:02d}.100 Received video streaming performance info packet from client: payload_size 194 header 05 00 00 00 ts_raw = {ts * 1000}",
        PLIST.format(sid=777, orient=orient, fps=fps),
        f"00:00:{ts:02d}.200 iScrMirror: codec header",
        f"00:00:{ts:02d}.200 00 00 00 25 01 00 16 01 {header_byte} 00 00 00",   # the real log has no trailing space
        f"00:00:{ts:02d}.300 iScrMirror: sink-caps {size} par 1/1",
    ])


log = "\n".join([
    "00:00:01.000 connection request from phone (iPhone16,1) with deviceID = AA:BB",   # before any marker
    "00:00:02.000 ### MARK landscapeA",
    block(3, orient=3, header_byte="aa", fps=29.5),
    block(4, orient=3, header_byte="aa", fps=30.1),
    "00:00:05.000 ### MARK landscapeB",
    block(6, orient=4, header_byte="aa", fps=28.7),
    block(7, orient=4, header_byte="aa", fps=30.4, size="1920x1080"),   # a size that only exists in B
    "00:00:07.500 ### MARK bridge landscape_rot=270",   # an automatic marker must not split a segment
    "00:00:08.000 ### MARK landscapeA",
    block(9, orient=3, header_byte="aa", fps=29.9),
]) + "\n"

with tempfile.TemporaryDirectory() as d:
    path = Path(d) / "synthetic.log"
    path.write_text(log, encoding="utf-8")
    script = Path(__file__).with_name("analyze_orientation_log.py")
    res = subprocess.run([sys.executable, "-X", "utf8", str(script), str(path)], capture_output=True, text=True)
    out = res.stdout
    print(out)
    assert res.returncode == 0, res.stderr

    # the orientation field is constant per label and differs between labels
    assert "orientation  CONSTANT per label (strong candidate)" in out
    assert "landscapeA     ['3']" in out and "landscapeB     ['4']" in out
    # a value that is identical in all labels is not reported; a drifting value is reported as noise-free variation only
    assert "sessionID" not in out and "locked" not in out
    # labels merge across repeated segments, and the automatic marker did not create a new label
    assert "landscapeA       2 segment(s)" in out
    assert "bridge" not in out and "(start)" not in out
    # sizes are NOT masked: the size that exists only in B is reported, the shared one is not
    assert "iScrMirror: sink-caps 1920x1080 par 1/1" in out
    assert "(none)" in out.split("== hex dumps")[1]  # identical header bytes in all labels: nothing to report

    # comparing a single label is rejected with a helpful message
    one = subprocess.run([sys.executable, "-X", "utf8", str(script), str(path), "--labels", "landscapeA"],
                         capture_output=True, text=True)
    assert one.returncode != 0 and "need at least two" in one.stderr

print("analyze_orientation_log: all checks passed")
