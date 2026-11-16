# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

import http
import json
import logging
import threading
import time

import openapi_check
import pytest
from conftest import OTHER_TOKEN, TOKEN, wait_until

websockets = pytest.importorskip("websockets")
from websockets.exceptions import ConnectionClosed  # noqa: E402
from websockets.sync.server import serve  # noqa: E402

import icescreentouch as ist  # noqa: E402
from icescreentouch import Client  # noqa: E402


# Every message the client sends and every event a fake sends is checked against docs/api/openapi.yaml (the schemas
# StreamClientMessage and StreamEvent, and the key names the real server accepts). A violation is recorded here and fails
# the test at teardown, even when the test swallowed the effect of it.
SCHEMA_ERRORS = []


@pytest.fixture(autouse=True)
def stream_contract():
    SCHEMA_ERRORS.clear()
    yield
    assert SCHEMA_ERRORS == [], "the client and the fake disagree with docs/api/openapi.yaml"


# The server side of the tests must not log frames at DEBUG either: the first frame holds the token, and the tests that
# look for the token in the log are about the client.
_QUIET_SERVER_LOGGER = logging.getLogger("tests.fake_stream_server")
_QUIET_SERVER_LOGGER.setLevel(logging.INFO)


def check_message(message):
    SCHEMA_ERRORS.extend(f"client message {message!r}: {e}" for e in openapi_check.stream_message_errors(message))


def check_event(event):
    SCHEMA_ERRORS.extend(f"server event {event!r}: {e}" for e in openapi_check.stream_event_errors(event))


class FakeStream:
    """A WebSocket server that behaves like /v1/stream: auth first, ready, ack/pong per message.

    ``hook(message, connection) -> bool`` can take over a message (return True when it handled it).
    """

    def __init__(self, hook=None, *, token=TOKEN, ready=True, process_request=None, raw_handler=None):
        self.token = token
        self.hook = hook
        self.ready = ready
        self.received = []
        self.request_headers = None
        self.connections = []
        self.closed = threading.Event()
        self._server = serve(raw_handler or self._handle, "127.0.0.1", 0, compression=None, process_request=process_request,
                             logger=_QUIET_SERVER_LOGGER)
        self.port = self._server.socket.getsockname()[1]
        self._thread = threading.Thread(target=self._server.serve_forever, daemon=True)
        self._thread.start()

    def _handle(self, connection):
        self.connections.append(connection)
        self.request_headers = connection.request.headers
        try:
            first = json.loads(connection.recv(timeout=2))
            self.received.append(first)
            check_message(first)
            if first.get("op") != "auth" or first.get("token") != self.token:
                send(connection, ev="error", code="unauthorized", detail="the token is not valid")
                connection.close(1008, "unauthorized")
                return
            if not self.ready:
                time.sleep(2)
                return
            send(connection, ev="ready", api=1)
            for raw in connection:
                message = json.loads(raw)
                self.received.append(message)
                check_message(message)
                if self.hook and self.hook(message, connection):
                    continue
                if message["op"] == "ping":
                    send(connection, ev="pong")
                else:
                    send(connection, ev="ack", seq=message["seq"])
        except ConnectionClosed:
            pass
        finally:
            self.closed.set()

    def ops(self, op):
        return [m for m in self.received if m.get("op") == op]

    def client(self):
        return Client(base_url=f"http://127.0.0.1:{self.port}", token=self.token)

    def stop(self):
        self._server.shutdown()


@pytest.fixture
def make_stream():
    servers = []

    def make(*args, **kwargs):
        server = FakeStream(*args, **kwargs)
        servers.append(server)
        return server

    yield make
    for server in servers:
        server.stop()


def send(connection, **event):
    check_event(event)
    connection.send(json.dumps(event))


# ---- Connection ----------------------------------------------------------------------------------------------

def test_auth_is_the_first_message_and_there_is_no_origin(make_stream):
    server = make_stream()
    with server.client().stream() as s:
        assert s.api_version == 1 and s.is_open
    assert server.received[0] == {"op": "auth", "token": TOKEN}
    assert "Origin" not in server.request_headers
    assert server.request_headers["Host"] == f"127.0.0.1:{server.port}"
    assert "Authorization" not in server.request_headers  # a browser cannot send it either: the token is in-band


