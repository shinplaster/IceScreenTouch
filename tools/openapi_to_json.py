# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Generate the embedded OpenAPI document from the hand-written YAML spec.

docs/api/openapi.yaml is the source of truth; this script writes src/IceScreenTouch.Api/openapi.json, the file the
app embeds and serves at GET /v1/openapi.json. The output is deterministic: UTF-8 without BOM, LF line ends,
2-space indent, keys in document order, a trailing newline.

Usage (from the repository root):

    uv run --with pyyaml python tools/openapi_to_json.py

Pass --check to verify that the JSON file is up to date without writing it (exit code 1 when it is stale).
"""

import argparse
import json
import sys
from pathlib import Path

import yaml

ROOT = Path(__file__).resolve().parent.parent
SOURCE = ROOT / "docs" / "api" / "openapi.yaml"
TARGET = ROOT / "src" / "IceScreenTouch.Api" / "openapi.json"


class SpecError(Exception):
    """The YAML does not mean what the author meant (YAML 1.1 implicit typing, duplicate keys)."""


class StrictLoader(yaml.SafeLoader):
    """A safe loader that refuses duplicate keys and keys that are not strings."""

    def construct_mapping(self, node, deep=False):
        seen = set()
        for key_node, _ in node.value:
            key = self.construct_object(key_node, deep=True)
            if not isinstance(key, str):
                raise SpecError(
                    f"line {key_node.start_mark.line + 1}: mapping key {key!r} is a {type(key).__name__}, not a "
                    "string (YAML 1.1 implicit typing: quote it, for example \"200\")"
                )
            if key in seen:
                raise SpecError(f"line {key_node.start_mark.line + 1}: duplicate mapping key {key!r}")
            seen.add(key)
        return super().construct_mapping(node, deep)


def load_spec(path: Path):
    with path.open(encoding="utf-8") as f:
        return yaml.load(f, Loader=StrictLoader)  # noqa: S506 - StrictLoader is a SafeLoader


def render(spec) -> str:
    return json.dumps(spec, ensure_ascii=False, indent=2) + "\n"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--check", action="store_true", help="fail when the JSON file is not up to date")
    args = parser.parse_args()

    try:
        spec = load_spec(SOURCE)
    except SpecError as ex:
        print(f"{SOURCE}: {ex}", file=sys.stderr)
        return 2

    if not isinstance(spec, dict) or spec.get("openapi") != "3.1.0":
        print(f"{SOURCE}: the document must be a mapping with openapi: \"3.1.0\" (quoted)", file=sys.stderr)
        return 2

    text = render(spec)
    if args.check:
        current = TARGET.read_bytes().decode("utf-8") if TARGET.exists() else None
        if current != text:
            print(f"{TARGET} is stale. Run: uv run --with pyyaml python tools/openapi_to_json.py", file=sys.stderr)
            return 1
        return 0

    TARGET.write_bytes(text.encode("utf-8"))  # bytes: no newline translation on Windows
    print(f"wrote {TARGET.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
