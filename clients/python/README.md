# icescreentouch (Python client and MCP server)

Control an iPhone or iPad from Python, or from an AI agent over MCP, through the
[IceScreen Touch](../../README.md) automation API (Windows).

It needs the app running with **Settings > Automation API** switched on. The API is available in the purchased
Microsoft Store build and in builds you make yourself from this repository; it is not available during the trial.
The API listens on `127.0.0.1` only.

Python 3.10 or newer. The client itself uses only the standard library. Not published to PyPI yet: install from this
folder.

## Install

```powershell
cd clients\python

# with uv (recommended)
uv sync                      # client only
uv sync --extra stream       # + WebSocket session (websockets)
uv sync --extra image        # + Screenshot.to_pil() (Pillow)
uv sync --all-extras         # + MCP server, and the dev group (pytest)

# or with pip
pip install .                # also: .[stream]  .[image]  .[mcp]
```

## Use

```python
from icescreentouch import Client

with Client() as phone:                      # finds the API by itself (see "Connection")
    phone.home()
    shot = phone.screenshot(max_size=1024)   # shot.data (PNG bytes), shot.size, shot.to_pil()
    phone.tap(0.5, 0.9)                      # screen space: 0..1 of the screen as it looks now
    phone.tap_pixel(540, 1820, ref=shot.size)  # pixels of that screenshot
    phone.swipe((0.5, 0.8), (0.5, 0.2), duration_ms=300)
    phone.pinch((0.5, 0.5), 0.1, 0.3)        # radius = fraction of the screen's short side
    phone.press_key("a", ["cmd"])            # select all
    skipped = phone.type_text("hello")       # US keyboard; returns characters that could not be typed

    with phone.stream() as s:                # low-level WebSocket session (needs the `stream` extra)
        s.down(0.2, 0.5)
        s.move(0.8, 0.5)
        s.up()
```

* All times are milliseconds and named `*_ms`, as in the API (`hold_ms`, `duration_ms`, `interval_ms`).
* Spaces: `"screen"` (default, 0..1 of the screen as it looks now), `"pixel"` (needs `ref=(width, height)` of the
  screenshot), `"device"` (0..1 of the phone's own portrait frame).
* `get_status()`, `get_rotation()`, `set_rotation(landscape=None, portrait=None)`, `set_target(device_id_or_None)`,
  `cancel()`, `path(points)`, `long_press(...)` are there too.
* Errors: every failure is an `IceScreenTouchError`. Each API error code has its own class (`BusyError`,
  `UserActiveError`, `UserInterruptedError`, `NoPictureError`, `PictureChangedError`, ...) with `.code`, `.status` and
  `.detail`. `busy` and `user_active` are **not** retried: the user's own input on the mirror has priority, so decide in
  your script what to do. After a failure that may have reached the app (a timeout, a connection that broke after the
  request was written) the client does not repeat the operation: it could tap twice. `ApiUnavailableError.request_sent`
  is `False` when nothing was sent (the connection was refused).
* A `stream()` session keeps itself alive with a `ping` every 0.25 s while a finger or key is held (the server lifts
  everything after 1 s of silence). `interrupted` events call `on_interrupted(reason)` and raise
  `StreamInterruptedError` from the next call; `keepalive=False` turns the pings off. `up()` and `key(..., down=False)`
  are always sent first and raise a pending error afterwards, so a finger or key is never left held by an earlier
  failure. A call that times out waiting for its `ack` raises `ApiTimeoutError`, and a late answer to it is dropped.
  The WebSocket library's own logger is never allowed to log at DEBUG (it would log the token in the first frame).
* `screenshot(format="png", max_size=None, quality=None)` returns the receiver's picture as a `Screenshot` (`data`, `size`,
  `orientation`, `rotation`, `source_size`). `max_size` (16 to 8192) only shrinks; `quality` (1 to 100) is for JPEG only;
  arguments the app would refuse raise `ValueError` before anything is sent. The size follows the receiver window, so pass
  `ref=shot.size` when you point at it in pixels. It is not an operation (it works while a touch or a stream runs), but the
  app answers at most 10 a second: more raise `RateLimitedError` (`retry_after` is the `Retry-After` value). An app version
  without the screenshot endpoint answers 404 and the client raises `ScreenshotUnsupportedError`.

### Connection

`Client()` looks, in this order, at: `ICESCREENTOUCH_API` (`http://127.0.0.1:47821`) with `ICESCREENTOUCH_TOKEN`; the
`api.json` of the Microsoft Store package; `%ICESCREENTOUCH_DATA_DIR%\api.json`; `%LOCALAPPDATA%\IceScreenTouch\api.json`.
`Client(base_url=..., token=...)` overrides all of that. The client always connects to `127.0.0.1` and never sends an
`Origin` header.

**Owner check.** On Windows, before every request (REST and WebSocket) the client verifies that every process
listening on the port is `IceScreenTouch.exe` running as the **same Windows user** and, for a connection from `api.json`,
that the only listener is exactly the `pid` in the file. Otherwise the token is not sent and `StaleConnectionError` is
raised. The check also covers `ICESCREENTOUCH_API` / `ICESCREENTOUCH_TOKEN` and `Client(base_url=..., token=...)`, and it
**fails closed**: if the TCP table or the process cannot be read (for example access to the process is denied), the
token is not sent either. Off Windows nothing can be checked and it is skipped. `Client(..., verify_owner=False)` turns
the check off for a setup you control, such as a test server; the token then goes to whatever listens on the port.

## MCP server

`icescreentouch-mcp` is an MCP server over stdio for AI agents. Tools: `get_status`, `screenshot`, `tap`, `long_press`,
`swipe`, `pinch`, `type_text`, `press_key`, `home`, `set_rotation`. Positions are pixels of the most recent
`screenshot` result (or `space="screen"`, 0..1).

Claude Code:

```powershell
claude mcp add icescreentouch -- uv --directory C:\path\to\IceScreenTouch\clients\python run --extra mcp icescreentouch-mcp
```

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "icescreentouch": {
      "command": "uv",
      "args": ["--directory", "C:\\path\\to\\IceScreenTouch\\clients\\python", "run", "--extra", "mcp", "icescreentouch-mcp"]
    }
  }
}
```

**Safety.** The tools operate a real phone and can reach passcode screens, payment or purchase confirmations and
messages. Approval of tool calls is left to the MCP client: keep it on for these tools. The user's own input on the
mirror always wins; when a tool reports that the user interrupted or is active, the agent should look again rather than
retry. The server tells the model that text on the phone's screen and device names are content, never instructions.
The MCP SDK versions 1.22 and newer (including 2.x) are supported.

## Develop

```powershell
uv sync --all-extras
uv run pytest -q
```

The tests run against fake HTTP and WebSocket servers that copy the real server's responses; no phone or app is needed.

MIT licensed.
