# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""The text (OCR) calls of the client against the fake server (requests and answers held to docs/api/openapi.yaml)."""

import json
import threading
import time

import pytest
from conftest import Reply, problem

import icescreentouch as ist

ENGINE = {"id": "windows", "name": "Windows.Media.Ocr", "version": "10.0.26100"}
PICTURE = {"width": 498, "height": 1080, "orientation": "portrait", "rotation": 0}
WIFI = {"text": "Wi-Fi", "lineText": "Wi-Fi", "box": {"x": 0.1, "y": 0.3, "width": 0.2, "height": 0.04},
        "center": {"x": 0.2, "y": 0.32}, "boxLevel": "word"}


def read_answer(lines):
    return {"language": "ja", "engine": ENGINE, "picture": PICTURE, "space": "screen", "durationMs": 90, "lines": lines}


def find_answer(matches):
    return {"language": "ja", "engine": ENGINE, "picture": PICTURE, "space": "screen", "durationMs": 120, "attempts": 1,
            "matches": matches}


def tap_answer():
    return {"language": "ja", "engine": ENGINE, "picture": PICTURE, "space": "screen", "durationMs": 640, "attempts": 2,
            "tapped": {**WIFI, "at": {"x": 0.6, "y": 0.32}}, "matchCount": 1}


def ocr_problem(status, code, detail="x", **extras):
    reply = problem(status, code, detail)
    body = json.loads(reply.body)
    body.update(extras)
    return Reply(status, reply.content_type, json.dumps(body).encode())


def test_read_text_sends_the_options_and_parses_the_lines(api):
    api.route("POST", "/v1/text/read", read_answer([
        {"text": "設定", "box": {"x": 0.1, "y": 0.2, "width": 0.1, "height": 0.03}, "center": {"x": 0.15, "y": 0.215},
         "words": [{"text": "設", "box": {"x": 0.1, "y": 0.2, "width": 0.05, "height": 0.03}},
                   {"text": "定", "box": {"x": 0.15, "y": 0.2, "width": 0.05, "height": 0.03}}]}]))
    result = api.client().read_text("ja", region=(0, 0, 1, 0.5), words=True)

    assert api.requests[-1].json == {"space": "screen", "lang": "ja", "region": {"x": 0.0, "y": 0.0, "width": 1.0, "height": 0.5},
                                     "words": True}
    assert (result.language, result.engine.id, result.space, result.duration_ms) == ("ja", "windows", "screen", 90)
    line = result.lines[0]
    assert (line.text, line.center) == ("設定", (0.15, 0.215))
    assert [w.text for w in line.words] == ["設", "定"]
    assert line.confidence is None
    assert line.box.center == pytest.approx((0.15, 0.215))


def test_read_text_in_pixels_sends_the_ref(api):
    api.route("POST", "/v1/text/read", {**read_answer([]), "space": "pixel"})
    result = api.client().read_text(space="pixel", ref=(498, 1080), engine="windows")
    assert api.requests[-1].json == {"space": "pixel", "ref": {"width": 498, "height": 1080}, "engine": "windows"}
    assert result.lines == ()


def test_find_text_returns_the_matches_or_an_empty_list(api):
    api.route("POST", "/v1/text/find", find_answer([WIFI]))
    matches = api.client().find_text("wi-fi", match="contains", loose=True, case_sensitive=True, timeout_ms=3000, poll_ms=400)
    assert api.requests[-1].json == {"text": "wi-fi", "match": "contains", "loose": True, "caseSensitive": True,
                                     "timeoutMs": 3000, "pollMs": 400, "space": "screen"}
    assert [(m.text, m.center, m.box_level) for m in matches] == [("Wi-Fi", (0.2, 0.32), "word")]

    api.route("POST", "/v1/text/find", find_answer([]))
    assert api.client().find_text("Bluetooth") == []
    assert api.requests[-1].json == {"text": "Bluetooth", "space": "screen"}


def test_wait_for_text_returns_the_first_match_or_raises(api):
    api.route("POST", "/v1/text/find", find_answer([WIFI, {**WIFI, "center": {"x": 0.2, "y": 0.8}}]))
    assert api.client().wait_for_text("Wi-Fi", timeout_ms=2000).center == (0.2, 0.32)
    assert api.requests[-1].json["timeoutMs"] == 2000

    api.route("POST", "/v1/text/find", find_answer([]))
    with pytest.raises(ist.TextNotFoundError) as caught:
        api.client().wait_for_text("Done")
    assert caught.value.code == "text_not_found"
    assert api.requests[-1].json["timeoutMs"] == 10000


def test_tap_text_sends_the_options_and_parses_the_result(api):
    api.route("POST", "/v1/touch/tap-text", tap_answer())
    result = api.client().tap_text("Wi-Fi", select="index", index=1, offset=(0.4, 0.0), settle_ms=0, hold_ms=100, count=2,
                                   interval_ms=200, timeout_ms=3000, lang="en-US")
    assert api.requests[-1].json == {"text": "Wi-Fi", "timeoutMs": 3000, "space": "screen", "lang": "en-US", "select": "index",
                                     "index": 1, "offset": {"x": 0.4, "y": 0.0}, "settleMs": 0, "holdMs": 100, "count": 2,
                                     "intervalMs": 200}
    assert (result.tapped.text, result.at, result.match_count, result.attempts) == ("Wi-Fi", (0.6, 0.32), 1, 2)


def test_tap_text_defaults_send_only_the_text(api):
    api.route("POST", "/v1/touch/tap-text", tap_answer())
    api.client().tap_text("Wi-Fi")
    assert api.requests[-1].json == {"text": "Wi-Fi", "space": "screen"}