def test_wrong_token_raises_unauthorized(make_stream):
    server = make_stream(token=OTHER_TOKEN)
    with pytest.raises(ist.UnauthorizedError) as info:
        Client(base_url=f"http://127.0.0.1:{server.port}", token=TOKEN).stream().open()
    assert TOKEN not in str(info.value)


def test_ready_timeout(make_stream):
    server = make_stream(ready=False)
    with pytest.raises(ist.ApiTimeoutError):
        server.client().stream(connect_timeout=0.3).open()


def test_refused_before_ready_maps_the_error_event(make_stream):
    def refuse(connection):
        connection.recv(timeout=2)
        send(connection, ev="error", code="busy", detail="another operation is running")
        connection.close(1013, "busy")

    server = make_stream(raw_handler=refuse)  # refuses the session like the real server does when busy
    with pytest.raises(ist.BusyError):
        server.client().stream().open()


def test_close_without_error_event_maps_the_close_code(make_stream):
    def refuse(connection):
        connection.recv(timeout=2)
        connection.close(1013, "try later")

    server = make_stream(raw_handler=refuse)
    with pytest.raises(ist.StreamClosedError) as info:
        server.client().stream().open()
    assert info.value.close_code == 1013 and info.value.reason == "try later"


def test_http_refusal_of_the_upgrade_is_mapped(make_stream):
    body = json.dumps({"type": "about:blank", "title": "Conflict", "status": 409, "code": "busy",
                       "detail": "at most 4 streams can be open at once"}).encode()

    def refuse(connection, request):
        response = connection.respond(http.HTTPStatus.CONFLICT, body.decode())
        del response.headers["Content-Type"]
        response.headers["Content-Type"] = "application/problem+json"
        return response

    server = make_stream(process_request=refuse)
    with pytest.raises(ist.BusyError):
        server.client().stream().open()


def test_nothing_listening_is_unavailable():
    import socket

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    with pytest.raises(ist.ApiUnavailableError):
        Client(base_url=f"http://127.0.0.1:{port}", token=TOKEN).stream().open()


# ---- Messages ------------------------------------------------------------------------------------------------

def test_touch_messages(make_stream):
    server = make_stream()
    with server.client().stream() as s:
        s.down(0.2, 0.5)
        s.move(0.8, 0.5)
        s.up()
    down, move, up = server.ops("down")[0], server.ops("move")[0], server.ops("up")[0]
    assert down == {"op": "down", "seq": 1, "space": "screen", "contacts": [{"id": 0, "x": 0.2, "y": 0.5}]}
    assert move == {"op": "move", "seq": 2, "contacts": [{"id": 0, "x": 0.8, "y": 0.5}]}
    assert up == {"op": "up", "seq": 3}


def test_pixel_down_and_two_fingers(make_stream):
    server = make_stream()
    with server.client().stream() as s:
        s.down_many([(0, 100, 200), (1, 200, 200)], "pixel", ref=(390, 844))
        s.move_many([(0, 90, 200), (1, 210, 200)])
        s.up()
    assert server.ops("down")[0]["space"] == "pixel"
    assert server.ops("down")[0]["ref"] == {"width": 390, "height": 844}
    assert [c["id"] for c in server.ops("down")[0]["contacts"]] == [0, 1]
    assert "space" not in server.ops("move")[0]


def test_contact_validation_is_local(make_stream):
    server = make_stream()
    with server.client().stream() as s:
        with pytest.raises(ValueError):
            s.down(0.5, 0.5, id=2)
        with pytest.raises(ValueError):
            s.down_many([(0, 0.1, 0.1), (0, 0.2, 0.2)])
        with pytest.raises(ValueError):
            s.down_many([])
    assert server.ops("down") == []


def test_key_messages(make_stream):
    server = make_stream()
    with server.client().stream() as s:
        s.key("a", True, ["cmd"])
        assert s.holding
        s.key("a", False, ["cmd"])
        assert not s.holding
    assert server.ops("key")[0] == {"op": "key", "seq": 1, "key": "a", "modifiers": ["cmd"], "down": True}
    assert server.ops("key")[1]["down"] is False


