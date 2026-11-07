"""Find what in a collected uxplay log depends on the phone's orientation.

Record a log with:  .\\run.ps1 --collect -v   and type `mark <label>` in the console right after
each rotation (e.g. portrait, landscapeA, landscapeB). Then run:

    python analyze_orientation_log.py logs\\uxplay-....log [--labels landscapeA,landscapeB]

Lines of the log are grouped by the label of the preceding marker; labels used more than once are
merged. The script prints every piece of information (plist values, log lines, header bytes) that is
present for one label but different or missing for another. A field that is constant inside each
label and different between labels is the best candidate for an orientation signal.
"""

from __future__ import annotations

import argparse
import collections
import re
from pathlib import Path

STAMP = re.compile(r"^\d\d:\d\d:\d\d\.\d{3} ")
MARK = re.compile(r"^### MARK (.*)$")
# A hex dump row: pairs of hex digits separated by single spaces (the log keeps no trailing space).
HEX_LINE = re.compile(r"^(?:[0-9a-fA-F]{2}(?: |$))+$")
PLIST_PAIR = re.compile(r"<key>([^<]*)</key>\s*<(\w+)(?:\s*/>|[^>]*>([^<]*)</\2>)")
AUTO_MARK_PREFIX = "bridge "  # written by the app itself (rotation setting changes); not a user label


# Lines whose numbers ARE the information (picture sizes), so they must not be masked.
KEEP_NUMBERS = ("iScrMirror: sink-caps", "raop_rtp_mirror width_source", "*** codec is")


def norm(line: str) -> str:
    """Mask the numbers so that lines differing only by timestamps / counters compare equal."""
    line = line.strip()
    if line.startswith(KEEP_NUMBERS):
        return line
    return re.sub(r"\d+(?:\.\d+)?", "#", line)


def read_segments(path: Path) -> list[tuple[str, list[str]]]:
    segments: list[tuple[str, list[str]]] = []
    label, current = "(start)", []
    for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
        line = STAMP.sub("", raw)
        m = MARK.match(line)
        if m and m.group(1).startswith(AUTO_MARK_PREFIX):
            continue  # written by the app itself; neither a label nor information about the phone
        if m:
            segments.append((label, current))
            label, current = m.group(1).strip(), []
        else:
            current.append(line)
    segments.append((label, current))
    return [s for s in segments if s[1]]


class Facts:
    def __init__(self) -> None:
        self.lines = 0
        self.segments = 0
        self.text: collections.Counter[str] = collections.Counter()
        self.plist: dict[str, set[str]] = collections.defaultdict(set)
        self.hex: dict[str, set[str]] = collections.defaultdict(set)

    def add(self, lines: list[str]) -> None:
        self.segments += 1
        self.lines += len(lines)
        context = ""
        for line in lines:
            if not line.strip():
                continue
            if HEX_LINE.match(line):
                self.hex[context].add(line.strip())
                continue
            context = norm(line)
            self.text[context] += 1
        for key, kind, value in PLIST_PAIR.findall("\n".join(lines)):
            self.plist[key].add(value if value != "" else kind)  # <true/> has no text, keep its type


def compare(facts: dict[str, Facts], limit: int) -> list[str]:
    labels = list(facts)
    out: list[str] = []

    out.append("== segments ==")
    for label, f in facts.items():
        sizes = sorted(k.replace("iScrMirror: sink-caps ", "") for k in f.text if k.startswith("iScrMirror: sink-caps"))
        packets = sum(c for k, c in f.text.items() if k.startswith("Received unencrypted codec packet"))
        out.append(f"  {label:<16} {f.segments} segment(s), {f.lines} lines, codec packets: {packets}"
                   + (f", sink: {sizes}" if sizes else ""))

    out.append("\n== plist values that differ between labels ==")
    keys = sorted({k for f in facts.values() for k in f.plist})
    shown = 0
    for key in keys:
        per = {lb: sorted(facts[lb].plist.get(key, set())) for lb in labels}
        if len({tuple(v) for v in per.values()}) <= 1:
            continue
        constant = all(len(v) == 1 for v in per.values())
        noisy = any(len(v) > 8 for v in per.values())
        tag = "CONSTANT per label (strong candidate)" if constant else ("varies a lot (noise?)" if noisy else "")
        out.append(f"  {key}  {tag}")
        for lb in labels:
            vals = per[lb]
            out.append(f"      {lb:<14} {vals if len(vals) <= 8 else str(vals[:8]) + ' ...'}")
        shown += 1
        if shown >= limit:
            out.append("  ... (more omitted)")
            break
    if shown == 0:
        out.append("  (none)")

    out.append("\n== log lines (numbers masked) present for some labels only ==")
    shown = 0
    for line in sorted({k for f in facts.values() for k in f.text}):
        counts = {lb: facts[lb].text.get(line, 0) for lb in labels}
        if all(c > 0 for c in counts.values()) or not any(counts.values()):
            continue
        out.append(f"  {line[:110]}")
        out.append("      " + ", ".join(f"{lb}={c}" for lb, c in counts.items()))
        shown += 1
        if shown >= limit:
            out.append("  ... (more omitted)")
            break
    if shown == 0:
        out.append("  (none)")

    out.append("\n== hex dumps (codec header, SPS/PPS, ...) that differ between labels ==")
    shown = 0
    for context in sorted({c for f in facts.values() for c in f.hex}):
        per = {lb: facts[lb].hex.get(context, set()) for lb in labels}
        if len({frozenset(v) for v in per.values()}) <= 1:
            continue
        out.append(f"  after: {context[:100]}")
        base = set.intersection(*[v for v in per.values() if v]) if any(per.values()) else set()
        for lb in labels:
            only = sorted(per[lb] - base)
            out.append(f"      {lb:<14} {len(per[lb])} distinct line(s); not shared: {only[:4]}{' ...' if len(only) > 4 else ''}")
        shown += 1
        if shown >= limit:
            out.append("  ... (more omitted)")
            break
    if shown == 0:
        out.append("  (none)")
    return out


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("log", type=Path)
    ap.add_argument("--labels", help="comma-separated labels to compare (default: all labels found)")
    ap.add_argument("--limit", type=int, default=40, help="max entries per section")
    ap.add_argument("--include-start", action="store_true",
                    help="also treat what was logged before the first marker as a phase (normally ignored)")
    ns = ap.parse_args()

    groups: dict[str, Facts] = {}
    for label, lines in read_segments(ns.log):
        if label == "(start)" and not ns.include_start:
            continue
        groups.setdefault(label, Facts()).add(lines)
    if ns.labels:
        wanted = [s.strip() for s in ns.labels.split(",")]
        missing = [w for w in wanted if w not in groups]
        if missing:
            raise SystemExit(f"labels not found in the log: {missing}; available: {list(groups)}")
        groups = {w: groups[w] for w in wanted}
    if len(groups) < 2:
        raise SystemExit(f"need at least two labelled phases to compare; found: {list(groups)}. "
                         "Use `mark <label>` in the console after each rotation.")
    print("\n".join(compare(groups, ns.limit)))


if __name__ == "__main__":
    main()
