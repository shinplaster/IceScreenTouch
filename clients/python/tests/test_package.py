# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

import pathlib
import re

import icescreentouch


def test_the_version_in_the_package_matches_pyproject():
    text = (pathlib.Path(__file__).resolve().parent.parent / "pyproject.toml").read_text(encoding="utf-8")
    declared = re.search(r'^version\s*=\s*"([^"]+)"', text, re.MULTILINE)
    assert declared is not None
    assert icescreentouch.__version__ == declared.group(1)


def test_importing_the_mcp_tools_without_the_extra_gives_the_install_hint():
    import subprocess
    import sys

    code = (
        "import sys\n"
        "sys.modules['mcp'] = None\n"
        "sys.modules['pydantic'] = None\n"
        "try:\n"
        "    import icescreentouch.mcp.tools\n"
        "except ImportError as exc:\n"
        "    print(exc)\n"
        "else:\n"
        "    print('imported')\n"
    )
    done = subprocess.run([sys.executable, "-c", code], capture_output=True, text=True, timeout=60)
    assert "pip install icescreentouch[mcp]" in done.stdout, (done.stdout, done.stderr)
