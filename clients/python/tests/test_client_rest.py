# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

import io
import json
import time

import pytest
from conftest import OTHER_TOKEN, STATUS, TOKEN, Reply, problem

import icescreentouch as ist
from icescreentouch import Client


def last(api):
    return api.requests[-1]


# ---- Requests: headers and bodies exactly as the C# side expects -------------------------------------------

def test_request_headers(api):
    api.client().get_status()
    request = last(api)
    assert request.headers["authorization"] == f"Bearer {TOKEN}"
    assert request.headers["host"] == f"127.0.0.1:{api.port}"
    assert "origin" not in request.headers
    assert "cookie" not in request.headers


def test_body_requests_are_json_utf8(api):
    api.client().tap(0.5, 0.5)
    assert last(api).headers["content-type"] == "application/json; charset=utf-8"


def test_status_is_parsed(api):
    status = api.client().get_status()
    assert status.version == "0.3.0"
    assert status.api_version == 1
    assert status.devices[0].id == "iphone-1" and status.devices[0].subscribed
    assert status.picture.size == (390, 844) and status.picture.orientation == "portrait"
    assert status.rotation.landscape == 90 and status.rotation.portrait == 0
    assert status.operating is False
    assert status.to_dict()["picture"]["height"] == 844


def test_status_without_picture(api):
    api.route("GET", "/v1/status", {**STATUS, "picture": None, "receiver": None})
    status = api.client().get_status()
    assert status.picture is None and status.receiver is None


def test_tap_defaults_to_screen_space(api):
    api.client().tap(0.25, 0.75)
    assert last(api).path == "/v1/touch/tap"
    assert last(api).json == {"x": 0.25, "y": 0.75, "space": "screen"}


def test_tap_all_options(api):
    api.client().tap(0.5, 0.9, hold_ms=100, count=2, interval_ms=150)
    assert last(api).json == {"x": 0.5, "y": 0.9, "space": "screen", "holdMs": 100, "count": 2, "intervalMs": 150}


def test_tap_pixel_sends_ref(api):
    api.client().tap_pixel(540, 1820, ref=(1179, 2556))
    assert last(api).json == {"x": 540.0, "y": 1820.0, "space": "pixel", "ref": {"width": 1179, "height": 2556}}


def test_pixel_ref_can_be_an_object_with_size(api):
    class Image:
        size = (100, 200)

    api.client().tap(1, 2, "pixel", ref=Image())
    assert last(api).json["ref"] == {"width": 100, "height": 200}


def test_pixel_without_ref_is_refused_before_sending(api):
    with pytest.raises(ValueError):
        api.client().tap(1, 2, "pixel")
    assert api.requests == []


def test_bad_space_and_nan_are_refused_locally(api):
    client = api.client()
    with pytest.raises(ValueError):
        client.tap(0.5, 0.5, "world")
    with pytest.raises(ValueError):
        client.tap(float("nan"), 0.5)
    assert api.requests == []


def test_device_space_has_no_ref(api):
    api.client().tap(0.5, 0.5, "device", ref=(1, 1))
    assert last(api).json == {"x": 0.5, "y": 0.5, "space": "device"}


def test_long_press(api):
    api.client().long_press(0.5, 0.5, hold_ms=1200)
    assert last(api).path == "/v1/touch/long-press"
    assert last(api).json == {"x": 0.5, "y": 0.5, "space": "screen", "holdMs": 1200}


def test_swipe_field_names(api):
    api.client().swipe((0.5, 0.8), (0.5, 0.2), duration_ms=300, hold_before_ms=400, hold_after_ms=100)
    assert last(api).path == "/v1/touch/swipe"
    assert last(api).json == {"from": {"x": 0.5, "y": 0.8}, "to": {"x": 0.5, "y": 0.2}, "space": "screen",
                              "durationMs": 300, "holdBeforeMs": 400, "holdAfterMs": 100}