def test_ping_waits_for_the_pong(make_stream):
    server = make_stream()
    with server.client().stream() as s:
        s.ping()
    assert server.ops("ping") == [{"op": "ping"}]


def test_missing_ack_times_out(make_stream):
    server = make_stream(hook=lambda m, c: m["op"] == "move")
    with server.client().stream(ack_timeout=0.3) as s:
        s.down(0.5, 0.5)
        with pytest.raises(ist.ApiTimeoutError):
            s.move(0.6, 0.5)


# ---- Errors, interruptions, closes -----------------------------------------------------------------------------

def test_error_event_raises_the_mapped_exception(make_stream):
    def refuse(message, connection):
        if message["op"] == "down":
            send(connection, ev="error", seq=message["seq"], code="no_picture", detail="there is no picture of the device")
            return True
        return False

    server = make_stream(hook=refuse)
    with server.client().stream() as s:
        with pytest.raises(ist.NoPictureError) as info:
            s.down(0.5, 0.5)
        assert info.value.status == 409 and info.value.detail == "there is no picture of the device"
        assert not s.holding
        s.ping()  # the session is still usable


def test_error_of_a_call_that_did_not_wait_is_raised_by_the_next_call(make_stream):
    def refuse(message, connection):
        if message["op"] == "move":
            send(connection, ev="error", seq=message["seq"], code="invalid_request", detail="no finger is down: send down first")
            return True
        return False

    server = make_stream(hook=refuse)
    with server.client().stream() as s:
        s.move(0.5, 0.5, wait=False)
        assert wait_until(lambda: len(server.ops("move")) == 1)
        time.sleep(0.1)
        with pytest.raises(ist.InvalidRequestError):
            s.ping()
        s.ping()  # raised once


def test_user_interruption_callback_and_exception(make_stream):
    reasons = []

    def take_over(message, connection):
        if message["op"] == "move":
            send(connection, ev="interrupted", reason="user")
            connection.close(1000, "interrupted")
            return True
        return False

    server = make_stream(hook=take_over)
    with server.client().stream(on_interrupted=reasons.append) as s:
        s.down(0.5, 0.5)
        with pytest.raises(ist.StreamInterruptedError) as info:
            s.move(0.6, 0.5)
        assert info.value.reason == "user"
        assert reasons == ["user"] and s.last_interruption == "user"
        with pytest.raises(ist.StreamInterruptedError):
            s.up()  # a terminal interruption stays
        assert not s.holding


def test_idle_watchdog_raises_once_and_the_session_goes_on(make_stream):
    reasons = []
    server = make_stream()
    with server.client().stream(keepalive=False, on_interrupted=reasons.append) as s:
        s.down(0.5, 0.5)
        send(server.connections[0], ev="interrupted", reason="idle")  # what the server's 1 s watchdog sends
        assert wait_until(lambda: reasons == ["idle"])
        assert not s.holding
        with pytest.raises(ist.StreamInterruptedError) as info:
            s.down(0.5, 0.5)
        assert info.value.reason == "idle"
        s.down(0.5, 0.5)  # the session stayed open
        assert s.is_open


@pytest.mark.parametrize("code,reason", [(1001, "the API is shutting down"), (1009, "message too large"),
                                         (1011, "internal error"), (1003, "text messages only"), (1008, "too many messages")])
def test_close_codes(make_stream, code, reason):
    def close(message, connection):
        connection.close(code, reason)
        return True

    server = make_stream(hook=close)
    with server.client().stream() as s:
        with pytest.raises(ist.StreamClosedError) as info:
            s.down(0.5, 0.5)
        assert info.value.close_code == code and info.value.reason == reason
        with pytest.raises(ist.StreamClosedError):
            s.ping()


def test_error_event_before_close_wins_over_the_close_code(make_stream):
    def rate_limit(message, connection):
        send(connection, ev="error", code="rate_limited", detail="too many messages per second")
        connection.close(1008, "too many messages")
        return True

    server = make_stream(hook=rate_limit)
    with server.client().stream() as s:
        with pytest.raises(ist.RateLimitedError):
            s.down(0.5, 0.5)


