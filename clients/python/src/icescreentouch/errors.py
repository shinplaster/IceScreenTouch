# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Exceptions of the IceScreen Touch client.

Every failure the client reports is an :class:`IceScreenTouchError`. Errors the API itself reports (RFC 9457
``application/problem+json`` with a machine code, chapter 10 of docs/API-DESIGN.md) have one class per code, so a
script can tell ``busy`` from ``user_active`` with ``except``. The client never retries ``busy`` or
``user_active`` by itself: the user has priority, so a script decides what to do.

The error texts never contain the bearer token.
"""

from __future__ import annotations

from typing import Optional


class IceScreenTouchError(Exception):
    """Base class of everything this package raises on purpose.

    Attributes:
        code: The machine-readable API error code (``"busy"``), or None for a client-side failure.
        status: The HTTP status, or None.
        detail: The server's sentence for the reader (never user data), or None.
    """

    code: Optional[str] = None

    def __init__(self, message: str, *, code: Optional[str] = None, status: Optional[int] = None,
                 detail: Optional[str] = None) -> None:
        super().__init__(message)
        if code is not None:
            self.code = code
        self.status = status
        self.detail = detail


class InvalidArgumentError(ValueError):
    """An argument of a call is wrong (out of range, the wrong kind, missing). Nothing was sent.

    It is a ``ValueError`` (what Python code expects for a bad argument) and deliberately not an
    :class:`IceScreenTouchError`: it is the caller's mistake, not a failure of the API.
    """


# ---- Failures of the client side (nothing reached the API, or its answer was not understood) ----------------

class ConnectionDiscoveryError(IceScreenTouchError):
    """The connection information (port and token) could not be found or is not usable."""


class StaleConnectionError(ConnectionDiscoveryError):
    """The port does not belong to IceScreen Touch of the current Windows user, so the token was NOT sent.

    Reasons: api.json is stale (a crash left it behind: nobody listens, or another process holds the port); the
    listener runs as another user or is not ``IceScreenTouch.exe``; or, on Windows, the owner of the port could not be
    verified at all (the check fails closed).
    """


class ApiUnavailableError(IceScreenTouchError):
    """The API could not be reached (the app is not running, the API is off, or the connection broke).

    Attributes:
        request_sent: False when the failure happened before anything was sent (the connection was refused), so the
            operation certainly did not run and repeating it is safe. True when the request may have reached the app
            (the connection broke or timed out afterwards): the operation may have run.
    """

    request_sent: bool = True


class ApiTimeoutError(ApiUnavailableError, TimeoutError):
    """The API did not answer in time. An operation that was already started may still be running (see Client.cancel)."""


# ---- Errors the API reports ----------------------------------------------------------------------------------

class ApiError(IceScreenTouchError):
    """An error answer of the API. The subclass tells the code; unknown codes are plain ApiError."""


class InvalidRequestError(ApiError):
    """400 invalid_request: the body, a range or a limit is wrong."""
    code = "invalid_request"


class UnauthorizedError(ApiError):
    """401 unauthorized: the token is missing or wrong (the app may have regenerated it)."""
    code = "unauthorized"


class ForbiddenOriginError(ApiError):
    """403 forbidden_origin: the Origin or Host header is not allowed (a client bug: this client sends neither wrongly)."""
    code = "forbidden_origin"


class PurchaseRequiredError(ApiError):
    """403 purchase_required: the license does not include the API (trial, expired)."""
    code = "purchase_required"


class NotRunningError(ApiError):
    """409 not_running: the mirroring session is not running."""
    code = "not_running"


class NotConnectedError(ApiError):
    """409 not_connected: the target device has not subscribed to the HID reports (not paired or connected)."""
    code = "not_connected"


class NoPictureError(ApiError):
    """409 no_picture: there is no picture (the receiver window is closed, minimized, on another virtual desktop or not found, or it could not be read)."""
    code = "no_picture"


class BusyError(ApiError):
    """409 busy: another operation, a WebSocket session or a measurement is running. Not retried automatically."""
    code = "busy"


class UserInterruptedError(ApiError):
    """409 user_interrupted: the user touched the mirror; the operation stopped and every finger was lifted.

    The user has priority. Do not retry blindly.
    """
    code = "user_interrupted"


class UserActiveError(ApiError):
    """409 user_active: the user used the mirror within the last second. Not retried automatically."""
    code = "user_active"


class NoKeyboardError(ApiError):
    """409 no_keyboard: the published HID map has no keyboard."""
    code = "no_keyboard"


class NoHomeButtonError(ApiError):
    """409 no_home_button: the published HID map has no home button."""
    code = "no_home_button"


class PictureChangedError(ApiError):
    """409 picture_changed: the screen rotated since the screenshot a pixel position refers to; take a new one."""
    code = "picture_changed"


class RateLimitedError(ApiError):
    """429 rate_limited: the API limits how fast it is called.

    A REST screenshot is limited to 10 in any second (over all clients), and too many in progress at once are refused as well; the answer has ``Retry-After: 1``. On a WebSocket
    stream it means more than 1000 messages in a second.

    Attributes:
        retry_after: Seconds to wait, from the ``Retry-After`` header, or None when the answer had none.
    """
    code = "rate_limited"
    retry_after: Optional[float] = None


class CancelledError(ApiError):
    """499 cancelled: Client.cancel() (or POST /v1/cancel) ended the operation."""
    code = "cancelled"


class NotFoundError(ApiError):
    """404 not_found: no such endpoint (for example screenshots on an app version before screenshot support)."""
    code = "not_found"


class MethodNotAllowedError(ApiError):
    """405 method_not_allowed."""
    code = "method_not_allowed"


class InternalError(ApiError):
    """500 internal_error: an unexpected failure inside the app (for example a failed BLE write)."""
    code = "internal_error"


class ScreenshotUnsupportedError(NotFoundError):
    """The app answers 404 for /v1/screenshot: this version of the app has no screenshot endpoint (update the app)."""


_BY_CODE = {cls.code: cls for cls in (
    InvalidRequestError, UnauthorizedError, ForbiddenOriginError, PurchaseRequiredError, NotRunningError,
    NotConnectedError, NoPictureError, BusyError, UserInterruptedError, UserActiveError, NoKeyboardError,
    NoHomeButtonError, PictureChangedError, RateLimitedError, CancelledError, NotFoundError,
    MethodNotAllowedError, InternalError)}

# The HTTP status of each code (used for errors that arrive on the WebSocket, which carries no status).
_STATUS_BY_CODE = {
    "invalid_request": 400, "unauthorized": 401, "not_found": 404, "method_not_allowed": 405,
    "forbidden_origin": 403, "purchase_required": 403, "rate_limited": 429, "internal_error": 500,
    "cancelled": 499, "not_running": 409, "not_connected": 409, "no_picture": 409, "busy": 409,
    "user_interrupted": 409, "user_active": 409, "no_keyboard": 409, "no_home_button": 409,
    "picture_changed": 409,
}


def error_for_code(code: Optional[str], detail: Optional[str], status: Optional[int] = None) -> ApiError:
    """The exception for an API error code (``ApiError`` itself for a code this client does not know)."""
    cls = _BY_CODE.get(code or "", ApiError)
    if status is None and code is not None:
        status = _STATUS_BY_CODE.get(code)
    label = code or "error"
    text = f"{label}: {detail}" if detail else label
    return cls(text, code=code, status=status, detail=detail)


# ---- WebSocket stream ----------------------------------------------------------------------------------------

class StreamError(IceScreenTouchError):
    """Base class of the failures of a WebSocket stream that are not API error codes."""


class StreamInterruptedError(StreamError):
    """The stream was interrupted (an ``interrupted`` event).

    Attributes:
        reason: ``"user"`` (the user took over), ``"cancelled"`` (cancel or server stop), ``"session_ended"``
            (the mirroring session ended) or ``"idle"`` (the 1 s dead-man watchdog lifted everything because no
            message came while a finger or key was down). After ``idle`` the stream stays open; after the others
            the server closes it.
    """

    def __init__(self, reason: str) -> None:
        super().__init__(f"the stream was interrupted ({reason}); every finger and key was lifted", code="interrupted")
        self.reason = reason


class StreamClosedError(StreamError):
    """The server closed the stream, or the connection broke.

    Attributes:
        close_code: The WebSocket close code (1008 policy, 1013 try again later, 1001 server stopping,
            1009 message too big, 1011 internal error, 1003 binary message, 1000 normal), or None when the
            connection ended without a close frame.
        reason: The server's short close reason, or "".
    """

    def __init__(self, message: str, *, close_code: Optional[int] = None, reason: str = "") -> None:
        super().__init__(message)
        self.close_code = close_code
        self.reason = reason


_CLOSE_TEXT = {
    1000: "the stream was closed",
    1001: "the server is shutting down",
    1003: "the server accepts text messages only",
    1008: "the server refused the stream (authentication, license or rate limit)",
    1009: "a message was larger than the 4 KB limit",
    1011: "the server failed inside (an internal error)",
    1013: "the server cannot take the stream now (try again later)",
}


def closed_error(close_code: Optional[int], reason: str, last_error: Optional[ApiError]) -> IceScreenTouchError:
    """The exception for a stream that ended with ``close_code``.

    A close usually follows an ``error`` event that names the cause (``busy``, ``unauthorized``...); that error is
    returned when there is one, because it is what a script can act on.
    """
    if last_error is not None:
        return last_error
    text = _CLOSE_TEXT.get(close_code, "the stream was closed") if close_code is not None else "the connection was lost"
    if reason:
        text = f"{text} ({reason})"
    return StreamClosedError(text, close_code=close_code, reason=reason)


__all__ = [
    "InvalidArgumentError", "IceScreenTouchError", "ConnectionDiscoveryError", "StaleConnectionError", "ApiUnavailableError",
    "ApiTimeoutError", "ApiError", "InvalidRequestError", "UnauthorizedError", "ForbiddenOriginError",
    "PurchaseRequiredError", "NotRunningError", "NotConnectedError", "NoPictureError", "BusyError",
    "UserInterruptedError", "UserActiveError", "NoKeyboardError", "NoHomeButtonError", "PictureChangedError",
    "RateLimitedError", "CancelledError", "NotFoundError", "MethodNotAllowedError", "InternalError",
    "ScreenshotUnsupportedError", "StreamError", "StreamInterruptedError", "StreamClosedError",
]
