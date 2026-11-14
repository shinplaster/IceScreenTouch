# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

# No "from __future__ import annotations" here: the MCP SDK reads these annotations to build the tool schemas, and the
# server registers wrappers of these methods that live in another module.

"""The tools of the MCP server, as plain methods (so tests can call them with a fake client).

Coordinates. The model looks at a screenshot, so ``tap`` and the other touch tools take **pixels of the most recent
``screenshot`` tool result** by default (the size of that image is sent as ``ref``; the app turns it into a position
on the real screen). Without a screenshot there is no reference, so the tool refuses and tells the model to take one.
``space="screen"`` takes 0..1 of the whole screen as it looks now instead.
"""

import io
from typing import Any, Callable, Dict, List, Literal, Optional, Tuple

from ..client import Client
from ..errors import (
    ApiTimeoutError,
    ApiUnavailableError,
    BusyError,
    ConnectionDiscoveryError,
    IceScreenTouchError,
    InvalidRequestError,
    NoHomeButtonError,
    NoKeyboardError,
    NoPictureError,
    NotConnectedError,
    NotRunningError,
    PictureChangedError,
    PurchaseRequiredError,
    RateLimitedError,
    ScreenshotUnsupportedError,
    UnauthorizedError,
    UserActiveError,
    UserInterruptedError,
)
from ._compat import Image, ToolError

DEFAULT_MAX_SIDE = 1024

Space = Literal["pixel", "screen"]

_NEED_SCREENSHOT = (
    "There is no reference screenshot yet. Call the screenshot tool first and use pixel coordinates of that image "
    "(or pass space='screen' with x and y between 0 and 1)."
)


def describe_error(exc: IceScreenTouchError) -> str:
    """A message for the model that says what happened and what to do next (never the token)."""
    if isinstance(exc, PictureChangedError):
        return ("The screen rotated since the last screenshot, so those pixel coordinates are out of date. "
                "Take a new screenshot with the screenshot tool and use coordinates from it.")
    if isinstance(exc, UserInterruptedError):
        return ("The user touched the phone mirror while this action ran, so it was stopped and every finger was "
                "lifted. The user's own input has priority: do not simply repeat the action. Take a screenshot to "
                "see the current state and continue only if it still makes sense (ask the user if unsure).")
    if isinstance(exc, UserActiveError):
        return ("The user is using the phone mirror right now (input within the last second), so nothing was done. "
                "The user's own input has priority: wait a moment and take a screenshot before trying again; do not "
                "retry in a loop.")
    if isinstance(exc, BusyError):
        return "Another operation (or a WebSocket session or measurement) is running in IceScreen Touch. Wait and try again later."
    if isinstance(exc, NoPictureError):
        return ("IceScreen Touch has no picture of the phone: the receiver window (UxPlay, AirServer...) is closed or "
                "minimized. Ask the user to show the mirror window.")
    if isinstance(exc, NotConnectedError):
        return "The phone is not connected over Bluetooth (it has not subscribed to the input device). Ask the user to check the pairing."
    if isinstance(exc, NotRunningError):
        return "Mirroring is not running in IceScreen Touch. Ask the user to start it."
    if isinstance(exc, PurchaseRequiredError):
        return "The external API is not available in the trial (it needs the purchased or a self-built app)."
    if isinstance(exc, UnauthorizedError):
        return "IceScreen Touch refused the token (it may have been regenerated). Restart this MCP server or set ICESCREENTOUCH_TOKEN."
    if isinstance(exc, (NoKeyboardError, NoHomeButtonError)):
        return f"{exc} (the published Bluetooth HID configuration lacks this feature)."
    if isinstance(exc, RateLimitedError):
        return "IceScreen Touch says there are too many requests. Slow down: wait a moment before trying again."
    if isinstance(exc, ScreenshotUnsupportedError):
        return "This version of IceScreen Touch cannot take screenshots yet. Update the app."
    if isinstance(exc, InvalidRequestError):
        return f"IceScreen Touch rejected the request: {exc.detail or exc}"
    if isinstance(exc, ApiTimeoutError):
        return ("IceScreen Touch did not answer in time. The action may still be running or may already have "
                "finished. Take a screenshot to see the current state before doing anything else, and do not repeat "
                "the action blindly.")
    if isinstance(exc, ApiUnavailableError):
        note = "" if not exc.request_sent else (
            " The connection broke after the request was sent, so the action may have run: take a screenshot to "
            "check before repeating it.")
        return ("IceScreen Touch could not be reached. Ask the user to start IceScreen Touch and switch on "
                "Settings > External API." + note)
    if isinstance(exc, ConnectionDiscoveryError):
        return str(exc)  # already says what is wrong and what to do (the token was not sent, the file was not found...)
    return str(exc)


def _shrink(data: bytes, max_side: int) -> Optional[Tuple[bytes, Tuple[int, int]]]:
    """Scales a PNG down so its long side is ``max_side`` (needs Pillow); None when Pillow is missing."""
    try:
        from PIL import Image as PilImage
    except ImportError:
        return None
    image = PilImage.open(io.BytesIO(data))
    image.load()
    image.thumbnail((max_side, max_side), PilImage.Resampling.LANCZOS)
    out = io.BytesIO()
    image.convert("RGB").save(out, format="PNG")
    return out.getvalue(), image.size


