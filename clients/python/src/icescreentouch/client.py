# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""The synchronous REST client of the IceScreen Touch external API (docs/API-DESIGN.md, chapter 7).

Every time value in this module is in milliseconds and its name ends in ``_ms``, exactly as the API names them
(``hold_ms`` is ``holdMs``). Positions are ``(x, y)`` pairs in one of three spaces:

* ``"screen"`` (default): 0..1 of the screen as it looks now (landscape if the picture is landscape);
* ``"pixel"``: pixels of a screenshot; ``ref=(width, height)`` of that screenshot is then required;
* ``"device"``: 0..1 of the device's own portrait frame (no rotation, no picture needed).

An operation call returns when the device has received everything (a swipe returns after the lift). ``busy`` and
``user_active`` are never retried here; see :mod:`icescreentouch.errors`.
"""

from __future__ import annotations

import http.client
import json
import re
import socket
from typing import Any, Callable, Dict, Iterable, Mapping, Optional, Sequence, Tuple, TypeVar
from urllib.parse import urlencode

from . import discovery
from .discovery import Connection
from .errors import (
    ApiError,
    ApiTimeoutError,
    ApiUnavailableError,
    NotFoundError,
    RateLimitedError,
    ScreenshotUnsupportedError,
    StaleConnectionError,
    UnauthorizedError,
    error_for_code,
)
from .models import RotationInfo, Screenshot, Status, image_size, parse_source_size

Point = Tuple[float, float]
Size = Tuple[int, int]
T = TypeVar("T")

_MAX_RESPONSE_BYTES = 64 * 1024 * 1024
_SPACES = ("screen", "pixel", "device")


def _size_of(ref: Any) -> Size:
    """``(width, height)`` from a pair, or from anything with ``.size`` (a PIL image or a Screenshot)."""
    pair = getattr(ref, "size", ref)
    try:
        width, height = pair
        return (int(width), int(height))
    except (TypeError, ValueError):
        raise ValueError("ref must be (width, height) of the screenshot, or an object with a .size") from None


def _space_fields(space: str, ref: Any) -> Dict[str, Any]:
    if space not in _SPACES:
        raise ValueError(f"space must be one of {_SPACES}, not {space!r}")
    fields: Dict[str, Any] = {"space": space}
    if space == "pixel":
        if ref is None:
            raise ValueError("space='pixel' needs ref=(width, height): the size of the screenshot the position refers to")
        width, height = _size_of(ref)
        fields["ref"] = {"width": width, "height": height}
    return fields


def _point(p: Point) -> Dict[str, float]:
    x, y = p
    return {"x": float(x), "y": float(y)}


def _put(body: Dict[str, Any], name: str, value: Optional[int]) -> None:
    if value is not None:
        body[name] = int(value)


_RETRY_AFTER = re.compile(r"[0-9]{1,4}")
_ROTATION_HEADER = re.compile(r"[0-9]{1,3}")
_MAX_RETRY_AFTER = 60  # seconds: the app sends 1; a larger value is not believed


def _whole(name: str, value: Any) -> int:
    """``value`` as an int, but only if it is one (a bool or a float such as 16.9 is a mistake, not 16)."""
    if isinstance(value, bool) or not isinstance(value, int):
        raise ValueError(f"{name} must be an integer")
    return value


def _header_rotation(value: Optional[str]) -> Optional[int]:
    """The rotation header as 0, 90, 180 or 270 (ASCII digits only), else None."""
    if value is None or not _ROTATION_HEADER.fullmatch(value.strip()):
        return None
    degrees = int(value.strip())
    return degrees if degrees in (0, 90, 180, 270) else None


def _ms(value: Optional[int], default: int) -> int:
    return default if value is None else int(value)


class Client:
    """A connection to the IceScreen Touch API on this PC.

    ``Client()`` finds the API itself (environment, then api.json; see :mod:`icescreentouch.discovery`). On Windows,
    before every request it checks that the process listening on the port is ``IceScreenTouch.exe`` running as the same
    Windows user (and, for api.json, the very process the file names); otherwise the token is not sent
    (``StaleConnectionError``). ``Client(base_url="http://127.0.0.1:47821", token="...")`` uses exactly that address
    and token, with the same owner check. Nothing is connected until the first call, and when the app was restarted
    (a new token or port) an automatically found connection is found again once.

    Args:
        base_url: ``http://127.0.0.1:<port>``. Needs ``token`` (or ``ICESCREENTOUCH_TOKEN``).
        token: The bearer token.
        timeout: Seconds a call may take on top of the time the operation itself needs.
        env: The environment to read (tests); default ``os.environ``.
        verify_owner: ``True`` (default) runs the owner check described above. Pass ``False`` only for a setup you
            control, such as a test server that is not ``IceScreenTouch.exe``; the token then goes to whatever listens
            on the port.
    """

    def __init__(self, base_url: Optional[str] = None, token: Optional[str] = None, *, timeout: float = 10.0,
                 env: Optional[Mapping[str, str]] = None, verify_owner: bool = True) -> None:
        self._base_url = base_url
        self._token = token
        self._env = env
        self._timeout = float(timeout)
        self._verify_owner = bool(verify_owner)
        self._explicit = base_url is not None
        self._connection: Optional[Connection] = None
        if self._explicit:
            self._connection = discovery.discover(base_url, token, env, self._verify_owner)

    def __repr__(self) -> str:
        shown = self._connection.base_url if self._connection else "not connected yet"
        return f"Client({shown})"

    def __enter__(self) -> "Client":
        return self

    def __exit__(self, *exc: object) -> None:
        self.close()

    def close(self) -> None:
        """Nothing is kept open between calls; this exists for ``with`` and for symmetry."""

    @property
    def connection(self) -> Connection:
        """The connection in use (found on first use)."""
        if self._connection is None:
            self._connection = discovery.discover(self._base_url, self._token, self._env, self._verify_owner)
        return self._connection

    # ---- Status and settings -------------------------------------------------------------------------------

    def get_status(self) -> Status:
        """State of the app: version, BLE, devices, target, receiver, picture, rotation, whether an operation runs."""
        return Status.from_json(self._json("GET", "/v1/status"))

    def get_rotation(self) -> RotationInfo:
        """The rotations the app assumes (landscape 90 or 270, portrait 0 or 180)."""
        return _rotation(self._json("GET", "/v1/rotation"))

    def set_rotation(self, landscape: Optional[int] = None, portrait: Optional[int] = None) -> RotationInfo:
        """Sets the assumed landscape (90 or 270) and/or portrait (0 or 180) rotation, saved like Ctrl+R."""
        if landscape is None and portrait is None:
            raise ValueError("set_rotation needs landscape and/or portrait")
        body: Dict[str, Any] = {}
        _put(body, "landscape", landscape)
        _put(body, "portrait", portrait)
        return _rotation(self._json("PUT", "/v1/rotation", body))

    def set_target(self, device_id: Optional[str]) -> None:
        """Chooses the device that gets the input (``None`` = automatic). Ids are in ``get_status().devices``."""
        self._json("PUT", "/v1/target", {"deviceId": device_id})

    def cancel(self) -> bool:
        """Stops the running operation (fingers and keys are lifted). True when something was running."""
        return bool(self._json("POST", "/v1/cancel").get("cancelled"))

    # ---- Touch ---------------------------------------------------------------------------------------------

    def tap(self, x: float, y: float, space: str = "screen", hold_ms: Optional[int] = None,
            count: Optional[int] = None, interval_ms: Optional[int] = None, *, ref: Any = None) -> None:
        """Taps once, or ``count`` times (1 to 10; ``interval_ms`` between the taps)."""
        body = {"x": float(x), "y": float(y), **_space_fields(space, ref)}
        _put(body, "holdMs", hold_ms)
        _put(body, "count", count)
        _put(body, "intervalMs", interval_ms)
        seconds = (_ms(count, 1) * max(_ms(hold_ms, 80), 70) + (_ms(count, 1) - 1) * _ms(interval_ms, 120)) / 1000
        self._json("POST", "/v1/touch/tap", body, op_seconds=seconds)

    def tap_pixel(self, x: float, y: float, ref: Any, hold_ms: Optional[int] = None, count: Optional[int] = None,
                  interval_ms: Optional[int] = None) -> None:
        """``tap`` in screenshot pixels. ``ref`` is the screenshot's ``(width, height)`` (or the Screenshot / PIL image)."""
        self.tap(x, y, "pixel", hold_ms, count, interval_ms, ref=ref)

    def long_press(self, x: float, y: float, space: str = "screen", hold_ms: Optional[int] = None, *,
                   ref: Any = None) -> None:
        """Holds one finger down for ``hold_ms`` (default 800)."""
        body = {"x": float(x), "y": float(y), **_space_fields(space, ref)}
        _put(body, "holdMs", hold_ms)
        self._json("POST", "/v1/touch/long-press", body, op_seconds=_ms(hold_ms, 800) / 1000)

    def swipe(self, frm: Point, to: Point, space: str = "screen", duration_ms: Optional[int] = None,
              hold_before_ms: Optional[int] = None, hold_after_ms: Optional[int] = None, *, ref: Any = None) -> None:
        """One finger from ``frm`` to ``to`` in ``duration_ms`` (default 300).

        ``hold_before_ms`` waits at ``frm`` first (a long press before the drag); ``hold_after_ms`` waits at ``to``
        before the lift (stops the scroll momentum).
        """
        body = {"from": _point(frm), "to": _point(to), **_space_fields(space, ref)}
        _put(body, "durationMs", duration_ms)
        _put(body, "holdBeforeMs", hold_before_ms)
        _put(body, "holdAfterMs", hold_after_ms)
        total = _ms(duration_ms, 300) + _ms(hold_before_ms, 0) + _ms(hold_after_ms, 0)
        self._json("POST", "/v1/touch/swipe", body, op_seconds=total / 1000)

    def path(self, points: Iterable[Sequence[float]], space: str = "screen", *, ref: Any = None) -> None:
        """One finger along ``points``, each ``(x, y, t_ms)`` with ``t_ms`` the time since the touch (not decreasing).

        At most 1000 points and 60 seconds.
        """
        listed = []
        for p in points:
            x, y, t = p
            listed.append({"x": float(x), "y": float(y), "t": int(t)})
        body = {**_space_fields(space, ref), "points": listed}
        last = max((p["t"] for p in listed), default=0)
        self._json("POST", "/v1/touch/path", body, op_seconds=last / 1000)

    def pinch(self, center: Point, start_radius: float, end_radius: float, space: str = "screen",
              duration_ms: Optional[int] = None, *, ref: Any = None) -> None:
        """Two fingers placed side by side around ``center``; the radius (half the distance between them, as a
        fraction of the screen's SHORT side) goes from ``start_radius`` to ``end_radius``. Zoom in: small to large."""
        body = {"center": _point(center), "startRadius": float(start_radius), "endRadius": float(end_radius),
                **_space_fields(space, ref)}
        _put(body, "durationMs", duration_ms)
        self._json("POST", "/v1/touch/pinch", body, op_seconds=_ms(duration_ms, 400) / 1000)

    # ---- Keys and home -------------------------------------------------------------------------------------

    def press_key(self, key: str, modifiers: Iterable[str] = (), hold_ms: Optional[int] = None) -> None:
        """Presses one key by its device-side name (``"Enter"``, ``"a"``, ``"ArrowUp"``, ``"F5"``...).

        ``modifiers``: any of ``"cmd"``, ``"shift"``, ``"option"``, ``"ctrl"``. A shifted character (``"A"``, ``"!"``)
        is the base key with ``"shift"``.
        """
        body: Dict[str, Any] = {"key": key, "modifiers": list(modifiers)}
        _put(body, "holdMs", hold_ms)
        self._json("POST", "/v1/keys/press", body, op_seconds=(_ms(hold_ms, 30) + 60) / 1000)

    def type_text(self, text: str, interval_ms: Optional[int] = None) -> str:
        """Types ``text`` one character at a time on a US keyboard layout (at most 1000 characters, 60 seconds).

        Returns the characters that have no key (for example Japanese) and were therefore not typed; it is ``""`` when
        everything was typed.
        """
        body: Dict[str, Any] = {"text": text}
        _put(body, "intervalMs", interval_ms)
        seconds = len(text) * (0.06 + _ms(interval_ms, 20) / 1000)
        return str(self._json("POST", "/v1/keys/type", body, op_seconds=seconds).get("skipped", ""))

    def home(self) -> None:
        """Presses the home button."""
        self._json("POST", "/v1/home", op_seconds=1.0)

    # ---- Screenshot ----------------------------------------------------------------------------------------

    def screenshot(self, format: str = "png", max_size: Optional[int] = None, quality: Optional[int] = None) -> Screenshot:
        """The picture of the device (the receiver's video rectangle), shrunk so the long side is at most ``max_size``.

        Args:
            format: ``"png"`` (default) or ``"jpeg"`` (``"jpg"`` is accepted here and sent as ``"jpeg"``).
            max_size: The longest side in pixels, 16 to 8192. The app only shrinks (never enlarges) and keeps the aspect;
                without it the image has the size of the receiver's picture (which follows the size of its window, not
                the resolution of the device).
            quality: JPEG quality 1 to 100 (the app's default is 90); only with ``format="jpeg"``.

        Returns:
            A :class:`Screenshot`. Its ``size`` (the size of the image you got) is what ``tap_pixel`` and friends need as
            ``ref``; ``source_size`` is the size before shrinking, ``orientation`` and ``rotation`` are how the app saw the
            picture when it was taken.

        Raises:
            ValueError: ``max_size`` or ``quality`` is not an integer (a bool or a float is refused, not truncated) or is out of
                range, or ``quality`` is given for PNG (nothing is sent).
            NoPictureError: The receiver window is closed, minimized, on another virtual desktop or not found, or it could not be read.
            NotRunningError: Mirroring is not running.
            RateLimitedError: More than 10 screenshots in a second (all clients together), or too many in progress at once; see ``retry_after``.
            ScreenshotUnsupportedError: The app is a version without the screenshot endpoint (404).

        Not an operation: it also works while a touch, a key or a stream is running, and it sends nothing to the device.
        The image is made from the receiver window alone, so other windows (notifications, overlays) do not appear in it.
        """
        fmt = {"jpg": "jpeg"}.get(format.lower(), format.lower())
        if fmt not in ("png", "jpeg"):
            raise ValueError("format must be 'png' or 'jpeg'")
        if max_size is not None and not 16 <= _whole("max_size", max_size) <= 8192:
            raise ValueError("max_size must be 16 to 8192")
        if quality is not None:
            if fmt != "jpeg":
                raise ValueError("quality only applies to format='jpeg'")
            if not 1 <= _whole("quality", quality) <= 100:
                raise ValueError("quality must be 1 to 100")
        query: Dict[str, Any] = {"format": fmt}
        _put(query, "maxSize", max_size)
        _put(query, "quality", quality)
        try:
            status, headers, data = self._call("GET", f"/v1/screenshot?{urlencode(query)}", None, 5.0, "image/png, image/jpeg, application/problem+json")
        except NotFoundError as exc:
            if type(exc) is NotFoundError:
                raise ScreenshotUnsupportedError(
                    "this version of IceScreen Touch has no screenshot endpoint (GET /v1/screenshot); update the app",
                    code="not_found", status=404, detail=exc.detail) from None
            raise
        content_type = (headers.get("Content-Type") or "").split(";")[0].strip().lower()
        shown = "jpeg" if content_type == "image/jpeg" else "png" if content_type == "image/png" else fmt
        source = parse_source_size(headers.get("X-IceScreenTouch-Source-Size"))
        size = image_size(data) or source or (0, 0)
        rotation = headers.get("X-IceScreenTouch-Rotation")
        return Screenshot(
            data=data,
            format=shown,
            size=size,
            orientation=headers.get("X-IceScreenTouch-Orientation"),
            rotation=_header_rotation(rotation),
            source_size=source,
        )

    # ---- WebSocket -----------------------------------------------------------------------------------------

    def stream(self, **options: Any) -> "Any":
        """A WebSocket session for low-level control (needs the ``stream`` extra). Use it as a context manager.

        See :class:`icescreentouch.stream.Stream` for the options and the operations.
        """
        from .stream import Stream

        return Stream(self, **options)

    # ---- Transport -----------------------------------------------------------------------------------------

    def _json(self, method: str, path: str, body: Optional[Mapping[str, Any]] = None, *, op_seconds: float = 0.0) -> Dict[str, Any]:
        payload = None if body is None else json.dumps(body, allow_nan=False, ensure_ascii=False).encode("utf-8")
        _status, _headers, data = self._call(method, path, payload, op_seconds, "application/json, application/problem+json")
        try:
            parsed = json.loads(data.decode("utf-8")) if data else {}
        except ValueError:
            raise ApiError("the API answered with something that is not JSON") from None
        if not isinstance(parsed, dict):
            raise ApiError("the API answered with an unexpected JSON value")
        return parsed

    def _call(self, method: str, path: str, payload: Optional[bytes], op_seconds: float, accept: str):
        return self._attempt(lambda connection: self._exchange(connection, method, path, payload, op_seconds, accept))

    def _attempt(self, action: Callable[[Connection], T]) -> T:
        """Runs ``action(connection)`` after the owner check. If the connection was found automatically and the call
        failed in a way that proves nothing was executed, looks for the connection again once (the app may have been
        restarted: a new token, port or pid) and retries when it changed.

        Retried: a 401 ``unauthorized`` (the app refused before doing anything), a failed owner check, and an
        ``ApiUnavailableError`` whose ``request_sent`` is False (the connection was refused: nothing was sent). NOT
        retried: a timeout, or a connection that broke after the request was written (the operation may have run:
        repeating it could tap twice).
        """
        connection = self.connection
        try:
            if self._verify_owner:
                discovery.check_owner(connection)
            return action(connection)
        except (UnauthorizedError, StaleConnectionError, ApiUnavailableError) as exc:
            if self._explicit or isinstance(exc, ApiTimeoutError):
                raise
            if isinstance(exc, ApiUnavailableError) and exc.request_sent:
                raise
            try:
                fresh = discovery.discover(self._base_url, self._token, self._env, self._verify_owner)
            except Exception:  # noqa: BLE001 - keep the original failure
                raise exc from None
            if fresh == connection:
                raise
            self._connection = fresh
            return action(fresh)

    def _exchange(self, connection: Connection, method: str, path: str, payload: Optional[bytes], op_seconds: float,
                  accept: str):
        # Host is "127.0.0.1:<port>" (what http.client builds) and there is never an Origin header.
        http_connection = http.client.HTTPConnection(discovery.HOST, connection.port, timeout=self._timeout + op_seconds)
        headers = {
            "Authorization": f"Bearer {connection.token}",
            "Accept": accept,
            "Connection": "close",
            "User-Agent": "icescreentouch-python",
        }
        if payload is not None:
            headers["Content-Type"] = "application/json; charset=utf-8"
        try:
            try:
                http_connection.connect()
            except socket.timeout:
                raise ApiTimeoutError(
                    "IceScreen Touch did not accept the connection in time (nothing was sent)") from None
            except OSError as exc:
                # Refused or unreachable: nothing was written, so the operation did not run and a retry is safe.
                unavailable = ApiUnavailableError(
                    f"IceScreen Touch could not be reached on 127.0.0.1:{connection.port} ({type(exc).__name__}): "
                    "is the app running with the External API switched on?")
                unavailable.request_sent = False
                raise unavailable from None
            try:
                http_connection.request(method, path, body=payload, headers=headers)
                response = http_connection.getresponse()
                data = response.read(_MAX_RESPONSE_BYTES + 1)
            except socket.timeout:
                raise ApiTimeoutError(
                    "IceScreen Touch did not answer in time (an operation that already started may still be running; "
                    "call cancel())") from None
            except (OSError, http.client.HTTPException) as exc:
                raise ApiUnavailableError(
                    f"IceScreen Touch could not be reached on 127.0.0.1:{connection.port} ({type(exc).__name__}): "
                    "is the app running with the External API switched on?") from None
        finally:
            http_connection.close()
        if len(data) > _MAX_RESPONSE_BYTES:
            raise ApiError("the API answered with too much data")
        if response.status >= 400:
            raise _problem(response.status, response.getheader("Content-Type") or "", data, response.getheader("Retry-After"))
        return response.status, response.headers, data


def _problem(status: int, content_type: str, data: bytes, retry_after: Optional[str] = None) -> ApiError:
    code = detail = None
    if "json" in content_type:
        try:
            parsed = json.loads(data.decode("utf-8"))
            if isinstance(parsed, dict):
                code = parsed.get("code") if isinstance(parsed.get("code"), str) else None
                detail = parsed.get("detail") if isinstance(parsed.get("detail"), str) else None
        except ValueError:
            pass
    if code is None:
        return ApiError(f"the API answered HTTP {status}", status=status)
    error = error_for_code(code, detail, status)
    if isinstance(error, RateLimitedError) and retry_after is not None and _RETRY_AFTER.fullmatch(retry_after.strip()):
        error.retry_after = float(min(int(retry_after.strip()), _MAX_RETRY_AFTER))
    return error


def _rotation(data: Mapping[str, Any]) -> RotationInfo:
    return RotationInfo(landscape=int(data.get("landscape", 90)), portrait=int(data.get("portrait", 0)))