def test_swipe_in_pixels(api):
    api.client().swipe((100, 200), (100, 50), "pixel", ref=(390, 844))
    body = last(api).json
    assert body["space"] == "pixel" and body["ref"] == {"width": 390, "height": 844}


def test_path(api):
    api.client().path([(0.2, 0.2, 0), (0.8, 0.8, 100)])
    assert last(api).path == "/v1/touch/path"
    assert last(api).json == {"space": "screen", "points": [{"x": 0.2, "y": 0.2, "t": 0}, {"x": 0.8, "y": 0.8, "t": 100}]}


def test_pinch(api):
    api.client().pinch((0.5, 0.5), 0.1, 0.3, duration_ms=500)
    assert last(api).path == "/v1/touch/pinch"
    assert last(api).json == {"center": {"x": 0.5, "y": 0.5}, "startRadius": 0.1, "endRadius": 0.3,
                              "space": "screen", "durationMs": 500}


def test_press_key(api):
    api.client().press_key("a", ["cmd"])
    assert last(api).path == "/v1/keys/press"
    assert last(api).json == {"key": "a", "modifiers": ["cmd"]}
    api.client().press_key("Enter", hold_ms=50)
    assert last(api).json == {"key": "Enter", "modifiers": [], "holdMs": 50}


def test_type_text_returns_skipped(api):
    api.route("POST", "/v1/keys/type", {"skipped": "あ"})
    assert api.client().type_text("hあ", interval_ms=10) == "あ"
    assert last(api).json == {"text": "hあ", "intervalMs": 10}
    assert "あ".encode("utf-8") in last(api).body or b"\\u3042" in last(api).body


def test_home_and_cancel(api):
    client = api.client()
    client.home()
    assert last(api).method == "POST" and last(api).path == "/v1/home" and last(api).body == b""
    assert client.cancel() is False
    api.route("POST", "/v1/cancel", {"cancelled": True})
    assert client.cancel() is True


def test_rotation(api):
    client = api.client()
    assert (client.get_rotation().landscape, client.get_rotation().portrait) == (90, 0)
    api.route("PUT", "/v1/rotation", {"landscape": 270, "portrait": 0})
    info = client.set_rotation(landscape=270)
    assert info.landscape == 270
    assert last(api).json == {"landscape": 270}
    with pytest.raises(ValueError):
        client.set_rotation()


def test_set_target_sends_null_for_automatic(api):
    client = api.client()
    client.set_target("iphone-1")
    assert last(api).json == {"deviceId": "iphone-1"}
    client.set_target(None)
    assert last(api).method == "PUT" and last(api).json == {"deviceId": None}


# ---- Errors ------------------------------------------------------------------------------------------------

@pytest.mark.parametrize("status,code,cls", [
    (400, "invalid_request", ist.InvalidRequestError),
    (401, "unauthorized", ist.UnauthorizedError),
    (403, "forbidden_origin", ist.ForbiddenOriginError),
    (403, "purchase_required", ist.PurchaseRequiredError),
    (409, "not_running", ist.NotRunningError),
    (409, "not_connected", ist.NotConnectedError),
    (409, "no_picture", ist.NoPictureError),
    (409, "busy", ist.BusyError),
    (409, "user_interrupted", ist.UserInterruptedError),
    (409, "user_active", ist.UserActiveError),
    (409, "no_keyboard", ist.NoKeyboardError),
    (409, "no_home_button", ist.NoHomeButtonError),
    (409, "picture_changed", ist.PictureChangedError),
    (429, "rate_limited", ist.RateLimitedError),
    (499, "cancelled", ist.CancelledError),
    (500, "internal_error", ist.InternalError),
])
def test_each_code_has_its_own_exception(api, status, code, cls):
    api.route("POST", "/v1/home", problem(status, code, "the detail"))
    with pytest.raises(cls) as info:
        api.client().home()
    assert type(info.value) is cls
    assert info.value.code == code and info.value.status == status and info.value.detail == "the detail"
    assert isinstance(info.value, ist.IceScreenTouchError)


