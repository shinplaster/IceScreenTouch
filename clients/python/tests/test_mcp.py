# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

import asyncio
import io
import subprocess
import sys

import pytest

pytest.importorskip("mcp")
from PIL import Image as PilImage  # noqa: E402

import icescreentouch as ist  # noqa: E402
from icescreentouch.mcp._compat import Image, ToolError  # noqa: E402
from icescreentouch.mcp.server import INSTRUCTIONS, create_server  # noqa: E402
from icescreentouch.mcp.tools import PhoneTools  # noqa: E402
from icescreentouch.models import RotationInfo, Screenshot, Status  # noqa: E402
from conftest import STATUS  # noqa: E402


def png(width, height):
    out = io.BytesIO()
    PilImage.new("RGB", (width, height), (1, 2, 3)).save(out, format="PNG")
    return out.getvalue()


class FakePhone:
    """Records the calls the tools make; ``fail`` makes the next call raise."""

    def __init__(self, shot_size=(1024, 473)):
        self.calls = []
        self.fail = None
        self.shot_size = shot_size
        self.skipped = ""

    def _do(self, name, *args, **kwargs):
        self.calls.append((name, args, kwargs))
        if self.fail is not None:
            raise self.fail

    def screenshot(self, format="png", max_size=None, quality=None):
        self._do("screenshot", format=format, max_size=max_size)
        return Screenshot(png(*self.shot_size), "png", self.shot_size, "portrait", 0, (1179, 2556))

    def get_status(self):
        self._do("get_status")
        return Status.from_json(STATUS)

    def tap(self, x, y, space="screen", hold_ms=None, count=None, interval_ms=None, *, ref=None):
        self._do("tap", x, y, space, count=count, ref=ref)

    def long_press(self, x, y, space="screen", hold_ms=None, *, ref=None):
        self._do("long_press", x, y, space, hold_ms=hold_ms, ref=ref)

    def swipe(self, frm, to, space="screen", duration_ms=None, hold_before_ms=None, hold_after_ms=None, *, ref=None):
        self._do("swipe", frm, to, space, duration_ms=duration_ms, ref=ref)

    def pinch(self, center, start_radius, end_radius, space="screen", duration_ms=None, *, ref=None):
        self._do("pinch", center, start_radius, end_radius, space, duration_ms=duration_ms, ref=ref)

    def type_text(self, text, interval_ms=None):
        self._do("type_text", text, interval_ms)
        return self.skipped

    def press_key(self, key, modifiers=(), hold_ms=None):
        self._do("press_key", key, list(modifiers))

    def home(self):
        self._do("home")

    def set_rotation(self, landscape=None, portrait=None):
        if landscape is None and portrait is None:
            raise ist.InvalidArgumentError("set_rotation needs landscape and/or portrait")
        self._do("set_rotation", landscape, portrait)
        return RotationInfo(landscape or 90, portrait or 0)


@pytest.fixture
def phone():
    return FakePhone()


@pytest.fixture
def tools(phone):
    return PhoneTools(lambda: phone)


# ---- Coordinates -------------------------------------------------------------------------------------------

def test_pixel_without_a_screenshot_is_refused_with_advice(tools, phone):
    with pytest.raises(ToolError, match="screenshot tool first"):
        tools.tap(100, 200)
    assert phone.calls == []


def test_pixels_are_relative_to_the_latest_screenshot(tools, phone):
    tools.screenshot()
    tools.tap(500, 200)
    assert phone.calls[-1] == ("tap", (500, 200, "pixel"), {"count": None, "ref": (1024, 473)})
    phone.shot_size = (472, 1024)  # the phone rotated; the next screenshot is the new reference
    tools.screenshot()
    tools.swipe(100, 900, 100, 100)
    assert phone.calls[-1][2]["ref"] == (472, 1024)


def test_screen_space_needs_no_screenshot(tools, phone):
    tools.tap(0.5, 0.9, space="screen")
    assert phone.calls[-1] == ("tap", (0.5, 0.9, "screen"), {"count": None, "ref": None})


def test_every_touch_tool_passes_the_reference(tools, phone):
    tools.screenshot()
    tools.long_press(10, 20, duration_ms=900)
    assert phone.calls[-1] == ("long_press", (10, 20, "pixel"), {"hold_ms": 900, "ref": (1024, 473)})
    tools.pinch(500, 200, 0.1, 0.3)
    assert phone.calls[-1] == ("pinch", ((500, 200), 0.1, 0.3, "pixel"), {"duration_ms": 400, "ref": (1024, 473)})
    tools.tap(1, 2, count=2)
    assert phone.calls[-1][2]["count"] == 2


