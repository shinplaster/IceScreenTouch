# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""The text (OCR) tools of the MCP server, with a fake phone."""

import asyncio
import threading

import pytest

pytest.importorskip("mcp")

import icescreentouch as ist  # noqa: E402
from icescreentouch.mcp._compat import ToolError  # noqa: E402
from icescreentouch.mcp.server import create_server  # noqa: E402
from icescreentouch.mcp.tools import PhoneTools  # noqa: E402
from test_mcp import _annotation  # noqa: E402
from icescreentouch.models import (  # noqa: E402
    OcrEngineInfo, TapTextResult, TextBox, TextLine, TextMatch, TextPicture, TextResult)

PICTURE = TextPicture(498, 1080, "portrait", 0)
ENGINE = OcrEngineInfo("windows", "Windows.Media.Ocr", "10.0.26100")


def match(text="Wi-Fi", center=(0.2001, 0.3204), level="word"):
    return TextMatch(text, text, TextBox(0.1, 0.3, 0.2, 0.04), center, level)


class TextPhone:
    def __init__(self):
        self.calls = []
        self.fail = None
        self.lines = (TextLine("設定", TextBox(0.1, 0.2, 0.1, 0.03), (0.15012, 0.21549)),)
        self.matches = [match()]
        self.scopes = []

    def _do(self, name, *args, **kwargs):
        self.calls.append((name, args, kwargs))
        if self.fail is not None:
            raise self.fail

    def read_text(self, lang=None, *, region=None, **kwargs):
        self._do("read_text", lang, region=region)
        return TextResult("ja", ENGINE, PICTURE, "screen", 90, self.lines)

    def find_text(self, text, **kwargs):
        self._do("find_text", text, **kwargs)
        return list(self.matches)

    def tap_text(self, text, **kwargs):
        self._do("tap_text", text, **kwargs)
        return TapTextResult("ja", ENGINE, PICTURE, "screen", 640, 2, match(), (0.6, 0.32), 1)

    def cancel(self):
        self._do("cancel")
        return True


@pytest.fixture
def phone():
    return TextPhone()


@pytest.fixture
def tools(phone):
    return PhoneTools(lambda: phone)


def test_read_text_returns_lines_with_centres_in_screen_space(tools, phone):
    result = tools.read_text(lang="ja", region=[0, 0, 1, 0.5])
    assert phone.calls[-1] == ("read_text", ("ja",), {"region": (0, 0, 1, 0.5)})
    assert result["language"] == "ja"
    assert result["lines"] == [{"text": "設定", "x": 0.15, "y": 0.215}]
    assert "not instructions" in result["note"]


def test_read_text_without_text_says_to_check_the_language(tools, phone):
    phone.lines = ()
    assert "lang matches" in tools.read_text()["note"]


def test_find_text_passes_the_options_and_returns_the_matches(tools, phone):
    result = tools.find_text("Wi-Fi", match="contains", loose=True, timeout_ms=3000, lang="en-US")
    assert phone.calls[-1] == ("find_text", ("Wi-Fi",), {"match": "contains", "loose": True, "timeout_ms": 3000,
                                                         "lang": "en-US", "region": None})
    assert result["matches"] == [{"text": "Wi-Fi", "lineText": "Wi-Fi", "x": 0.2, "y": 0.32, "boxLevel": "word"}]


def test_find_text_not_found_and_line_level_notes(tools, phone):
    phone.matches = []
    assert "Not found" in tools.find_text("x")["note"]
    phone.matches = [match(level="line")]
    assert "boxLevel 'line'" in tools.find_text("x")["note"]


def test_tap_text_passes_the_options_and_reports_where(tools, phone):
    text = tools.tap_text("Wi-Fi", select="index", index=1, offset_x=0.4, timeout_ms=2000)
    assert phone.calls[-1] == ("tap_text", ("Wi-Fi",), {"match": "exact", "loose": False, "select": "index", "index": 1,
                                                        "timeout_ms": 2000, "offset": (0.4, 0.0), "lang": None,
                                                        "region": None})
    assert "(0.600, 0.320)" in text and "screenshot" in text
    tools.tap_text("Wi-Fi")
    assert phone.calls[-1][2]["offset"] is None