def test_unknown_code_is_a_plain_api_error(api):
    api.route("POST", "/v1/home", problem(409, "from_the_future", check=False))  # a code of a newer app
    with pytest.raises(ist.ApiError) as info:
        api.client().home()
    assert type(info.value) is ist.ApiError and info.value.code == "from_the_future"


def test_busy_and_user_active_are_not_retried(api):
    for code in ("busy", "user_active"):
        api.requests.clear()
        api.route("POST", "/v1/touch/tap", problem(409, code))
        with pytest.raises(ist.ApiError):
            api.client().tap(0.5, 0.5)
        assert len(api.requests) == 1


def test_non_problem_error_body(api):
    api.route("POST", "/v1/home", Reply(502, "text/html", b"<html>bad gateway</html>"))
    with pytest.raises(ist.ApiError) as info:
        api.client().home()
    assert info.value.status == 502 and info.value.code is None
    assert "bad gateway" not in str(info.value)


def test_wrong_token_is_unauthorized_without_leaking_it(api):
    client = Client(base_url=api.url, token=OTHER_TOKEN)
    with pytest.raises(ist.UnauthorizedError) as info:
        client.get_status()
    assert OTHER_TOKEN not in str(info.value) and OTHER_TOKEN not in repr(info.value)
    assert OTHER_TOKEN not in repr(client) and OTHER_TOKEN not in repr(client.connection)


def test_connection_refused_is_unavailable():
    import socket

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    client = Client(base_url=f"http://127.0.0.1:{port}", token=TOKEN)
    with pytest.raises(ist.ApiUnavailableError) as info:
        client.get_status()
    assert TOKEN not in str(info.value)


def test_non_loopback_base_url_is_refused():
    with pytest.raises(ist.ConnectionDiscoveryError):
        Client(base_url="http://example.com:47821", token=TOKEN)
    with pytest.raises(ist.ConnectionDiscoveryError):
        Client(base_url="https://127.0.0.1:47821", token=TOKEN)


def test_localhost_means_127_0_0_1(api):
    client = Client(base_url=f"http://localhost:{api.port}", token=TOKEN)
    client.get_status()
    assert last(api).headers["host"] == f"127.0.0.1:{api.port}"


# ---- Timeouts ----------------------------------------------------------------------------------------------

def test_timeout_is_a_distinct_error(api):
    def slow(_request):
        time.sleep(0.7)
        return STATUS

    api.route("GET", "/v1/status", slow)
    with pytest.raises(ist.ApiTimeoutError):
        api.client(timeout=0.2).get_status()


def test_timeout_includes_the_time_the_operation_takes(api):
    def slow(_request):
        time.sleep(0.7)
        return {}

    api.route("POST", "/v1/touch/long-press", slow)
    # 0.2 s base + 1.0 s hold = 1.2 s is more than the 0.7 s the fake server takes.
    api.client(timeout=0.2).long_press(0.5, 0.5, hold_ms=1000)
    with pytest.raises(ist.ApiTimeoutError):
        api.client(timeout=0.2).long_press(0.5, 0.5, hold_ms=100)


# ---- Screenshot --------------------------------------------------------------------------------------------

def png_bytes(width, height):
    from PIL import Image

    out = io.BytesIO()
    Image.new("RGB", (width, height), (10, 20, 30)).save(out, format="PNG")
    return out.getvalue()


def test_screenshot_headers_and_query(api):
    data = png_bytes(390, 844)
    api.route("GET", "/v1/screenshot", Reply(200, "image/png", data, {
        "X-IceScreenTouch-Orientation": "portrait", "X-IceScreenTouch-Rotation": "0",
        "X-IceScreenTouch-Source-Size": "1179x2556"}))
    shot = api.client().screenshot(max_size=1024)
    assert last(api).path == "/v1/screenshot?format=png&maxSize=1024"
    assert shot.data == data and shot.format == "png" and shot.content_type == "image/png"
    assert shot.size == (390, 844) and shot.width == 390 and shot.height == 844
    assert shot.orientation == "portrait" and shot.rotation == 0
    assert shot.source_size == (1179, 2556)
    assert shot.to_pil().size == (390, 844)
    assert "data=" not in repr(shot)