def test_unknown_space_is_refused(tools):
    with pytest.raises(ToolError, match="space"):
        tools.tap(1, 1, space="device")


# ---- Screenshot ----------------------------------------------------------------------------------------------

def test_screenshot_returns_an_image_and_the_size(tools, phone):
    image, text = tools.screenshot()
    assert isinstance(image, Image) and image._mime_type == "image/png"
    assert phone.calls[0] == ("screenshot", (), {"format": "png", "max_size": 1024})
    assert "1024x473" in text and tools.reference == (1024, 473)


def test_screenshot_is_shrunk_to_about_1024_when_the_app_did_not(phone):
    phone.shot_size = (1179, 2556)
    tools = PhoneTools(lambda: phone)
    image, text = tools.screenshot()
    shrunk = PilImage.open(io.BytesIO(image.data))
    assert max(shrunk.size) == 1024 and shrunk.size == (472, 1024)
    assert tools.reference == (472, 1024)  # the reference is the image the model sees, not the original
    assert "472x1024" in text


def test_screenshot_that_is_small_enough_is_passed_through(phone):
    phone.shot_size = (390, 844)
    tools = PhoneTools(lambda: phone)
    image, _ = tools.screenshot()
    assert PilImage.open(io.BytesIO(image.data)).size == (390, 844)
    assert tools.reference == (390, 844)


def test_failed_screenshot_keeps_the_old_reference(tools, phone):
    tools.screenshot()
    phone.fail = ist.NoPictureError("no_picture", code="no_picture")
    with pytest.raises(ToolError, match="receiver window"):
        tools.screenshot()
    assert tools.reference == (1024, 473)


# ---- Errors the model must understand ------------------------------------------------------------------------

def test_picture_changed_asks_for_a_new_screenshot(tools, phone):
    tools.screenshot()
    phone.fail = ist.PictureChangedError("picture_changed", code="picture_changed")
    with pytest.raises(ToolError, match="Take a new screenshot"):
        tools.tap(10, 10)


@pytest.mark.parametrize("error", [ist.UserInterruptedError("x", code="user_interrupted"),
                                   ist.UserActiveError("x", code="user_active")])
def test_user_priority_errors_say_not_to_retry_blindly(tools, phone, error):
    phone.fail = error
    with pytest.raises(ToolError) as info:
        tools.home()
    text = str(info.value)
    assert "priority" in text and "do not" in text.lower() and "screenshot" in text


@pytest.mark.parametrize("error,word", [
    (ist.BusyError("x", code="busy"), "Another operation"),
    (ist.NotConnectedError("x", code="not_connected"), "Bluetooth"),
    (ist.NotRunningError("x", code="not_running"), "Mirroring"),
    (ist.PurchaseRequiredError("x", code="purchase_required"), "purchased"),
    (ist.ScreenshotUnsupportedError("x", code="not_found"), "Update the app"),
    (ist.InvalidRequestError("x", code="invalid_request", detail="position is outside the screenshot"), "outside the screenshot"),
    (ist.ApiUnavailableError("could not be reached"), "switch on Settings > Automation API"),
    (ist.RateLimitedError("x", code="rate_limited"), "at most 10 screenshots in any second"),
])
def test_other_errors_are_explained(tools, phone, error, word):
    phone.fail = error
    with pytest.raises(ToolError, match=word):
        tools.home()


def test_discovery_errors_are_relayed_once_without_a_repeated_hint(tools, phone):
    message = ("IceScreen Touch's connection file (api.json) was not found. Start the app and switch on "
               "Settings > Automation API, or set ICESCREENTOUCH_API and ICESCREENTOUCH_TOKEN.")
    phone.fail = ist.ConnectionDiscoveryError(message)
    with pytest.raises(ToolError) as info:
        tools.home()
    assert str(info.value) == message  # not followed by the same advice again
    assert str(info.value).count("Automation API") == 1


def test_a_stale_connection_is_relayed_as_it_is(tools, phone):
    phone.fail = ist.StaleConnectionError("the process that listens on port 1 is not IceScreenTouch.exe; the token was not sent")
    with pytest.raises(ToolError, match="token was not sent"):
        tools.home()


