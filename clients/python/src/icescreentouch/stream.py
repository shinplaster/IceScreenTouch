# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""The WebSocket session for low-level control (docs/API-DESIGN.md, chapter 8). Needs the ``stream`` extra.

::

    with Client() as phone:
        with phone.stream() as s:
            s.down(0.2, 0.5)
            s.move(0.8, 0.5)
            s.up()

While a session is open it owns the device: every REST operation of other clients answers ``busy``. Closing the
session (or a broken connection) lifts every finger and releases every key.

Dead-man watchdog. The server lifts everything and sends ``interrupted: idle`` when a finger or key is down and no
message arrives for one second. By default this class sends a ``ping`` every 0.25 s while something is held, so the
watchdog does not fire; pass ``keepalive=False`` to turn that off (the session then stays open after the watchdog).

Failures. A server ``error`` for one message raises that API error from the call (``wait=True``, the default: the call
waits for the ``ack``). With ``wait=False`` the call returns at once and the error is raised by the next call, except
that a release (``up()``, ``key(..., down=False)``) is always sent first and then raises it. A call that times out
waiting for its ``ack`` raises :class:`~icescreentouch.errors.ApiTimeoutError`; a late ``ack`` or ``error`` for it is
dropped and never shows up in an unrelated later call. An
``interrupted`` event (``user``, ``cancelled``, ``session_ended``, ``idle``) calls ``on_interrupted(reason)`` from the
reader thread and raises :class:`~icescreentouch.errors.StreamInterruptedError` from the next call (every call after a
terminal reason, once after ``idle``). Close codes map to :class:`~icescreentouch.errors.StreamClosedError`, or to the
API error the server named just before closing (``busy`` and 1013, ``unauthorized`` and 1008, ...).
"""

from __future__ import annotations

import json
import logging
import threading
from collections import deque
from typing import Any, Callable, Deque, Dict, Iterable, Optional, Tuple

from . import discovery
from .client import Client, _problem, _space_fields
from .discovery import Connection
from .errors import (
    ApiError,
    ApiTimeoutError,
    ApiUnavailableError,
    StreamClosedError,
    StreamInterruptedError,
    closed_error,
    error_for_code,
)

Contact = Tuple[int, float, float]  # (id, x, y)

# How many timed-out sequence numbers are remembered so that their late answers can be dropped.
_MAX_LATE = 1024


class _NoDebugLogger(logging.Logger):
    """The logger handed to ``websockets``: DEBUG is never enabled, whatever the logging configuration says.

    At DEBUG the library logs every frame it sends, and the first frame is ``{"op": "auth", "token": ...}``: the bearer
    token would end up in the application's logs. This logger refuses to be enabled for DEBUG (levels above it work
    as usual and propagate to ``icescreentouch``).
    """

    def isEnabledFor(self, level: int) -> bool:
        return level > logging.DEBUG and super().isEnabledFor(level)


_WS_LOGGER = _NoDebugLogger("icescreentouch.websocket")
_WS_LOGGER.parent = logging.getLogger("icescreentouch")


class _Waiter:
    def __init__(self) -> None:
        self.event = threading.Event()
        self.error: Optional[Exception] = None

    def finish(self, error: Optional[Exception] = None) -> None:
        self.error = error
        self.event.set()


class Stream:
    """One WebSocket session. Create it with :meth:`Client.stream` and use it as a context manager.

    Args:
        client: The client whose connection (port, token) is used.
        on_interrupted: Called with the reason (``"user"``, ``"cancelled"``, ``"session_ended"``, ``"idle"``) from the
            reader thread when an ``interrupted`` event arrives. Keep it short and do not call the stream from it.
        keepalive: Send a ``ping`` while a finger or key is held (see the module text).
        keepalive_interval: Seconds between those pings (must stay well under 1).
        connect_timeout: Seconds for the connection, the ``auth`` and the ``ready`` together.
        ack_timeout: Seconds a call waits for its ``ack``.
    """

    def __init__(self, client: Client, *, on_interrupted: Optional[Callable[[str], None]] = None,
                 keepalive: bool = True, keepalive_interval: float = 0.25, connect_timeout: float = 5.0,
                 ack_timeout: float = 5.0) -> None:
        self._client = client
        self._on_interrupted = on_interrupted
        self._keepalive = keepalive
        self._keepalive_interval = keepalive_interval
        self._connect_timeout = connect_timeout
        self._ack_timeout = ack_timeout

        self._ws: Any = None
        self._lock = threading.Lock()
        self._send_lock = threading.Lock()
        self._seq = 0
        self._waiters: Dict[int, _Waiter] = {}
        self._pings: Deque[Optional[_Waiter]] = deque()
        self._stop = threading.Event()
        self._reader: Optional[threading.Thread] = None
        self._keeper: Optional[threading.Thread] = None
        self._closing = False
        self._closed_exc: Optional[Exception] = None
        self._pending_exc: Optional[Exception] = None
        # Sequence numbers whose call timed out waiting for the answer: a late ack or error for one of them is dropped,
        # not reported by an unrelated later call.
        self._late: Dict[int, None] = {}
        self._last_error: Optional[ApiError] = None
        self._touching = False
        self._keys: set = set()
        self.api_version: Optional[int] = None
        self.last_interruption: Optional[str] = None

    # ---- Lifecycle -----------------------------------------------------------------------------------------

    def __enter__(self) -> "Stream":
        return self.open()

    def __exit__(self, *exc: object) -> None:
        self.close()

    def __repr__(self) -> str:
        return f"Stream({'open' if self.is_open else 'closed'})"

    @property
    def is_open(self) -> bool:
        return self._ws is not None and self._closed_exc is None and not self._closing

    def open(self) -> "Stream":
        """Connects, authenticates (first message) and waits for ``ready``."""
        if self._ws is not None:
            raise RuntimeError("the stream is already open")
        self._client._attempt(self._connect)
        self._reader = threading.Thread(target=self._read_loop, name="icescreentouch-stream", daemon=True)
        self._reader.start()
        if self._keepalive:
            self._keeper = threading.Thread(target=self._keepalive_loop, name="icescreentouch-keepalive", daemon=True)
            self._keeper.start()
        return self

    def close(self) -> None:
        """Closes the session. The server lifts every finger and releases every key."""
        self._closing = True
        self._stop.set()
        ws = self._ws
        if ws is not None:
            try:
                ws.close()
            except Exception:  # noqa: BLE001 - closing is best effort
                pass
        for thread in (self._reader, self._keeper):
            if thread is not None and thread is not threading.current_thread():
                thread.join(timeout=3.0)
        self._finish(StreamClosedError("the stream was closed", close_code=1000))

    def _connect(self, connection: Connection) -> None:
        try:
            from websockets.exceptions import ConnectionClosed, InvalidStatus, WebSocketException
            from websockets.sync.client import connect
        except ImportError as exc:  # pragma: no cover - depends on the environment
            raise ImportError("Client.stream() needs websockets: pip install icescreentouch[stream]") from exc

        url = f"ws://{discovery.HOST}:{connection.port}/v1/stream"
        try:
            # No Origin (the library sends none unless asked), Host is 127.0.0.1:<port>, no compression (the server
            # offers none), and proxy=None: the token must never be routed through a system proxy.
            options: Dict[str, Any] = dict(open_timeout=self._connect_timeout, compression=None, proxy=None,
                                           ping_interval=None, close_timeout=2.0, max_size=64 * 1024,
                                           logger=_WS_LOGGER)
            try:
                # websockets 17 wants "with connect(...)" or legacy=True for a connection we close ourselves; older
                # versions do not know the flag and reject it before they connect.
                ws = connect(url, legacy=True, **options)
            except TypeError:
                ws = connect(url, **options)
        except InvalidStatus as exc:
            response = exc.response
            raise _problem(response.status_code, response.headers.get("Content-Type", ""), response.body or b"") from None
        except TimeoutError:
            raise ApiTimeoutError("IceScreen Touch did not accept the WebSocket in time") from None
        except (OSError, WebSocketException) as exc:
            # Opening a session is not an operation: nothing has run, so a retry (after a new discovery) is safe.
            unavailable = ApiUnavailableError(
                f"IceScreen Touch could not be reached on 127.0.0.1:{connection.port} ({type(exc).__name__}): "
                "is the app running with the Automation API switched on?")
            unavailable.request_sent = False
            raise unavailable from None

        try:
            # The token is the first message (a browser cannot set a header); the server wants it within 2 s.
            ws.send(json.dumps({"op": "auth", "token": connection.token}))
            raw = ws.recv(timeout=self._connect_timeout)
            event = _parse_event(raw)
            if event is None or event.get("ev") not in ("ready", "error"):
                raise StreamClosedError("the server did not answer the authentication as expected")
            if event["ev"] == "error":
                error = _error_of(event)
                # The close frame follows the error; its code adds nothing the error does not say.
                raise error
            api = event.get("api")
            self.api_version = api if isinstance(api, int) else None
        except ConnectionClosed as exc:
            rcvd = exc.rcvd
            raise closed_error(rcvd.code if rcvd else None, rcvd.reason if rcvd else "", None) from None
        except TimeoutError:
            ws.close()
            raise ApiTimeoutError("IceScreen Touch did not answer the WebSocket authentication in time") from None
        except BaseException:
            try:
                ws.close()
            except Exception:  # noqa: BLE001
                pass
            raise
        self._ws = ws

    # ---- Operations ----------------------------------------------------------------------------------------

    def down(self, x: float, y: float, id: int = 0, space: str = "screen", *, ref: Any = None, wait: bool = True) -> None:
        """Puts finger ``id`` (0 or 1) down at ``(x, y)``, or moves it if it is already down.

        ``space`` and ``ref`` are as for :meth:`Client.tap`; they apply to this and the following ``move`` calls.
        """
        self.down_many([(id, x, y)], space, ref=ref, wait=wait)

    def down_many(self, contacts: Iterable[Contact], space: str = "screen", *, ref: Any = None, wait: bool = True) -> None:
        """Puts one or two fingers down; ``contacts`` are ``(id, x, y)`` with ids 0 and 1 (two fingers: a pinch)."""
        message = {"op": "down", **_space_fields(space, ref), "contacts": _contacts(contacts)}
        self._request(message, wait)
        with self._lock:
            self._touching = True

    def move(self, x: float, y: float, id: int = 0, *, wait: bool = True) -> None:
        """Moves finger ``id`` (it must be down). The server sends at most one report per 16 ms and acknowledges the
        moves it merged, so a loop of ``move`` calls is paced by the server."""
        self.move_many([(id, x, y)], wait=wait)

    def move_many(self, contacts: Iterable[Contact], *, wait: bool = True) -> None:
        """Moves one or two fingers that are down."""
        self._request({"op": "move", "contacts": _contacts(contacts)}, wait)

    def up(self, *, wait: bool = True) -> None:
        """Lifts every finger at its last position.

        A release is always sent, even when an earlier error is waiting to be raised (a ``wait=False`` call that
        failed, or the ``idle`` watchdog); that error is raised after the lift was sent.
        """
        self._request({"op": "up"}, wait, release=self._release_touch)

    def _release_touch(self) -> None:
        with self._lock:
            self._touching = False

    def key(self, key: str, down: bool = True, modifiers: Iterable[str] = (), *, wait: bool = True) -> None:
        """Presses (``down=True``) or releases a key by its device-side name (up to 6 keys can be held).

        Like :meth:`up`, a release is sent even when an earlier error is waiting to be raised.
        """
        message = {"op": "key", "key": key, "modifiers": list(modifiers), "down": bool(down)}
        if down:
            self._request(message, wait)
            with self._lock:
                self._keys.add(key.lower())
        else:
            self._request(message, wait, release=lambda: self._release_key(key))

    def _release_key(self, key: str) -> None:
        with self._lock:
            self._keys.discard(key.lower())

    def ping(self, *, wait: bool = True) -> None:
        """Resets the server's 1 s watchdog; with ``wait`` returns when the ``pong`` arrives."""
        self._raise_pending()
        waiter = _Waiter() if wait else None
        self._send({"op": "ping"}, ping=waiter)
        self._await(waiter, "pong")

    @property
    def holding(self) -> bool:
        """A finger or key is down (as far as this client knows)."""
        with self._lock:
            return self._touching or bool(self._keys)

    # ---- Machinery -----------------------------------------------------------------------------------------

    def _request(self, message: Dict[str, Any], wait: bool, *, release: Optional[Callable[[], None]] = None) -> None:
        """Sends ``message`` (and waits for its ack). ``release`` marks a message that lifts a finger or releases a key:
        it is sent even when an earlier error is pending (the finger or key must not stay held), ``release()`` updates
        the held state once the message is out, and the pending error is raised afterwards."""
        deferred = self._raise_pending(defer=release is not None)
        waiter = _Waiter() if wait else None
        with self._lock:
            self._seq += 1
            seq = self._seq
            if waiter is not None:
                self._waiters[seq] = waiter
        message["seq"] = seq
        try:
            self._send(message)
        except BaseException:
            with self._lock:
                self._waiters.pop(seq, None)
            raise
        if release is not None:
            release()
        try:
            if waiter is not None:
                self._await_ack(waiter, seq)
        except BaseException:
            if deferred is not None:
                with self._lock:
                    self._pending_exc = self._pending_exc or deferred  # not lost: the next call reports it
            raise
        if deferred is not None:
            raise deferred

    def _await_ack(self, waiter: _Waiter, seq: int) -> None:
        if not waiter.event.wait(self._ack_timeout):
            with self._lock:
                abandoned = self._waiters.pop(seq, None) is not None
                if abandoned:
                    self._late[seq] = None
                    while len(self._late) > _MAX_LATE:
                        del self._late[next(iter(self._late))]
            if abandoned:
                raise ApiTimeoutError(f"no ack from IceScreen Touch within {self._ack_timeout:g} s")
            waiter.event.wait(1.0)  # the answer arrived just now; the reader is finishing the waiter
        if waiter.error is not None:
            raise waiter.error

    def _await(self, waiter: Optional[_Waiter], what: str) -> None:
        if waiter is None:
            return
        if not waiter.event.wait(self._ack_timeout):
            raise ApiTimeoutError(f"no {what} from IceScreen Touch within {self._ack_timeout:g} s")
        if waiter.error is not None:
            raise waiter.error

    def _send(self, message: Dict[str, Any], ping: Optional[_Waiter] = None) -> None:
        text = json.dumps(message, allow_nan=False, ensure_ascii=False)
        ws = self._ws
        if ws is None:
            raise StreamClosedError("the stream is not open")
        with self._send_lock:
            if message.get("op") == "ping":
                # Pongs come back in order: the queue pairs each with the call that waits for it (or None).
                with self._lock:
                    self._pings.append(ping)
            try:
                ws.send(text)
            except Exception:  # noqa: BLE001 - the reader reports why the connection ended
                with self._lock:
                    if message.get("op") == "ping" and self._pings:
                        self._pings.pop()
                    closed = self._closed_exc
                raise closed or StreamClosedError("the connection was lost") from None

    def _raise_pending(self, defer: bool = False) -> Optional[Exception]:
        """Raises the stream's pending failure. With ``defer`` a pending (not a closing) error is returned instead, for
        the caller to raise after it sent a release."""
        with self._lock:
            if self._closed_exc is not None:
                raise self._closed_exc
            if self._pending_exc is not None:
                exc, self._pending_exc = self._pending_exc, None
                if defer:
                    return exc
                raise exc
        return None

    def _finish(self, exc: Exception) -> None:
        """The stream ended: every waiting call fails with ``exc`` (the first reason stays)."""
        with self._lock:
            if self._closed_exc is not None:
                return
            self._closed_exc = exc
            self._touching = False
            self._keys.clear()
            self._late.clear()
            waiters = list(self._waiters.values()) + [w for w in self._pings if w is not None]
            self._waiters.clear()
            self._pings.clear()
        self._stop.set()
        for waiter in waiters:
            waiter.finish(exc)

    def _read_loop(self) -> None:
        from websockets.exceptions import ConnectionClosed

        ws = self._ws
        try:
            while True:
                event = _parse_event(ws.recv())
                if event is not None:
                    self._handle(event)
        except ConnectionClosed as exc:
            rcvd = exc.rcvd
            code = rcvd.code if rcvd else None
            reason = rcvd.reason if rcvd else ""
            with self._lock:
                last = self._last_error
            self._finish(closed_error(code, reason, last))
        except Exception:  # noqa: BLE001
            self._finish(StreamClosedError("the connection was lost"))

    def _handle(self, event: Dict[str, Any]) -> None:
        kind = event.get("ev")
        seq = event.get("seq")
        if kind in ("ack", "error") and isinstance(seq, int) and self._drop_late(seq):
            return  # the call that wanted this answer already gave up (it timed out)
        if kind == "ack" and isinstance(seq, int):
            with self._lock:
                waiter = self._waiters.pop(seq, None)
            if waiter is not None:
                waiter.finish()
        elif kind == "pong":
            with self._lock:
                waiter = self._pings.popleft() if self._pings else None
            if waiter is not None:
                waiter.finish()
        elif kind == "error":
            error = _error_of(event)
            if isinstance(seq, int):
                with self._lock:
                    waiter = self._waiters.pop(seq, None)
                    if waiter is None:
                        # An error for a call that did not wait: the next call reports it.
                        self._pending_exc = self._pending_exc or error
                if waiter is not None:
                    waiter.finish(error)
            else:
                with self._lock:
                    self._last_error = error
        elif kind == "interrupted":
            self._interrupted(str(event.get("reason", "")))

    def _drop_late(self, seq: int) -> bool:
        with self._lock:
            if seq in self._late:
                del self._late[seq]
                return True
        return False

    def _interrupted(self, reason: str) -> None:
        error = StreamInterruptedError(reason)
        self.last_interruption = reason
        callback = self._on_interrupted
        if reason != "idle":
            # The server closes the stream after this event.
            self._finish(error)
        else:
            with self._lock:
                # The watchdog lifted everything; the session goes on.
                self._touching = False
                self._keys.clear()
                waiters = list(self._waiters.values())
                self._waiters.clear()
                self._pending_exc = error
            for waiter in waiters:
                waiter.finish(error)
        if callback is not None:
            try:
                callback(reason)
            except Exception:  # noqa: BLE001 - a callback must not kill the reader
                pass

    def _keepalive_loop(self) -> None:
        while not self._stop.wait(self._keepalive_interval):
            if not self.holding:
                continue
            try:
                self._send({"op": "ping"}, None)
            except Exception:  # noqa: BLE001 - the stream is gone; the reader reports it
                return


def _contacts(contacts: Iterable[Contact]) -> list:
    listed = []
    for contact in contacts:
        id_, x, y = contact
        if id_ not in (0, 1):
            raise ValueError("a contact id must be 0 or 1")
        listed.append({"id": int(id_), "x": float(x), "y": float(y)})
    if not 1 <= len(listed) <= 2:
        raise ValueError("one or two contacts are needed")
    if len({c["id"] for c in listed}) != len(listed):
        raise ValueError("contact ids must be unique")
    return listed


def _parse_event(raw: Any) -> Optional[Dict[str, Any]]:
    if isinstance(raw, (bytes, bytearray)):
        return None
    try:
        parsed = json.loads(raw)
    except ValueError:
        return None
    return parsed if isinstance(parsed, dict) else None


def _error_of(event: Dict[str, Any]) -> ApiError:
    code = event.get("code") if isinstance(event.get("code"), str) else None
    detail = event.get("detail") if isinstance(event.get("detail"), str) else None
    return error_for_code(code, detail)