def test_screenshot_jpeg(api):
    from PIL import Image

    out = io.BytesIO()
    Image.new("RGB", (300, 640)).save(out, format="JPEG")
    api.route("GET", "/v1/screenshot", Reply(200, "image/jpeg", out.getvalue(), {
        "X-IceScreenTouch-Orientation": "portrait", "X-IceScreenTouch-Rotation": "180"}))
    shot = api.client().screenshot(format="jpg", quality=80)
    assert last(api).path == "/v1/screenshot?format=jpeg&quality=80"
    assert shot.format == "jpeg" and shot.size == (300, 640) and shot.rotation == 180 and shot.source_size is None


def test_screenshot_bad_format_is_refused_locally(api):
    with pytest.raises(ValueError):
        api.client().screenshot(format="gif")


def test_screenshot_not_implemented_yet_is_a_clear_error(api):
    # No route: the fake answers 404 not_found, like a server from before step A4.
    with pytest.raises(ist.ScreenshotUnsupportedError) as info:
        api.client().screenshot()
    assert isinstance(info.value, ist.NotFoundError) and info.value.status == 404
    assert "screenshot" in str(info.value)


@pytest.mark.parametrize("status,code,cls", [
    (409, "no_picture", ist.NoPictureError), (409, "not_running", ist.NotRunningError),
    (429, "rate_limited", ist.RateLimitedError), (403, "purchase_required", ist.PurchaseRequiredError)])
def test_screenshot_errors(api, status, code, cls):
    api.route("GET", "/v1/screenshot", problem(status, code))
    with pytest.raises(cls):
        api.client().screenshot()


def test_screenshot_rate_limit_carries_retry_after(api):
    reply = problem(429, "rate_limited", "at most 10 screenshots per second")
    reply.headers["Retry-After"] = "1"
    api.route("GET", "/v1/screenshot", reply)
    with pytest.raises(ist.RateLimitedError) as info:
        api.client().screenshot()
    assert info.value.retry_after == 1.0 and info.value.status == 429 and info.value.code == "rate_limited"
    # An answer without the header leaves it unknown.
    api.route("GET", "/v1/screenshot", problem(429, "rate_limited"))
    with pytest.raises(ist.RateLimitedError) as info:
        api.client().screenshot()
    assert info.value.retry_after is None


@pytest.mark.parametrize("kwargs", [
    {"max_size": 15}, {"max_size": 8193}, {"quality": 80}, {"format": "png", "quality": 80},
    {"format": "jpeg", "quality": 0}, {"format": "jpeg", "quality": 101}])
def test_screenshot_arguments_the_app_would_refuse_are_refused_locally(api, kwargs):
    with pytest.raises(ValueError):
        api.client().screenshot(**kwargs)
    assert not [r for r in api.requests if r.path.startswith("/v1/screenshot")]  # nothing was sent


def test_screenshot_limits_are_accepted(api):
    data = png_bytes(16, 16)
    api.route("GET", "/v1/screenshot", Reply(200, "image/png", data, {
        "X-IceScreenTouch-Orientation": "portrait", "X-IceScreenTouch-Rotation": "0", "X-IceScreenTouch-Source-Size": "16x16"}))
    api.client().screenshot(max_size=16)
    api.client().screenshot(max_size=8192)
    api.client().screenshot(format="jpeg", quality=1)
    api.client().screenshot(format="jpeg", quality=100)