def test_a_timeout_does_not_mention_cancel_and_says_to_check(tools, phone):
    phone.fail = ist.ApiTimeoutError("IceScreen Touch did not answer in time (an operation that already started may still be running; call cancel())")
    with pytest.raises(ToolError) as info:
        tools.swipe(1, 2, 3, 4, space="screen")
    text = str(info.value)
    assert "cancel" not in text.lower() and "may still be running" in text and "screenshot" in text


def test_a_connection_that_broke_mid_request_says_the_action_may_have_run(tools, phone):
    phone.fail = ist.ApiUnavailableError("the connection broke")
    with pytest.raises(ToolError, match="may have run"):
        tools.home()
    refused = ist.ApiUnavailableError("refused")
    refused.request_sent = False
    phone.fail = refused
    with pytest.raises(ToolError) as info:
        tools.home()
    assert "may have run" not in str(info.value)


def test_a_rate_limited_screenshot_says_the_real_limit_and_when_to_retry(tools, phone):
    phone.fail = ist.RateLimitedError("rate_limited: at most 10 screenshots per second", code="rate_limited", status=429)
    with pytest.raises(ToolError) as info:
        tools.screenshot()
    text = str(info.value)
    assert "at most 10 screenshots in any second" in text and "Wait about a second" in text
    assert tools.reference is None  # nothing to point at

    error = ist.RateLimitedError("x", code="rate_limited", status=429)
    error.retry_after = 1.0
    phone.fail = error
    with pytest.raises(ToolError, match="Wait 1 second and"):
        tools.screenshot()
    error.retry_after = 3.0
    with pytest.raises(ToolError, match="Wait 3 seconds and"):
        tools.screenshot()


def test_bad_arguments_become_tool_errors(tools):
    with pytest.raises(ToolError, match="landscape"):
        tools.set_rotation()


def test_client_is_created_lazily():
    made = []
    tools = PhoneTools(lambda: made.append(1) or FakePhone())
    assert made == []
    tools.home()
    tools.home()
    assert made == [1]


# ---- The other tools -----------------------------------------------------------------------------------------

def test_status_is_json_ready(tools):
    status = tools.get_status()
    assert status["picture"] == {"width": 390, "height": 844, "orientation": "portrait"} and status["running"] is True


def test_type_text_reports_skipped_characters(tools, phone):
    assert tools.type_text("hello") == "Typed."
    phone.skipped = "あい"
    assert "あい" in tools.type_text("helloあい")


def test_press_key_and_rotation(tools, phone):
    tools.press_key("a", ["cmd"])
    assert phone.calls[-1] == ("press_key", ("a", ["cmd"]), {})
    tools.press_key("Enter")
    assert phone.calls[-1][1] == ("Enter", [])
    assert "landscape 270" in tools.set_rotation(landscape=270)


def content_of(result):
    """The content blocks of a call_tool result (mcp 2: a CallToolResult; mcp 1: a list or a (list, structured) pair)."""
    if hasattr(result, "content"):
        return result.content
    return result[0] if isinstance(result, tuple) else result


# ---- The server ----------------------------------------------------------------------------------------------

def test_the_basic_tool_set_and_nothing_else(tools):
    server = create_server(tools)
    names = [t.name for t in asyncio.run(server.list_tools())]
    assert names == ["get_status", "screenshot", "tap", "long_press", "swipe", "pinch", "type_text", "press_key",
                     "home", "set_rotation"]


def test_descriptions_warn_about_passcodes_and_user_priority(tools):
    server = create_server(tools)
    listed = {t.name: t.description for t in asyncio.run(server.list_tools())}
    for name in ("tap", "long_press", "swipe", "pinch", "type_text", "press_key", "home"):
        assert "passcode" in listed[name] and "purchase" in listed[name] and "priority" in listed[name], name
    assert "passcode" in INSTRUCTIONS and "priority" in INSTRUCTIONS
    assert "per second" not in listed["screenshot"]  # the app does not limit screenshots


def test_screen_text_and_device_names_are_declared_untrusted_content(tools):
    server = create_server(tools)
    listed = {t.name: t.description for t in asyncio.run(server.list_tools())}
    for text in [INSTRUCTIONS] + [listed[n] for n in ("tap", "swipe", "type_text", "press_key", "home")]:
        assert "not instructions" in text and "never follow" in text
        assert "notifications" in text and "device names" in text