def test_server_stopping_event_then_1001(make_stream):
    def stopping(message, connection):
        send(connection, ev="interrupted", reason="cancelled")
        connection.close(1001, "the API is shutting down")
        return True

    server = make_stream(hook=stopping)
    with server.client().stream() as s:
        with pytest.raises(ist.StreamInterruptedError) as info:
            s.up()
        assert info.value.reason == "cancelled"


def test_closing_the_stream_closes_the_connection(make_stream):
    server = make_stream()
    with server.client().stream() as s:
        s.down(0.5, 0.5)
    assert server.closed.wait(3)
    with pytest.raises(ist.StreamClosedError):
        s.up()


# ---- Keepalive -------------------------------------------------------------------------------------------------

def test_keepalive_pings_while_a_finger_is_down(make_stream):
    server = make_stream()
    with server.client().stream(keepalive_interval=0.05) as s:
        assert wait_until(lambda: True)
        time.sleep(0.2)
        assert server.ops("ping") == []  # nothing held: no pings
        s.down(0.5, 0.5)
        assert wait_until(lambda: len(server.ops("ping")) >= 3)
        s.up()
        time.sleep(0.15)
        count = len(server.ops("ping"))
        time.sleep(0.3)
        assert len(server.ops("ping")) == count  # nothing held again: pings stop


def test_keepalive_covers_held_keys(make_stream):
    server = make_stream()
    with server.client().stream(keepalive_interval=0.05) as s:
        s.key("a", True)  # Shift is a modifier, not a key name: the real server refuses it
        assert wait_until(lambda: len(server.ops("ping")) >= 2)


def test_keepalive_can_be_turned_off(make_stream):
    server = make_stream()
    with server.client().stream(keepalive=False, keepalive_interval=0.05) as s:
        s.down(0.5, 0.5)
        time.sleep(0.4)
    assert server.ops("ping") == []


def test_default_keepalive_stays_under_the_one_second_watchdog():
    import inspect

    from icescreentouch.stream import Stream

    assert inspect.signature(Stream).parameters["keepalive_interval"].default < 0.5


# ---- The token never reaches the log ---------------------------------------------------------------------------------

def test_the_token_is_not_logged_even_at_debug(make_stream, caplog):
    """websockets logs every frame at DEBUG, and the first frame is the auth message with the token."""
    for name in (None, "websockets", "websockets.client", "icescreentouch", "icescreentouch.websocket"):
        logging.getLogger(name).setLevel(logging.DEBUG)
    caplog.set_level(logging.DEBUG)
    server = make_stream()
    try:
        with server.client().stream() as s:
            s.down(0.5, 0.5)
            s.up()
    finally:
        for name in ("websockets", "websockets.client", "icescreentouch", "icescreentouch.websocket"):
            logging.getLogger(name).setLevel(logging.NOTSET)
    assert server.ops("auth")  # the auth message really was sent
    assert TOKEN not in caplog.text
    assert not [r for r in caplog.records if TOKEN in r.getMessage() or TOKEN in str(r.args)]


def test_the_websocket_logger_refuses_debug():
    from icescreentouch.stream import _WS_LOGGER

    _WS_LOGGER.setLevel(logging.DEBUG)
    try:
        assert not _WS_LOGGER.isEnabledFor(logging.DEBUG)
        assert _WS_LOGGER.isEnabledFor(logging.ERROR)
    finally:
        _WS_LOGGER.setLevel(logging.NOTSET)


# ---- A release is sent even when an error is pending -------------------------------------------------------------

def refuse_moves(message, connection):
    if message["op"] == "move":
        send(connection, ev="error", seq=message["seq"], code="invalid_request", detail="no finger is down: send down first")
        return True
    return False


def test_up_is_sent_before_the_pending_error_is_raised(make_stream):
    server = make_stream(hook=refuse_moves)
    with server.client().stream(keepalive_interval=0.05) as s:
        s.down(0.5, 0.5)
        s.move(0.6, 0.5, wait=False)
        assert wait_until(lambda: s._pending_exc is not None)
        with pytest.raises(ist.InvalidRequestError):
            s.up()
        assert len(server.ops("up")) == 1  # the finger was lifted
        assert not s.holding
        s.ping()  # the error was raised once
        count = len(server.ops("ping"))
        time.sleep(0.3)
        assert len(server.ops("ping")) == count  # and the keepalive stopped: nothing is held any more