def test_the_fake_server_holds_the_query_to_the_spec(api):
    # Bypass the client's own checks to show that the fake would catch a client that sends what the real server refuses.
    api.route("GET", "/v1/screenshot", Reply(200, "image/png", png_bytes(16, 16)))
    for query in ("format=gif", "maxSize=15", "format=png&quality=5", "size=5", "format=png&format=jpeg", "maxSize=+5"):
        with pytest.raises(ist.InvalidRequestError):
            api.client()._call("GET", f"/v1/screenshot?{query}", None, 5.0, "image/png")
    assert len(api.schema_errors) == 6
    api.schema_errors.clear()


# ---- Rediscovery after the app restarted -------------------------------------------------------------------

def test_new_token_in_api_json_is_picked_up_once(api, tmp_path, monkeypatch):
    monkeypatch.setattr("icescreentouch._winnet.listening_pids", lambda port: None)
    data_dir = tmp_path / "data"
    data_dir.mkdir()
    file = data_dir / "api.json"
    file.write_text(json.dumps({"port": api.port, "token": OTHER_TOKEN}))
    client = Client(env={"ICESCREENTOUCH_DATA_DIR": str(data_dir)})
    assert client.connection.token == OTHER_TOKEN
    file.write_text(json.dumps({"port": api.port, "token": TOKEN}))
    client.get_status()  # 401 with the old token, then the file is read again
    assert [r.headers["authorization"] for r in api.requests] == [f"Bearer {OTHER_TOKEN}", f"Bearer {TOKEN}"]


def test_explicit_connection_is_never_rediscovered(api, tmp_path):
    client = Client(base_url=api.url, token=OTHER_TOKEN, env={"ICESCREENTOUCH_DATA_DIR": str(tmp_path)})
    with pytest.raises(ist.UnauthorizedError):
        client.get_status()
    assert len(api.requests) == 1


class DropAfterRequest:
    """A TCP server that reads a whole request and closes the connection without answering (the app died mid-call)."""

    def __init__(self):
        import socket
        import threading

        self.requests = []
        self._socket = socket.socket()
        self._socket.bind(("127.0.0.1", 0))
        self._socket.listen()
        self.port = self._socket.getsockname()[1]
        self._thread = threading.Thread(target=self._serve, daemon=True)
        self._thread.start()

    def _serve(self):
        while True:
            try:
                connection, _ = self._socket.accept()
            except OSError:
                return
            with connection:
                data = b""
                while b"\r\n\r\n" not in data:
                    chunk = connection.recv(4096)
                    if not chunk:
                        break
                    data += chunk
                self.requests.append(data)

    def stop(self):
        self._socket.close()


def unused_port():
    import socket

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def test_a_connection_that_breaks_after_the_request_was_written_is_not_replayed(api, tmp_path):
    drop = DropAfterRequest()
    try:
        data_dir = tmp_path / "data"
        data_dir.mkdir()
        file = data_dir / "api.json"
        file.write_text(json.dumps({"port": drop.port, "token": OTHER_TOKEN}))
        client = Client(env={"ICESCREENTOUCH_DATA_DIR": str(data_dir)})
        assert client.connection.port == drop.port
        # The app "restarted" meanwhile: a re-discovery would now find a working API with another token.
        file.write_text(json.dumps({"port": api.port, "token": TOKEN}))
        with pytest.raises(ist.ApiUnavailableError) as info:
            client.tap(0.5, 0.5)
        assert info.value.request_sent is True and not isinstance(info.value, ist.ApiTimeoutError)
        assert len(drop.requests) == 1  # it reached the first app, which may have run it ...
        assert api.requests == []  # ... so it is not sent again to the second
    finally:
        drop.stop()


def test_a_refused_connection_is_retried_after_a_new_discovery(api, tmp_path):
    data_dir = tmp_path / "data"
    data_dir.mkdir()
    file = data_dir / "api.json"
    file.write_text(json.dumps({"port": unused_port(), "token": OTHER_TOKEN}))  # the app was restarted on another port
    client = Client(env={"ICESCREENTOUCH_DATA_DIR": str(data_dir)})
    file.write_text(json.dumps({"port": api.port, "token": TOKEN}))
    client.tap(0.5, 0.5)
    assert len(api.requests) == 1 and api.requests[0].headers["authorization"] == f"Bearer {TOKEN}"