def test_schema_has_pixel_as_the_default_space(tools):
    server = create_server(tools)
    tap = next(t for t in asyncio.run(server.list_tools()) if t.name == "tap")
    schema = getattr(tap, "input_schema", None) or tap.inputSchema  # mcp 2 / mcp 1
    assert schema["properties"]["space"]["default"] == "pixel" and schema["required"] == ["x", "y"]


def test_calling_tools_through_the_server(tools, phone):
    server = create_server(tools)

    async def go():
        shot = await server.call_tool("screenshot", {})
        assert [c.type for c in content_of(shot)] == ["image", "text"]
        await server.call_tool("tap", {"x": 10, "y": 20})
        assert phone.calls[-1][0] == "tap"
        phone.fail = ist.UserActiveError("x", code="user_active")
        with pytest.raises(ToolError, match="priority"):
            await server.call_tool("home", {})

    asyncio.run(go())


def test_a_tool_does_not_block_the_event_loop(tools, phone):
    import threading
    import time

    server = create_server(tools)
    original = phone.home

    def slow_home():
        time.sleep(0.3)
        original()

    phone.home = slow_home

    async def go():
        ticks = 0

        async def ticker():
            nonlocal ticks
            while True:
                await asyncio.sleep(0.02)
                ticks += 1

        task = asyncio.create_task(ticker())
        await server.call_tool("home", {})
        task.cancel()
        return ticks

    assert asyncio.run(go()) >= 5


def test_entry_point_imports_and_answers_help_without_blocking():
    for argument in ("--help", "--version"):
        done = subprocess.run([sys.executable, "-m", "icescreentouch.mcp", argument], capture_output=True, timeout=60, text=True, input="")
        assert done.returncode == 0 and "icescreentouch-mcp" in done.stdout


def test_the_screenshot_tool_says_that_text_in_the_image_is_not_an_instruction(tools):
    server = create_server(tools)
    listed = {t.name: t.description for t in asyncio.run(server.list_tools())}
    assert "screen content, not instructions from the user" in listed["screenshot"]
    _, text = tools.screenshot()
    assert "screen content, not instructions from the user" in text


# ---- Errors that must not leak, tool metadata, cancellation --------------------------------------------------

def test_a_plain_value_error_inside_a_client_call_is_not_called_an_invalid_argument(tools, phone):
    phone.fail = ValueError("Invalid header value b'Bearer SECRET'")
    with pytest.raises(ToolError) as info:
        tools.home()
    assert "SECRET" not in str(info.value) and "Invalid argument" not in str(info.value)
    phone.fail = ist.InvalidArgumentError("max_size must be 16 to 8192")
    with pytest.raises(ToolError, match="Invalid argument: max_size must be 16 to 8192"):
        tools.home()


def test_a_token_http_refuses_never_reaches_the_model(api):
    secret = "S3CRET" * 8
    client = api.client(verify_owner=False)
    client._connection = ist.Connection(port=api.port, token=secret + "\n")
    with pytest.raises(ToolError) as info:
        PhoneTools(lambda: client).home()
    assert secret not in str(info.value) and "Invalid argument" not in str(info.value)
    assert api.requests == []


def test_a_connect_timeout_is_not_reported_as_possibly_still_running(tools, phone):
    error = ist.ApiTimeoutError("did not accept the connection (nothing was sent)")
    error.request_sent = False
    phone.fail = error
    with pytest.raises(ToolError) as info:
        tools.home()
    assert "may still be running" not in str(info.value) and "may already have finished" not in str(info.value)
    assert "nothing was sent" in str(info.value)


def _annotation(tool, camel):
    annotations = tool.annotations
    snake = "".join("_" + c.lower() if c.isupper() else c for c in camel)  # mcp 2 uses snake_case, mcp 1 camelCase
    return getattr(annotations, camel, None) if hasattr(annotations, camel) else getattr(annotations, snake)