def test_key_release_is_sent_before_the_pending_error_is_raised(make_stream):
    server = make_stream(hook=refuse_moves)
    with server.client().stream() as s:
        s.key("a", True)
        s.down(0.5, 0.5)
        s.move(0.6, 0.5, wait=False)
        assert wait_until(lambda: s._pending_exc is not None)
        with pytest.raises(ist.InvalidRequestError):
            s.key("a", False)
        assert [m["down"] for m in server.ops("key")] == [True, False]
        s.up()
        assert not s.holding


def test_a_pending_error_still_stops_a_press(make_stream):
    server = make_stream(hook=refuse_moves)
    with server.client().stream() as s:
        s.down(0.5, 0.5)
        s.move(0.6, 0.5, wait=False)
        assert wait_until(lambda: s._pending_exc is not None)
        with pytest.raises(ist.InvalidRequestError):
            s.key("a", True)
        assert server.ops("key") == []  # a press is not sent: the caller has to look at the error first


def test_the_idle_interruption_does_not_keep_up_from_being_sent(make_stream):
    server = make_stream()
    with server.client().stream(keepalive=False) as s:
        s.down(0.5, 0.5)
        send(server.connections[0], ev="interrupted", reason="idle")
        assert wait_until(lambda: s.last_interruption == "idle")
        with pytest.raises(ist.StreamInterruptedError):
            s.up()
        assert len(server.ops("up")) == 1
        s.up()  # raised once


def test_up_after_a_closed_stream_raises_the_closure(make_stream):
    def close(message, connection):
        connection.close(1011, "internal error")
        return True

    server = make_stream(hook=close)
    with server.client().stream() as s:
        with pytest.raises(ist.StreamClosedError):
            s.down(0.5, 0.5)
        with pytest.raises(ist.StreamClosedError):
            s.up()  # nothing can be sent; the server released everything when it closed


# ---- Late answers of calls that timed out ----------------------------------------------------------------------------

def test_a_late_error_of_a_timed_out_call_does_not_hit_a_later_call(make_stream):
    held = []

    def hold_moves(message, connection):
        if message["op"] == "move":
            held.append((message["seq"], connection))  # answered late by the test
            return True
        return False

    server = make_stream(hook=hold_moves)
    with server.client().stream(ack_timeout=0.2) as s:
        s.down(0.5, 0.5)
        with pytest.raises(ist.ApiTimeoutError):
            s.move(0.6, 0.5)
        seq, connection = held[0]
        send(connection, ev="error", seq=seq, code="not_connected", detail="the device is not connected")
        s.ping()  # the pong comes after the late error: it was dropped
        s.up()  # no NotConnectedError here
        assert not s.holding


def test_a_late_ack_of_a_timed_out_call_is_dropped_and_other_seqs_still_work(make_stream):
    held = []

    def hold_first_move(message, connection):
        if message["op"] == "move" and not held:
            held.append((message["seq"], connection))
            return True
        return False

    server = make_stream(hook=hold_first_move)
    with server.client().stream(ack_timeout=0.2) as s:
        s.down(0.5, 0.5)
        with pytest.raises(ist.ApiTimeoutError):
            s.move(0.6, 0.5)
        seq, connection = held[0]
        send(connection, ev="ack", seq=seq)
        s.move(0.7, 0.5)  # a new seq is acknowledged normally
        s.up()
        assert s._late == {}  # the late ack was consumed


def test_the_answer_of_a_call_that_did_not_time_out_still_counts(make_stream):
    server = make_stream(hook=refuse_moves)
    with server.client().stream(ack_timeout=0.2) as s:
        s.down(0.5, 0.5)
        with pytest.raises(ist.InvalidRequestError):
            s.move(0.6, 0.5)  # an error within the time is raised by its own call
        assert s._late == {}


def test_a_websocket_that_cannot_be_opened_in_time_says_nothing_was_sent(make_stream, monkeypatch):
    import websockets.sync.client

    def too_slow(*args, **kwargs):
        raise TimeoutError("timed out during opening handshake")

    monkeypatch.setattr(websockets.sync.client, "connect", too_slow)
    server = make_stream()
    with pytest.raises(ist.ApiTimeoutError) as info:
        server.client().stream().open()
    assert info.value.request_sent is False