def test_ocr_unavailable_carries_the_available_languages(api):
    api.route("POST", "/v1/text/read", ocr_problem(409, "ocr_unavailable", reason="language", engine="windows",
                                                   available=["en-US", "ja"], engines=["windows"]))
    with pytest.raises(ist.OcrUnavailableError) as caught:
        api.client().read_text("fr")
    error = caught.value
    assert (error.reason, error.engine, error.available, error.engines) == ("language", "windows", ("en-US", "ja"), ("windows",))


@pytest.mark.parametrize("code,cls", [("ambiguous_text", ist.AmbiguousTextError), ("text_not_found", ist.TextNotFoundError),
                                      ("tap_outside_screen", ist.TapOutsideScreenError)])
def test_tap_text_errors_carry_the_candidates(api, code, cls):
    api.route("POST", "/v1/touch/tap-text", ocr_problem(409, code, matches=[WIFI, WIFI], matchCount=5))
    with pytest.raises(cls) as caught:
        api.client().tap_text("Wi-Fi")
    assert caught.value.match_count == 5
    assert [m.center for m in caught.value.matches] == [(0.2, 0.32), (0.2, 0.32)]


def test_an_app_without_ocr_is_a_clear_error(api):
    for call in (lambda c: c.read_text(), lambda c: c.find_text("x"), lambda c: c.tap_text("x")):
        with pytest.raises(ist.TextUnsupportedError) as caught:
            call(api.client())
        assert "update the app" in str(caught.value)


@pytest.mark.parametrize("call", [
    lambda c: c.read_text(space="device"),
    lambda c: c.read_text(space="pixel"),
    lambda c: c.read_text(region=(0, 0, 1)),
    lambda c: c.find_text(""),
    lambda c: c.find_text("x" * 201),
    lambda c: c.find_text("x", match="regex"),
    lambda c: c.find_text("x", timeout_ms=30001),
    lambda c: c.find_text("x", timeout_ms=1.5),
    lambda c: c.find_text("x", poll_ms=100),
    lambda c: c.tap_text("x", select="random"),
    lambda c: c.tap_text("x", select="index"),
    lambda c: c.tap_text("x", index=1),
])
def test_arguments_the_app_would_refuse_are_refused_locally(api, call):
    with pytest.raises(ist.InvalidArgumentError):
        call(api.client())
    assert api.requests == []


def test_find_text_waits_longer_than_the_plain_timeout(api):
    def slow(_request):
        time.sleep(0.6)
        return find_answer([WIFI])

    api.route("POST", "/v1/text/find", slow)
    # The client's own timeout is 0.2 s, but the call may wait timeout_ms on top of it.
    assert api.client(timeout=0.2).find_text("Wi-Fi", timeout_ms=1000)


def test_an_abortable_request_can_be_abandoned_from_another_thread(api):
    released = threading.Event()

    def hang(_request):
        released.wait(5)
        return find_answer([])

    api.route("POST", "/v1/text/find", hang)
    client = api.client()
    outcome = {}

    def run():
        with client.abortable() as scope:
            outcome["scope"] = scope
            try:
                client.find_text("Done", timeout_ms=20000)
            except ist.IceScreenTouchError as exc:
                outcome["error"] = exc

    worker = threading.Thread(target=run)
    worker.start()
    deadline = time.monotonic() + 3
    while "scope" not in outcome or not api.requests:
        assert time.monotonic() < deadline
        time.sleep(0.01)
    started = time.monotonic()
    outcome["scope"].abort()
    worker.join(3)
    released.set()
    assert not worker.is_alive()
    assert time.monotonic() - started < 2
    assert isinstance(outcome["error"], ist.RequestAbortedError)


def test_an_aborted_scope_sends_nothing(api):
    api.route("POST", "/v1/text/read", read_answer([]))
    client = api.client()
    with client.abortable() as scope:
        scope.abort()
        with pytest.raises(ist.RequestAbortedError) as caught:
            client.read_text()
    assert caught.value.request_sent is False
    assert api.requests == []


def test_requests_outside_a_scope_are_not_affected(api):
    api.route("POST", "/v1/text/read", read_answer([]))
    client = api.client()
    with client.abortable() as scope:
        scope.abort()
    assert client.read_text().lines == ()


def test_the_status_has_the_ocr_part(api):
    ocr = api.client().get_status().ocr
    assert (ocr.available, ocr.engine.id, ocr.engine.kind, ocr.engine.word_boxes) == (True, "windows", "builtin", True)
    assert (ocr.languages, ocr.default_language, ocr.engines) == (("en-US", "ja"), "ja", ("windows",))
    assert api.client().get_status().to_dict()["ocr"]["languages"] == ["en-US", "ja"]


def test_poll_ms_reaches_the_app_from_find_and_wait(api):
    api.route("POST", "/v1/text/find", find_answer([WIFI]))
    api.client().wait_for_text("Wi-Fi", timeout_ms=2000, poll_ms=800)
    assert api.requests[-1].json["pollMs"] == 800


def test_the_text_length_is_counted_as_the_app_counts_it(api):
    api.route("POST", "/v1/text/find", find_answer([]))
    api.client().find_text("😀" * 100)  # 200 UTF-16 code units: allowed
    with pytest.raises(ist.InvalidArgumentError):
        api.client().find_text("😀" * 101)


def test_a_malformed_number_in_an_answer_does_not_raise():
    result = ist.TextResult.from_json({"language": "ja", "durationMs": None, "picture": {"width": "x"},
                                       "lines": [{"text": "a", "box": {"x": None}, "center": {"x": "1"}}]})
    assert (result.duration_ms, result.picture.width, result.lines[0].box.x, result.lines[0].center) == (0, 0, 0.0, (0.0, 0.0))