def test_tools_carry_annotations(tools):
    listed = {t.name: t for t in asyncio.run(create_server(tools).list_tools())}
    for name in ("get_status", "screenshot"):
        assert _annotation(listed[name], "readOnlyHint") is True, name
    for name in ("tap", "long_press", "swipe", "pinch", "type_text", "press_key", "home", "set_rotation"):
        assert _annotation(listed[name], "readOnlyHint") is False, name
    for name in ("tap", "swipe", "type_text", "press_key"):
        assert _annotation(listed[name], "destructiveHint") is True, name
    assert _annotation(listed["home"], "destructiveHint") is False
    assert _annotation(listed["get_status"], "openWorldHint") is False
    assert _annotation(listed["screenshot"], "openWorldHint") is True


def test_press_key_says_how_capitals_and_the_home_key_work(tools):
    listed = {t.name: t.description for t in asyncio.run(create_server(tools).list_tools())}
    text = listed["press_key"]
    assert "shift" in text and '"A"' in text and "base key" in text
    assert "`home` tool" in text and "keyboard keys" in text
    assert "saves" in listed["set_rotation"] and "restart" in listed["set_rotation"]


def test_parameters_have_descriptions_and_the_stated_limits(tools):
    listed = {t.name: t for t in asyncio.run(create_server(tools).list_tools())}

    def properties(name):
        tool = listed[name]
        return (getattr(tool, "input_schema", None) or tool.inputSchema)["properties"]

    tap = properties("tap")
    assert tap["x"]["description"] and tap["count"]["minimum"] == 1 and tap["count"]["maximum"] == 10
    assert properties("long_press")["duration_ms"]["maximum"] == 60000
    assert properties("type_text")["text"]["maxLength"] == 1000
    assert properties("pinch")["start_radius"]["minimum"] == 0.05 and properties("pinch")["end_radius"]["maximum"] == 0.45
    press = properties("press_key")
    assert "shift" in str(press["modifiers"]) and "cmd" in str(press["modifiers"])


def test_cancelling_a_tool_call_cancels_the_operation_on_the_phone(tools, phone):
    import threading

    started, release = threading.Event(), threading.Event()
    phone.cancelled = 0

    def blocking_swipe(*args, **kwargs):
        started.set()
        release.wait(5)

    def cancel():
        phone.cancelled += 1
        release.set()
        return True

    phone.swipe = blocking_swipe
    phone.cancel = cancel
    server = create_server(tools)

    async def go():
        task = asyncio.create_task(server.call_tool("swipe", {"from_x": 1, "from_y": 2, "to_x": 3, "to_y": 4,
                                                              "space": "screen"}))
        while not started.is_set():
            await asyncio.sleep(0.01)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task

    asyncio.run(go())
    release.set()
    assert phone.cancelled == 1


def test_cancelling_a_read_only_tool_does_not_cancel_anything(tools, phone):
    import threading

    started, release = threading.Event(), threading.Event()
    phone.cancelled = 0
    phone.cancel = lambda: setattr(phone, "cancelled", phone.cancelled + 1)

    def blocking_status():
        started.set()
        release.wait(5)
        return Status.from_json(STATUS)

    phone.get_status = blocking_status
    server = create_server(tools)

    async def go():
        task = asyncio.create_task(server.call_tool("get_status", {}))
        while not started.is_set():
            await asyncio.sleep(0.01)
        task.cancel()
        with pytest.raises(asyncio.CancelledError):
            await task

    asyncio.run(go())
    release.set()
    assert phone.cancelled == 0


def test_cancel_without_a_client_or_with_a_failing_client_never_raises():
    PhoneTools(lambda: FakePhone()).cancel_running()  # no client made yet

    class Broken(FakePhone):
        def cancel(self):
            raise ist.ApiUnavailableError("gone")

        def home(self, *args, **kwargs):
            broken.cancel_running()  # while this call is in flight, so the cancel is really sent

    broken = PhoneTools(lambda: Broken())
    broken.home()
    broken.cancel_running()


def test_cancel_is_only_sent_while_a_call_of_this_server_is_in_flight():
    sent = []

    class Phone(FakePhone):
        def cancel(self):
            sent.append(1)
            return True

        def home(self, *args, **kwargs):
            tools.cancel_running()  # inside the client: sent

    tools = PhoneTools(lambda: Phone())
    tools.cancel_running()  # nothing started yet (no client)
    assert sent == []
    tools.home()
    assert sent == [1]
    tools.cancel_running()  # the call has finished: the app's cancel would stop somebody else's operation
    assert sent == [1]
    tools.get_status()
    tools.cancel_running()
    assert sent == [1]