def test_refused_connection_says_nothing_was_sent():
    with pytest.raises(ist.ApiUnavailableError) as info:
        Client(base_url=f"http://127.0.0.1:{unused_port()}", token=TOKEN).home()
    assert info.value.request_sent is False


def test_a_timeout_is_never_retried(api, tmp_path):
    def slow(_request):
        time.sleep(0.6)
        return STATUS

    api.route("GET", "/v1/status", slow)
    data_dir = tmp_path / "data"
    data_dir.mkdir()
    file = data_dir / "api.json"
    file.write_text(json.dumps({"port": api.port, "token": TOKEN}))
    client = Client(env={"ICESCREENTOUCH_DATA_DIR": str(data_dir)}, timeout=0.2)
    assert client.connection.token == TOKEN
    file.write_text(json.dumps({"port": api.port, "token": OTHER_TOKEN}))  # a re-discovery would find a change
    with pytest.raises(ist.ApiTimeoutError):
        client.get_status()
    time.sleep(0.5)
    assert len(api.requests) == 1


@pytest.mark.parametrize("kwargs", [
    {"max_size": 16.9}, {"max_size": True}, {"max_size": 100.0}, {"max_size": "512"},
    {"format": "jpeg", "quality": 80.5}, {"format": "jpeg", "quality": True}, {"format": "jpeg", "quality": "80"}])
def test_screenshot_sizes_that_are_not_integers_are_refused_not_truncated(api, kwargs):
    with pytest.raises(ValueError, match="integer"):
        api.client().screenshot(**kwargs)
    assert not [r for r in api.requests if r.path.startswith("/v1/screenshot")]


@pytest.mark.parametrize("value,expected", [
    ("1", 1.0), ("30", 30.0), (" 2 ", 2.0), ("9999", 60.0), ("61", 60.0),
    ("", None), ("-1", None), ("1.5", None), ("1e3", None), ("+1", None), ("12345", None),
    ("²", None), ("1³", None), ("Wed, 21 Oct 2015 07:28:00 GMT", None)])
def test_retry_after_is_read_only_from_ascii_digits_and_is_capped(api, value, expected):
    reply = problem(429, "rate_limited")
    reply.headers["Retry-After"] = value
    api.route("GET", "/v1/screenshot", reply)
    with pytest.raises(ist.RateLimitedError) as info:
        api.client().screenshot()
    assert info.value.retry_after == expected


@pytest.mark.parametrize("value,expected", [
    ("0", 0), ("90", 90), ("180", 180), ("270", 270), ("45", None), ("9999", None), ("-90", None), ("9¹", None),
    ("90.0", None), ("", None)])
def test_the_rotation_header_must_be_ascii_digits_naming_a_rotation(api, value, expected):
    api.route("GET", "/v1/screenshot", Reply(200, "image/png", png_bytes(10, 20), {
        "X-IceScreenTouch-Orientation": "portrait", "X-IceScreenTouch-Rotation": value}))
    assert api.client().screenshot().rotation == expected


@pytest.mark.parametrize("value,expected", [
    ("1179x2556", (1179, 2556)), ("1179X2556", (1179, 2556)), ("0x0", (0, 0)),
    ("1_0x5", None), ("¹x2", None), ("1x2x3", None), ("axb", None), ("-1x2", None), ("", None)])
def test_the_source_size_header_must_be_ascii_digits(api, value, expected):
    api.route("GET", "/v1/screenshot", Reply(200, "image/png", png_bytes(10, 20), {"X-IceScreenTouch-Source-Size": value}))
    assert api.client().screenshot().source_size == expected