class PhoneTools:
    """The state of one MCP server: the client and the size of the last screenshot shown to the model.

    Args:
        client_factory: Makes the client on first use (so the server starts even when the app is not running).
        max_side: The long side screenshots are shrunk to (about).
    """

    def __init__(self, client_factory: Callable[[], Any] = Client, max_side: int = DEFAULT_MAX_SIDE) -> None:
        self._factory = client_factory
        self._client: Any = None
        self._max_side = max_side
        self.reference: Optional[Tuple[int, int]] = None

    # ---- Plumbing ------------------------------------------------------------------------------------------

    def _phone(self) -> Any:
        if self._client is None:
            self._client = self._factory()
        return self._client

    def _call(self, action: Callable[[Any], Any]) -> Any:
        try:
            return action(self._phone())
        except IceScreenTouchError as exc:
            raise ToolError(describe_error(exc)) from None
        except ValueError as exc:
            raise ToolError(f"Invalid argument: {exc}") from None

    def _space(self, space: str) -> Tuple[str, Optional[Tuple[int, int]]]:
        if space == "pixel":
            if self.reference is None:
                raise ToolError(_NEED_SCREENSHOT)
            return "pixel", self.reference
        if space == "screen":
            return "screen", None
        raise ToolError("space must be 'pixel' or 'screen'")

    # ---- Tools ---------------------------------------------------------------------------------------------

    def get_status(self) -> Dict[str, Any]:
        """The state of IceScreen Touch, the phone connection and the picture size."""
        return self._call(lambda phone: phone.get_status().to_dict())

    def screenshot(self):  # no return annotation: a list with an Image must not become structured output
        """The phone's screen as an image, with the coordinate reference for the touch tools."""
        shot = self._call(lambda phone: phone.screenshot(format="png", max_size=self._max_side))
        data, size = shot.data, tuple(shot.size)
        if max(size) > self._max_side * 1.1 or min(size) <= 0:
            # The app did not shrink it enough (or the size is unknown): do it here when Pillow is there.
            shrunk = _shrink(data, self._max_side)
            if shrunk is not None:
                data, size = shrunk
        if min(size) <= 0:
            raise ToolError("The screenshot could not be read.")
        self.reference = (int(size[0]), int(size[1]))
        orientation = f", {shot.orientation}" if shot.orientation else ""
        text = (f"Screenshot {size[0]}x{size[1]} pixels{orientation}. For tap, long_press, swipe and pinch with "
                "space='pixel', x is 0..%d and y is 0..%d in THIS image." % (size[0], size[1]))
        return [Image(data=data, format="png"), text]

    def tap(self, x: float, y: float, space: Space = "pixel", count: int = 1) -> str:
        """Taps once (or ``count`` times, 1 to 10, for a double tap)."""
        space, ref = self._space(space)
        self._call(lambda phone: phone.tap(x, y, space, count=count if count != 1 else None, ref=ref))
        return "Tapped. Take a screenshot to see the result."

    def long_press(self, x: float, y: float, space: Space = "pixel", duration_ms: int = 800) -> str:
        """Presses and holds."""
        space, ref = self._space(space)
        self._call(lambda phone: phone.long_press(x, y, space, hold_ms=duration_ms, ref=ref))
        return "Long press done. Take a screenshot to see the result."

    def swipe(self, from_x: float, from_y: float, to_x: float, to_y: float, space: Space = "pixel",
              duration_ms: int = 300) -> str:
        """Drags one finger from the start to the end point."""
        space, ref = self._space(space)
        self._call(lambda phone: phone.swipe((from_x, from_y), (to_x, to_y), space, duration_ms=duration_ms, ref=ref))
        return "Swipe done. Take a screenshot to see the result."

    def pinch(self, center_x: float, center_y: float, start_radius: float, end_radius: float,
              space: Space = "pixel", duration_ms: int = 400) -> str:
        """Two-finger pinch around a centre point."""
        space, ref = self._space(space)
        self._call(lambda phone: phone.pinch((center_x, center_y), start_radius, end_radius, space,
                                             duration_ms=duration_ms, ref=ref))
        return "Pinch done. Take a screenshot to see the result."

    def type_text(self, text: str, interval_ms: Optional[int] = None) -> str:
        """Types text on the phone's keyboard (US layout; characters without a key are skipped)."""
        skipped = self._call(lambda phone: phone.type_text(text, interval_ms))
        if skipped:
            return (f"Typed, except {len(skipped)} character(s) that cannot be typed (not on a US keyboard, e.g. "
                    f"Japanese or emoji): {skipped!r}.")
        return "Typed."

    def press_key(self, key: str, modifiers: Optional[List[str]] = None) -> str:
        """Presses one key with optional modifiers."""
        self._call(lambda phone: phone.press_key(key, modifiers or ()))
        return "Key pressed."

    def home(self) -> str:
        """Presses the home button."""
        self._call(lambda phone: phone.home())
        return "Home pressed."

    def set_rotation(self, landscape: Optional[int] = None, portrait: Optional[int] = None) -> str:
        """Tells IceScreen Touch how the phone is rotated (it cannot tell left from right landscape itself)."""
        info = self._call(lambda phone: phone.set_rotation(landscape, portrait))
        return f"Rotation is now landscape {info.landscape}, portrait {info.portrait}."
