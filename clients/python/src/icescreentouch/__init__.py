# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

"""Python client for the IceScreen Touch automation API.

::

    from icescreentouch import Client

    with Client() as phone:
        phone.home()
        shot = phone.screenshot()
        phone.tap_pixel(540, 1820, ref=shot.size)
        phone.tap_text("Settings")          # find a text on the screen (OCR) and tap it

The WebSocket session (``phone.stream()``) needs ``pip install icescreentouch[stream]``; ``Screenshot.to_pil()`` needs
``pip install icescreentouch[image]``. The MCP server is ``icescreentouch-mcp`` (``pip install icescreentouch[mcp]``).
"""

from .client import Client, RequestScope
from .discovery import Connection, discover
from .errors import *  # noqa: F401,F403 - the exception classes are the public API
from .errors import __all__ as _errors_all
from .models import (
    Device,
    OcrEngineInfo,
    OcrInfo,
    Picture,
    RotationInfo,
    Screenshot,
    Status,
    TapTextResult,
    TextBox,
    TextLine,
    TextMatch,
    TextPicture,
    TextResult,
    TextWord,
)

__version__ = "0.1.0"

__all__ = ["Client", "RequestScope", "Connection", "discover", "Device", "Picture", "RotationInfo", "Screenshot", "Status",
           "OcrInfo", "OcrEngineInfo", "TextResult", "TextLine", "TextWord", "TextBox", "TextMatch", "TextPicture",
           "TapTextResult", "__version__", *_errors_all]
