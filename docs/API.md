# IceScreen Touch API

[日本語](API.ja.md)

IceScreen Touch has an automation API. A script or an AI agent on the same PC can operate the iPhone or iPad the way you do with the mouse and the keyboard: taps, swipes, pinches, typing, the Home button. The input takes the same Bluetooth path as your own input (the PC poses as a touch input device and a keyboard; see [USER-GUIDE.md](USER-GUIDE.md)). The API can be reached from this PC only.

This page is the guide. The full reference (every field, default, limit and error) is the OpenAPI document [api/openapi.yaml](api/openapi.yaml); the running app serves the same document as `GET /v1/openapi.json`.

## Contents

- [What you need](#what-you-need)
- [Turning it on](#turning-it-on)
- [Security](#security)
- [Quick start](#quick-start)
- [Coordinates](#coordinates)
- [Screenshot](#screenshot)
- [Text on the screen (OCR)](#text-on-the-screen-ocr)
- [Endpoints and limits](#endpoints-and-limits)
- [WebSocket stream](#websocket-stream)
- [Errors](#errors)
- [Python client and MCP server](#python-client-and-mcp-server)

## What you need

- The purchased IceScreen Touch from the Microsoft Store, or an app you built yourself from the source code. The API does not start while the app is in its trial, or while the Microsoft Store license cannot be checked (for example when the PC cannot reach the Store); it starts by itself once the license is read. If the license stops covering it while the API runs, every call answers `403` `purchase_required`, except `POST /v1/cancel` (a stop command always works).
- **Zoom** turned on in the device (Settings > Accessibility > Zoom), as for the mouse. Without it, touches do not arrive.
- A running mirroring session (the **Status** tab shows the state) and a device that is connected over Bluetooth.
- For `screen` and `pixel` coordinates, an AirPlay receiver whose window shows the device's picture. If the window is closed or minimized, those calls answer `409` `no_picture`. `device` coordinates do not need the picture (see [Coordinates](#coordinates)).

The API operates the one device the app currently sends input to ("Input to" in the **Status** tab; `PUT /v1/target` changes it).

## Turning it on

The API is **off by default**.

1. In IceScreen Touch, open the **Settings** tab and find the **Automation API** section.
2. Turn on "Let scripts on this PC operate the device".
3. Check the port. The default is **47821**. It can be 1024 to 65535, except 8000 (the measurement page uses it). If the port is in use by another program, the app says so; it never picks another port by itself.
4. Press **Save and apply**. The token is created now.

The app then shows "Automation API: listening on 127.0.0.1:<port>", and "Automation API: a script is operating the device" while a call or stream is running. If it cannot start, it shows "Automation API: could not start" (the port is in use by another program, or see the **Log** tab).

**Token.** Scripts authenticate with the token shown in the section (**Show** / **Hide**). **Copy** puts it on the clipboard. **Regenerate** makes a new one after a confirmation: the old token stops working at once, and any open WebSocket stream is closed.

**How scripts find the port and the token.** While the API is on, the app writes `api.json` (only your Windows user can read it) and deletes it when the API is switched off:

```json
{ "port": 47821, "token": "<43 characters>", "pid": 1234 }
```

| Install | Location of `api.json` |
|---|---|
| Normal (an app you built) | `%LOCALAPPDATA%\IceScreenTouch\api.json` |
| Microsoft Store | `%LOCALAPPDATA%\Packages\Shin-plaster.IceScreenTouch_5c159ja95e6hm\LocalCache\Local\IceScreenTouch\api.json` (Windows redirects the app's files there when `%LOCALAPPDATA%\IceScreenTouch` did not exist yet). If that folder already existed, for example from a build of your own, it is `%LOCALAPPDATA%\IceScreenTouch\api.json` as for a normal install. The **About** tab shows it under "Data folder". |

`pid` is the process that owns the port. Before a client sends the token it should check that the process listening on the port is `IceScreenTouch.exe`, runs as **your** Windows user and, when the token comes from `api.json`, is exactly that `pid`: otherwise the file is stale, or another program took the port, and the token would go to it. The Python client does this (see below). Clients are expected to look first at the environment variables `ICESCREENTOUCH_API` (for example `http://127.0.0.1:47821`) and `ICESCREENTOUCH_TOKEN`, then at `api.json`.

## Security

- The API listens on **127.0.0.1** only (not `::1`, not another address). Programs on other machines cannot reach it.
- **Keep the token secret.** Every call except `GET /v1/openapi.json` and the WebSocket upgrade carries `Authorization: Bearer <token>`. Anything that has the token can do on the device whatever you can do with the mouse and the keyboard, **including typing the passcode and confirming purchases**. The API cannot tell whether the device is locked and does not stop input to a passcode screen. Turn the API on only for scripts and agents you trust, and keep an AI agent's tool-call approval on.
- **Web pages cannot call it.** A request that has an `Origin` header (web pages always send one; scripts and command-line tools do not) is refused with `403` `forbidden_origin`. The `Host` header must be `127.0.0.1:<port>` (use the address `127.0.0.1`, not the name `localhost`).
- **Your own input wins.** When you use the mirror window (the mouse on the picture, the wheel, the keys that go to the device, Home, rotate), a running operation is stopped, every finger is lifted and every key is released: a REST call answers `409` `user_interrupted`, and a stream gets an `interrupted` event and is closed. For one second after your last input (and while you hold the mouse or a key), new operations answer `409` `user_active`. The API never resumes by itself; the script decides whether to try again.

## Quick start

The examples assume the port 47821 and a token in `api.json`. A body is JSON with `Content-Type: application/json` (UTF-8, at most 64 KB). Success is `200` with a JSON body; an operation that returns nothing answers `{}`. An operation answers only after it has finished on the device.

### curl (a Unix-like shell such as Git Bash or WSL)

```sh
TOKEN="<the token from the app>"
API=http://127.0.0.1:47821
AUTH="Authorization: Bearer $TOKEN"
JSON="Content-Type: application/json"

# State of the app, the devices, the picture and the rotation
curl -s -H "$AUTH" $API/v1/status

# Tap the middle of the screen
curl -s -H "$AUTH" -H "$JSON" -d '{"x":0.5,"y":0.5}' $API/v1/touch/tap

# Swipe up (scroll down)
curl -s -H "$AUTH" -H "$JSON" \
  -d '{"from":{"x":0.5,"y":0.8},"to":{"x":0.5,"y":0.2},"durationMs":300}' $API/v1/touch/swipe

# Type text (US keyboard)
curl -s -H "$AUTH" -H "$JSON" -d '{"text":"hello"}' $API/v1/keys/type

# Home button
curl -s -H "$AUTH" -X POST $API/v1/home

# Correct the assumed landscape direction
curl -s -H "$AUTH" -H "$JSON" -X PUT -d '{"landscape":270}' $API/v1/rotation
```

### PowerShell

```powershell
# Read the port and the token (for the Store install, use the path in the table above)
$api = Get-Content "$env:LOCALAPPDATA\IceScreenTouch\api.json" | ConvertFrom-Json
$base = "http://127.0.0.1:$($api.port)"
$headers = @{ Authorization = "Bearer $($api.token)" }

Invoke-RestMethod "$base/v1/status" -Headers $headers

# Tap
Invoke-RestMethod "$base/v1/touch/tap" -Method Post -Headers $headers `
  -ContentType "application/json" -Body '{"x":0.5,"y":0.5}'

# Swipe
Invoke-RestMethod "$base/v1/touch/swipe" -Method Post -Headers $headers `
  -ContentType "application/json" `
  -Body '{"from":{"x":0.5,"y":0.8},"to":{"x":0.5,"y":0.2},"durationMs":300}'

# Type text
Invoke-RestMethod "$base/v1/keys/type" -Method Post -Headers $headers `
  -ContentType "application/json" -Body '{"text":"hello"}'

# Home
Invoke-RestMethod "$base/v1/home" -Method Post -Headers $headers

# Rotation
Invoke-RestMethod "$base/v1/rotation" -Method Put -Headers $headers `
  -ContentType "application/json" -Body '{"landscape":270}'
```

### Python

The client is in `clients/python` (see [Python client and MCP server](#python-client-and-mcp-server)). It finds the port and the token by itself.

```python
from icescreentouch import Client

with Client() as phone:
    print(phone.get_status())
    phone.tap(0.5, 0.5)                          # screen space, 0..1
    phone.swipe((0.5, 0.8), (0.5, 0.2), duration_ms=300)
    skipped = phone.type_text("hello")           # returns the characters that could not be typed
    phone.home()
    phone.set_rotation(landscape=270)
```

## Coordinates

Every position has a space, `space` (default `screen`):

| `space` | Meaning | Needs the picture |
|---|---|---|
| `screen` | The screen **as it is seen now**, normalized to 0..1 (0,0 is the top left, 1,1 the bottom right). Landscape means a wide screen. The app undoes the rotation. | Yes |
| `pixel` | Pixels of a screenshot of the device. `ref` (`{ "width": W, "height": H }`, the size of that screenshot) is **required**; x is 0..W and y is 0..H. | Yes |
| `device` | The digitizer's own portrait frame, 0..1, never rotated. | No |

- A position outside the range answers `400` `invalid_request`.
- If a `pixel` request's `ref` has the opposite orientation (wide versus tall) from the current picture, the device rotated since the screenshot: `409` `picture_changed`. Take a new screenshot. A square `ref` is never refused.
- **The left or right side of a landscape screen cannot be detected.** The app assumes one. `GET /v1/status` (and `GET /v1/rotation`) shows the assumed `rotation` (`landscape`: 90 or 270, `portrait`: 0 or 180). If taps land mirrored, correct it with `PUT /v1/rotation` (saved, like `Ctrl+R` in the app). The same holds for an upside-down portrait screen (180).

## Screenshot

`GET /v1/screenshot` returns a picture of the device's screen: the part of the receiver window that shows the device (the black bars of the window are left out), as a PNG or a JPEG. It is exactly the rectangle that `GET /v1/status` reports as `picture`. It sends nothing to the device.

| Query parameter | Value | Default |
|---|---|---|
| `format` | `png` or `jpeg` (lower case) | `png` |
| `maxSize` | A whole number from 16 to 8192: the longest side of the image in pixels. The app only shrinks the image (it never enlarges it) and keeps the aspect ratio. | The size of the picture as it is |
| `quality` | A whole number from 1 to 100. Only with `format=jpeg`; with `png` it is `400` `invalid_request`. | 90 |

Parameter names are case-sensitive. An unknown parameter, a repeated one, or a value that is not plain digits or is out of range answers `400` `invalid_request`. The image has no transparency.

The headers of the answer say how the app saw the picture:

| Header | Value |
|---|---|
| `X-IceScreenTouch-Orientation` | `portrait`, or `landscape` when the picture is wider than tall (a square one is `portrait`) |
| `X-IceScreenTouch-Rotation` | The rotation the app assumes for that orientation: `0`, `90`, `180` or `270` (the same as `rotation` in `GET /v1/status`) |
| `X-IceScreenTouch-Source-Size` | The size of the picture before `maxSize` shrank it, for example `1179x2556` |

```powershell
Invoke-WebRequest "$base/v1/screenshot?maxSize=1024" -Headers $headers -OutFile shot.png
```

```python
shot = phone.screenshot(max_size=1024)          # shot.size is the size of the image you got
shot.save("shot.png")
phone.tap_pixel(540, 1200, ref=shot.size)       # a position in the pixels of that image
```

- **The size follows the receiver window, not the device.** The image has the size of the picture in the window, which is not the resolution of the device. To point at a position in the screenshot, use `space: "pixel"` and give the size of that screenshot (the size of the image you received) as `ref`.
- **No picture.** If the receiver window is closed, minimized, on another virtual desktop or not found, or it could not be read, the answer is `409` `no_picture`. A minimized window cannot be read.
- **Other windows do not appear in the image.** The image is made from the receiver window alone, so notifications, overlays and other programs on top of it are not in the image.
- **The receiver window gets a colored border during screenshots.** For about 2 seconds after a screenshot is taken, a colored border (usually yellow) is shown around the receiver window. The border is not in the image. If Windows' capture is temporarily unavailable (rare), a screenshot may be taken without the border.
- **Not an operation.** A screenshot also works while a touch, a key, or a WebSocket stream is running. It never answers `409` `busy`.
- **At most 10 screenshots in any second, counted over all programs that use the API.** The 11th within a second answers `429` `rate_limited` with the header `Retry-After: 1`. A request counts once it has passed the check of its query (also when it is then answered `409` `not_running` or `409` `no_picture`); a request refused with `429` does not count. Too many screenshots in progress at once are also answered `429` `rate_limited`.
- **The order of the checks** after the common ones (`Origin`, `Host`, token) is: the license (`403` `purchase_required`), the query (`400` `invalid_request`), the rate limit (`429` `rate_limited`), `409` `not_running`, `409` `no_picture`.

## Text on the screen (OCR)

Three endpoints read the text of the device's screen, so a script or an AI agent can find a label without counting pixels in a screenshot:

| Method and path | Purpose |
|---|---|
| `POST /v1/text/read` | Read the text of the screen: the lines in reading order (top to bottom, then left to right), with their boxes and centres |
| `POST /v1/text/find` | Find a text, or wait for it (`timeoutMs`); the answer lists the matches, an empty list when it is not there |
| `POST /v1/touch/tap-text` | Find a text and tap it |

```powershell
# Tap "Wi-Fi" (waiting up to 3 seconds for it to appear)
Invoke-RestMethod -Method Post "$base/v1/touch/tap-text" -Headers $headers -ContentType "application/json" -Body '{ "text": "Wi-Fi", "timeoutMs": 3000 }'
```

```python
for line in phone.read_text(lang="ja").lines:
    print(line.text, line.center)                  # centre in 0..1 of the screen
phone.wait_for_text("Done", timeout_ms=10000)       # raises TextNotFoundError when it does not appear
phone.tap_text("Bluetooth", offset=(0.4, 0.0))     # tap to the right of the label (its switch)
```

- **The engine** is Windows.Media.Ocr, a part of Windows. IceScreen Touch reads the same picture as a screenshot (the receiver window gets the same colored border) and does not write the picture or the text to a file or keep them after the request.
- **Languages.** The OCR languages are the ones Windows has installed; `GET /v1/status` lists them in `ocr.languages`. Without `lang`, the first of your Windows languages that has OCR is used. Ask for a language with `lang` (`"ja"`, `"en-US"`): if Windows has no OCR for it, the answer is `409` `ocr_unavailable` with the installed ones in `available`; the app never reads in another language instead. A screen in Japanese needs `ja` (`en-US` cannot read Japanese; `ja` also reads Latin letters). **When nothing is found, check that the language matches the screen** (`language` in the answer).
- **Boxes and coordinates.** Boxes and centres are in `screen` space (0..1, default) or in `pixel` space of `ref` (as for touches); `device` is not accepted. A centre can go straight to `POST /v1/touch/tap` with the same `space`. `region` (`{ "x", "y", "width", "height" }` in that space) reads only part of the screen: lines whose centre is inside it.
- **Matching.** The searched text and each line are compared after Unicode normalization (NFKC) and with all spaces removed, and without regard to case unless `caseSensitive` is true. `match: "exact"` (default) means the whole line: `設定` matches the line `設定` (and `< 設定`, where a lone character at either end that is not a kanji and stands apart, such as an icon read as a letter, is left out), but not `画面表示と設定`. `match: "contains"` matches part of a line; use it with `region`, since short texts appear in many lines. `loose: true` also treats look-alike characters (`ロ` and `口`, `l`, `I` and `1`, `0` and `O`, the long vowel mark and dashes), voiced marks (`ボ`, `ポ` and `ホ`) and the kinds of kana as the same: OCR misreads them.
- **Tap text.** `tap-text` is an operation (it is `409` `busy` while another one runs, and it needs what a tap needs). After it finds the text it reads the screen again after `settleMs` (250 ms by default, also when `timeoutMs` is 0) and taps only if the same match is still there, so it does not tap a screen that is still moving. While it searches it does not hold back your mouse: if you use the mirror meanwhile, it stops with `409` `user_interrupted` and taps nothing. If the text is on the screen more than once, `select: "unique"` (default) taps nothing and answers `409` `ambiguous_text` with the candidates in `matches`: narrow the search with `region`, or use `select: "first"` or `select: "index"`. `offset` moves the tap from the centre (for example to a switch to the right of a label). On the home screen, tapping an app's name does not open the app (iOS opens it from its icon): move the tap onto the icon with `offset` (about `-0.05` in `y`). If the screen rotated (or the assumed rotation changed) while it searched, the answer is `409` `picture_changed` and nothing is tapped.
- **Limits.** Text readings count as screenshots (at most 10 a second together) and at most 5 a second themselves, over all programs; one reading runs at a time, with two waiting; at most two `find` or `tap-text` calls with `timeoutMs` (up to 30 seconds) wait at once. More answer `429` `rate_limited` with `Retry-After: 1`. `read` and `find` are not operations (they work while a touch or a stream runs); `POST /v1/cancel` does not stop them, but closing the connection does.
- **The order of the checks** after the common ones: the license, the body (`400`), the engine and the language (`409` `ocr_unavailable`), the limits (`429`), `409` `not_running`, `409` `no_picture`, the capture, `409` `picture_changed`, the recognition. `tap-text` checks what a tap checks (`not_running`, `busy`, `user_active`, `not_connected`, `no_picture`) before the engine and the language.

## Endpoints and limits

Details of every request and response are in [api/openapi.yaml](api/openapi.yaml) (or `GET /v1/openapi.json`, which needs no token).

| Method and path | Purpose |
|---|---|
| `GET /v1/status` | State of the app, the devices, the picture, the rotation; whether an operation is running |
| `PUT /v1/target` | Choose the device that gets the input (`null` = automatic) |
| `GET /v1/rotation` | The rotations the app assumes |
| `PUT /v1/rotation` | Correct the assumed rotation |
| `GET /v1/screenshot` | A picture of the device's screen (PNG or JPEG; see [Screenshot](#screenshot)) |
| `POST /v1/text/read` | Read the text of the screen (see [Text on the screen](#text-on-the-screen-ocr)) |
| `POST /v1/text/find` | Find a text on the screen, or wait for it |
| `POST /v1/touch/tap` | Tap (one finger; `count` 1 to 10) |
| `POST /v1/touch/long-press` | Long press (default 800 ms) |
| `POST /v1/touch/swipe` | Swipe or drag in a straight line (with optional hold before and after) |
| `POST /v1/touch/path` | Drag one finger along any path (up to 1000 timed points) |
| `POST /v1/touch/pinch` | Pinch with two fingers |
| `POST /v1/touch/tap-text` | Find a text on the screen and tap it |
| `POST /v1/keys/press` | Press one key, with modifiers `cmd`, `shift`, `option`, `ctrl` |
| `POST /v1/keys/type` | Type text |
| `POST /v1/home` | Press the home button |
| `POST /v1/cancel` | Stop the running operation or stream |
| `GET /v1/stream` | WebSocket for low-level input (see below) |
| `GET /v1/openapi.json` | The specification (no token) |

**Limits**

- **One operation at a time.** While a touch, key, home, `PUT /v1/rotation`, `PUT /v1/target` or a stream is running, every other operation answers `409` `busy`; the API never queues. `GET /v1/status`, `GET /v1/rotation` and `GET /v1/openapi.json` always work, and `GET /v1/screenshot`, `POST /v1/text/read` and `POST /v1/text/find` never answer `busy`. While the measurement (Test) page of the app is running, operations are `409` `busy` too.
- **No operation takes longer than 60 seconds** (the sum of holds, durations, intervals and the last `t` of a path), else `400` `invalid_request`.
- A touch is held at least **70 ms** (a shorter `holdMs` is raised to 70). The finger of a moving touch is updated every 16 ms and is always lifted at its last position.
- `POST /v1/cancel` stops the running operation; the stopped request answers `499` `cancelled`.
- **Typing** sends the keys of a **US keyboard**. `keys/type` takes up to 1000 characters, and its estimated time must stay within 60 seconds (split a long text). New lines are typed as Enter, tabs as Tab. Characters without a key on a US keyboard (Japanese, emoji, accented letters) are not typed; they come back in `skipped`. The API does not convert Japanese: to type Japanese, switch the device to its "Japanese - Romaji" keyboard (for example with `ctrl` + `Space` through `keys/press`), type the romaji and confirm with the keys.
- `keys/press` and `keys/type` need the keyboard in the published HID map (`409` `no_keyboard` otherwise); `home` needs the home button (`409` `no_home_button`). The key names are the device's: `cmd` is the device's Command key, not the PC's Ctrl.
- A request body is at most 64 KB; unknown members and missing required members are `400` `invalid_request`.

## WebSocket stream

`GET /v1/stream` upgrades to a WebSocket for real-time, low-level input: put one or two fingers down, move them, lift them, press and release keys. This section is a summary; the full protocol is in the description of `GET /v1/stream` in [api/openapi.yaml](api/openapi.yaml).

- **Authentication.** A browser cannot set `Authorization` on a WebSocket, so the **first message** must be `{ "op": "auth", "token": "<token>" }`, within **2 seconds** of the upgrade. A wrong or late first message gets an `error` event (`unauthorized`) and the close code 1008. The `Origin` and `Host` checks still apply before the upgrade.
- **Session.** After a good `auth` the server sends `{ "ev": "ready", "api": 1 }`. The stream behaves like a running operation: every other operation answers `409` `busy` while it is open, and the same preconditions as for touches apply. Up to 4 streams can be connected, but only one holds the session.
- **Messages** are JSON in text frames, at most 4 KB. From the client (`seq` is a number you choose; the answer repeats it): `down` and `move` (`contacts`: one or two fingers `{ "id": 0 or 1, "x", "y" }`; `down` also takes `space` and `ref`), `up`, `key` (`down: true` or `false`), `ping`. From the server: `ack`, `error` (the message did nothing; the stream stays open), `interrupted`, `pong`.
- **Dead-man.** While a finger or key is down, if no message arrives for **1 second**, everything is lifted and released and the server sends `{ "ev": "interrupted", "reason": "idle" }`; the stream stays open. To hold a finger still, send `ping` regularly. `interrupted` with reason `user`, `cancelled` or `session_ended` ends the stream.
- **Close codes.** 1000 normal end (client, user, cancel or the end of the session); 1001 the app is shutting down; 1003 binary frame; 1008 authentication failed or timed out, the token was regenerated, the license does not cover the API, or more than 1000 messages in a second (`rate_limited`); 1009 message over 4 KB; 1011 internal error; 1013 try again later (the session could not be opened: `busy`, `user_active`, `not_connected` or `not_running`).

```
-> { "op": "auth", "token": "..." }
<- { "ev": "ready", "api": 1 }
-> { "op": "down", "seq": 1, "space": "screen", "contacts": [{ "id": 0, "x": 0.2, "y": 0.5 }] }
<- { "ev": "ack", "seq": 1 }
-> { "op": "move", "seq": 2, "contacts": [{ "id": 0, "x": 0.8, "y": 0.5 }] }
<- { "ev": "ack", "seq": 2 }
-> { "op": "up", "seq": 3 }
<- { "ev": "ack", "seq": 3 }
```

## Errors

An error is `application/problem+json` (RFC 9457) with the machine-readable member `code`. `detail` is a sentence for a person and never contains your data (positions, keys, text, the token). The text errors add members that a script can act on: `ocr_unavailable` has `reason` (`language` or `engine`), `available` and `engines`; `text_not_found`, `ambiguous_text` and `tap_outside_screen` have `matches` (up to 20 candidates) and `matchCount`.

| HTTP status | `code` | Meaning |
|---|---|---|
| 400 | `invalid_request` | The body, a range, a limit or a name is wrong. |
| 401 | `unauthorized` | The token is missing or wrong. |
| 403 | `forbidden_origin` | The `Origin` header is present, or `Host` is not allowed. |
| 403 | `purchase_required` | The license does not cover the API (trial, expired). |
| 404 | `not_found` | No such endpoint. |
| 405 | `method_not_allowed` | The endpoint does not accept that HTTP method (`Allow` lists the ones it does). |
| 409 | `not_running` | The mirroring session is not running. |
| 409 | `not_connected` | The target device has not subscribed to the HID reports. |
| 409 | `no_picture` | There is no picture (the receiver window is closed or minimized, or it could not be read) and the request needs one. |
| 409 | `picture_changed` | A `pixel` request refers to a screenshot of the opposite orientation: the device rotated since. For `tap-text`, also: the screen (or the assumed rotation) changed while it searched. |
| 409 | `busy` | Another operation, a WebSocket stream or the measurement page is running. |
| 409 | `user_interrupted` | You used the mirror; everything was lifted. |
| 409 | `user_active` | You used the mirror within the last second, or hold the mouse or a key. |
| 409 | `no_keyboard` | The published HID map has no keyboard. |
| 409 | `no_home_button` | The published HID map has no home button. |
| 409 | `ocr_unavailable` | Windows has no OCR for the language (`available` lists the installed ones), or no such OCR engine. |
| 409 | `text_not_found` | `tap-text` did not find the text in time; nothing was tapped. |
| 409 | `ambiguous_text` | `tap-text` found the text more than once (`matches` lists them); nothing was tapped. |
| 409 | `tap_outside_screen` | The `offset` of `tap-text` puts the tap outside the screen; nothing was tapped. |
| 429 | `rate_limited` | More than 10 screenshots and text readings in one second (or 5 text readings), or too many in progress or waiting at once (`Retry-After: 1`), or too many messages per second on a WebSocket stream (it is closed). |
| 499 | `cancelled` | `POST /v1/cancel` (or the client going away) ended the operation. |
| 500 | `internal_error` | An unexpected failure inside the app (for example a failed Bluetooth write). |

The checks run in this order: `Origin`, `Host`, token, route, then the request body.

## Python client and MCP server

`clients/python` holds the Python package `icescreentouch`: a client (standard library only) and the MCP server `icescreentouch-mcp` for AI agents. It needs Python 3.10 or newer and Windows. It is not published to PyPI: install it from the folder. Its own README, [../clients/python/README.md](../clients/python/README.md), has the details.

```powershell
cd clients\python

# with uv (recommended)
uv sync                      # client only
uv sync --extra stream       # + WebSocket session
uv sync --extra image        # + Screenshot.to_pil()
uv sync --all-extras         # + MCP server and the test tools

# or with pip
pip install .                # also: .[stream]  .[image]  .[mcp]
```

`Client()` finds the API from `ICESCREENTOUCH_API` and `ICESCREENTOUCH_TOKEN`, then from `api.json` (an explicitly set `ICESCREENTOUCH_DATA_DIR` first, then the Store location, then the default app data folder). It always connects to `127.0.0.1` and never sends an `Origin` header. `Client(base_url=..., token=...)` overrides the discovery. **Owner check:** on Windows, before every request the client verifies that the listener on the port is `IceScreenTouch.exe` running as the same Windows user and not at a lower integrity level than your script when that is below Medium (for `api.json`, also exactly the `pid` in the file). If it is not, or if that cannot be verified, the token is **not sent** and `StaleConnectionError` is raised. `Client(..., verify_owner=False)` turns the check off for a setup you control, such as a test server. Errors are `IceScreenTouchError` subclasses, one per `code`. `busy` and `user_active` are not retried: your input has priority, so the script decides what to do. `phone.stream()` opens the WebSocket session (it needs the `stream` extra) and keeps it alive with pings while a finger or key is held.

### MCP server

`icescreentouch-mcp` is an MCP server over stdio. Register it with your MCP client; replace the path with the place of your checkout.

Claude Code:

```powershell
claude mcp add icescreentouch -- uv --directory C:\path\to\IceScreenTouch\clients\python run --no-dev --extra mcp icescreentouch-mcp
```

Claude Desktop (`claude_desktop_config.json`):

```json
{
  "mcpServers": {
    "icescreentouch": {
      "command": "uv",
      "args": ["--directory", "C:\\path\\to\\IceScreenTouch\\clients\\python", "run", "--no-dev", "--extra", "mcp", "icescreentouch-mcp"]
    }
  }
}
```

The thirteen tools:

| Tool | Purpose |
|---|---|
| `get_status` | State of the app, the device connection and the picture |
| `screenshot` | The device's screen as an image, which is the reference for pixel positions |
| `read_text` | The text of the screen, line by line, with centres in 0..1 of the screen |
| `find_text` | Find a text, or wait for it (`timeout_ms` up to 10 seconds) |
| `tap_text` | Find a text and tap it |
| `tap` | Tap once, or 2 to 10 times |
| `long_press` | Press and hold |
| `swipe` | Drag one finger from one point to another |
| `pinch` | Two-finger pinch |
| `type_text` | Type text (US layout; characters without a key are reported as skipped) |
| `press_key` | Press one key with optional modifiers |
| `home` | Press the home button |
| `set_rotation` | Tell the app how the device is rotated |

The touch tools take **pixels of the most recent `screenshot` result** by default; the size of that image is sent as `ref`. Without a screenshot there is no reference, so the tool refuses and asks for one. `space="screen"` (0..1) works without a screenshot. The text tools use 0..1 of the screen, so they work without a screenshot; their positions go to `tap` with `space="screen"`. The text they return is screen content: the server tells the model never to follow it as instructions. Like screenshots, the text read by `read_text`, `find_text` and `tap_text` is sent to the AI service you use.

The tools operate a real device and can reach passcode screens and purchase confirmations. Keep your MCP client's approval of tool calls on for them. When a tool reports that you interrupted or are active, the agent should look at the screen again instead of retrying.