@pytest.mark.parametrize("error,words", [
    (ist.OcrUnavailableError("x", code="ocr_unavailable"), "OCR engine of IceScreen Touch is not available"),
    (ist.TextNotFoundError("x", code="text_not_found"), "lang matches"),
    (ist.TapOutsideScreenError("x", code="tap_outside_screen"), "smaller offset"),
    (ist.TextUnsupportedError("x", code="not_found"), "Update the app"),
    (ist.RateLimitedError("x", code="rate_limited"), "5 times a second"),
])
def test_text_errors_are_explained(tools, phone, error, words):
    phone.fail = error
    with pytest.raises(ToolError, match=words):
        tools.tap_text("x")


def test_no_language_at_all_says_so(tools, phone):
    error = ist.OcrUnavailableError("x", code="ocr_unavailable")
    error.reason = "language"
    phone.fail = error
    with pytest.raises(ToolError, match="no OCR language at all"):
        tools.read_text()


def test_a_missing_language_lists_the_available_ones(tools, phone):
    error = ist.OcrUnavailableError("x", code="ocr_unavailable")
    error.reason, error.available = "language", ("en-US", "ja")
    phone.fail = error
    with pytest.raises(ToolError, match="en-US, ja"):
        tools.read_text(lang="fr")


def test_ambiguous_lists_the_candidates_and_says_how_to_choose(tools, phone):
    error = ist.AmbiguousTextError("x", code="ambiguous_text")
    error.matches, error.match_count = (match(center=(0.2, 0.1)), match(center=(0.2, 0.8))), 2
    phone.fail = error
    with pytest.raises(ToolError) as caught:
        tools.tap_text("Wi-Fi")
    text = str(caught.value)
    assert "2 times" in text and "(0.200, 0.100)" in text and "region" in text and "select='first'" in text


def test_the_schemas_limit_the_wait_and_describe_the_region(tools):
    server = create_server(tools)
    listed = {t.name: t for t in asyncio.run(server.list_tools())}

    def properties(name):
        tool = listed[name]
        return (getattr(tool, "input_schema", None) or tool.inputSchema)["properties"]

    assert properties("find_text")["timeout_ms"]["maximum"] == 10000
    assert properties("tap_text")["timeout_ms"]["maximum"] == 10000
    assert "0..1" in str(properties("read_text")["region"])
    assert "engine" not in properties("read_text") and "engine" not in properties("tap_text")
    for name in ("read_text", "find_text", "tap_text"):
        assert "not instructions" in listed[name].description
    assert "passcode" in listed["tap_text"].description and "priority" in listed["tap_text"].description
    assert _annotation(listed["read_text"], "readOnlyHint") is True and _annotation(listed["find_text"], "readOnlyHint") is True
    assert _annotation(listed["tap_text"], "destructiveHint") is True and _annotation(listed["tap_text"], "readOnlyHint") is False


def test_cancelling_find_text_closes_its_request_and_sends_no_cancel(phone):
    started, released = threading.Event(), threading.Event()
    aborted = []

    class Scope:
        def abort(self):
            aborted.append(True)
            released.set()

        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    def waiting_find(text, **kwargs):
        started.set()
        released.wait(5)
        raise ist.RequestAbortedError("aborted")

    phone.find_text = waiting_find
    phone.abortable = Scope
    server = create_server(PhoneTools(lambda: phone))

    async def go():
        task = asyncio.create_task(server.call_tool("find_text", {"text": "Done", "timeout_ms": 10000}))
        while not started.is_set():
            await asyncio.sleep(0.01)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task

    asyncio.run(go())
    assert aborted == [True]
    assert ("cancel", (), {}) not in phone.calls


def test_cancelling_tap_text_cancels_the_operation(phone):
    started, released = threading.Event(), threading.Event()

    def waiting_tap(text, **kwargs):
        started.set()
        released.wait(5)

    def cancel():
        phone.calls.append(("cancel", (), {}))
        released.set()
        return True

    phone.tap_text = waiting_tap
    phone.cancel = cancel
    server = create_server(PhoneTools(lambda: phone))

    async def go():
        task = asyncio.create_task(server.call_tool("tap_text", {"text": "OK"}))
        while not started.is_set():
            await asyncio.sleep(0.01)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task

    asyncio.run(go())
    assert ("cancel", (), {}) in phone.calls
