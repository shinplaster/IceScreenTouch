# Changelog

All notable changes to IceScreen Touch are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## Versioning

- The app follows [Semantic Versioning 2.0](https://semver.org/). It stays at `0.x` until it is stable (1.0.0, for
  example when the Store sale starts). While it is `0.x`, new features and changes that break something for users
  raise the minor number (`0.1.0` → `0.2.0`); releases with fixes only raise the patch number (`0.1.0` → `0.1.1`).
  From 1.0.0 on: breaking changes raise the major, features the minor, fixes the patch number.
- The version changes only for a release, not with every commit: changes collect under **Unreleased**, and the
  release turns that heading into the new version. The version has one source: `<Version>` in
  `Directory.Build.props`. The build adds the git commit (`0.1.0+e51464e…`), which tells builds between releases
  apart; the About tab shows both. A release is tagged `vX.Y.Z`, and the tag must match `<Version>`.
- The Microsoft Store package uses `X.Y.Z.0` (the Store needs four numbers and a last number of 0).
- Two formats have their own version numbers, apart from the app's:
  - **HID report map** (`HidReports.ReportMapVersion`, now 3: 263 bytes). After a change, users must remove the
    pairing on the iPhone or iPad and pair again. A release that changes it raises at least the minor number and says
    so under **Changed** with "pair again".
  - **Settings file** (`AppSettings.Version` in `settings.json`, now 2). Newer versions read older files.

## [Unreleased]

### Changed

- **First start without a receiver**: on a PC that has no AirPlay receiver set up yet (the default settings and no local
  UxPlay build, as with every new Microsoft Store install), the Status tab asks you to install a receiver instead of
  showing "Needs attention" and "uxplay.exe was not found". A receiver found on this PC later is then used by itself
  (before, only on the very first start). A uxplay.exe path entered in the settings that does not exist is still an
  error.
- **Pairing step**: it says that the iPhone may list this PC twice and that both must be paired, and it names this PC by
  its Bluetooth name, which keeps its upper and lower case (before: the upper-case computer name).
- **Setup checklist**: once the iPhone can be controlled, the Zoom step (which the app cannot check) is shown as an
  optional check and the checklist says "All done".
- **Trial**: the trial line on the Status tab says that the Automation API is available after purchase, and once the
  iPhone could be controlled it adds one line: "It works. Buy to keep using it after the trial."

## [0.4.0] - 2026-11-23

### Added

- **Text on the screen (OCR)** in the Automation API: `POST /v1/text/read` reads the text of the screen (lines in
  reading order with their boxes), `POST /v1/text/find` finds a text or waits for it to appear, and
  `POST /v1/touch/tap-text` finds a text and taps it (it confirms the match with a second reading so that a screen still
  moving is not tapped, and stops without tapping when you use the mirror meanwhile). Recognition is done by
  Windows.Media.Ocr, part of Windows, with the OCR languages Windows has installed; the text is neither written to a file
  nor kept. Matching is exact by default (a whole line), `contains` matches part of a line, and `loose` tolerates
  look-alike characters and voiced marks. `GET /v1/status` shows the OCR engine and languages (`ocr`). New error codes:
  `ocr_unavailable`, `text_not_found`, `ambiguous_text` and `tap_outside_screen`. Like screenshots, text readings are
  limited (5 a second, counted with the screenshots), and they show the capture border on the receiver window. The Python client's `read_text`, `find_text`, `wait_for_text` and `tap_text` and the MCP server's `read_text`, `find_text` and `tap_text` tools use them.

### Fixed

- While AirServer (Microsoft Store edition, in its trial) still showed its activation screen, an iPhone that started
  mirroring made IceScreen Touch take that screen for the device's picture ("Ready to operate" in the Status tab, and the
  Automation API's screenshot showed part of the activation screen), so a click on it became a touch on the iPhone. For
  AirServer and LonelyScreen, a window in which no picture is found now counts as the mirror window only when it has the
  shape of the device's screen (as AirServer's window has once fitted to the picture); the picture is followed from the
  first look at the window that finds it, for example right after **Continue Trial** is pressed.

## [0.3.2] - 2026-11-23

### Added

- **How to install a receiver**, next to **Search again** in the Status tab's receiver list, opens the user guide's
  chapter on installing an AirPlay receiver on GitHub, in the language of the app (English or Japanese). The button and its
  tooltip name no receiver; the guide lists the tested ones in name order with links to their official sites.
- **User guide** in the About tab opens the user guide on GitHub, in the language of the app.

### Fixed

- With a receiver started outside the app (such as AirServer), other computers that keep a Google Cast connection
  to the receiver are no longer taken as possible mirrored devices when choosing which iPhone or iPad gets the input.
- With the "Automatic" screen setting, the picture of these models is now looked for with the right shape: iPad Pro
  13-inch (M5) and iPad Air 13-inch (M4) as 4:3, iPad mini (A20 Pro) as an iPad mini, and the iPhone Duo with the
  shape of its displays (they were taken as an 11-inch iPad or as an iPhone X-style screen).
- AirServer's window counts as the mirror window only while a device is connected to it, as LonelyScreen's already did.
  Without a device, AirServer can show a window of its own (seen: the Microsoft Store edition's activation screen, and
  the installer edition's activation dialog at start); IceScreen Touch took it for the device's picture ("Showing" in
  the Status tab), so a click on it became a touch on the iPhone. Google Cast connections from other computers no
  longer count as a connected device.

## [0.3.1] - 2026-11-18

### Fixed

- When IceScreen Touch runs as administrator, the Automation API token no longer changes at every start. The token file is
  now created owned by the current user, so the app trusts it on the next start (a token file made by an earlier version
  is replaced once more at the first start after the update).
- The API guide and the OpenAPI document now say that the Microsoft Store version keeps `api.json` in
  `%LOCALAPPDATA%\IceScreenTouch`, as a build of your own does, when that folder already existed (they only named
  the folder inside the package).
- During the trial, Settings now shows the note that the Automation API can be used after you buy IceScreen Touch (it
  stayed hidden, because the note was not updated once the license was read).
- `IceScreenTouch.exe --self-check` no longer warns about the screenshot and borderless capture checks on a display
  scaled above 100% (it now measures windows in physical pixels, like the app).

## [0.3.0] - 2026-11-17

### Added

- The **Automation API**: scripts and AI agents on the same PC can operate the iPhone or iPad the way the mouse and
  keyboard do (tap, long press, swipe, pinch, type with a US keyboard, press keys and Home, set the rotation) and take
  screenshots of the receiver window. It is a REST (HTTP/JSON) and WebSocket server on `127.0.0.1` only that web pages
  cannot call, protected by a token, and **off by default**: switch it on in Settings > "Automation API", where the
  token is created, copied and regenerated. The user's own mouse and keys take priority and interrupt a script. It is
  available in the purchased Store version and in builds of your own, not in the trial, and it also stays off while
  the Store license cannot be checked (it starts by itself once the license is read). See `docs/API.md`.
- A Python client library and an MCP server for AI agents (`clients/python`; not on PyPI, installed from the repository
  with uv).

### Changed

- Builds of your own: while the receiver window is covered by other windows, the app now finds the picture with
  Windows.Graphics.Capture, which draws no border, so LonelyScreen no longer flickers. The Microsoft Store version keeps
  `PrintWindow`, because Windows does not allow a borderless capture there without asking the user.

## [0.2.0] - 2026-11-10

### Added

- More AirPlay receivers can be followed: AirServer from the Microsoft Store (its window is hosted by Windows' app
  frame), Reflector 4 and 5KPlayer (whose mirror window belongs to `Airplay.exe`). The Status tab lists them and can
  start them.
- Reflector 4 and 5KPlayer move their window when it is dragged. IceScreen Touch now keeps button presses over the
  picture from these receivers and brings their window to the front, so swipes and drags reach the phone.
  **Alt + click or drag** goes to the receiver instead (to move its window, for example) and is not sent to the
  phone, with any receiver.

### Changed

- The free trial of the Microsoft Store version is 7 days (it was 24 hours). The Status tab counts the time left in
  days from one day up ("Trial: 6 days left"), then in hours and minutes as before.
- The Status tab says when a device is connected to the receiver but its mirror window cannot be found (for
  example, when it is minimized), instead of asking to choose the receiver in Screen Mirroring.
- The Status tab names receivers as the receiver list does (5KPlayer, Reflector 4), not by their process name.
- Settings: the UxPlay field is now "Location of uxplay.exe" (the old "empty = find it automatically" only found a
  build inside the source repository).
- The user guide has a chapter on installing an AirPlay receiver (the five tested receivers, in name order), and
  `docs/BUILD-UXPLAY.md` explains how to build UxPlay on Windows. App texts no longer name one receiver as the
  example.

### Fixed

- With UxPlay, the Status tab kept the last picture size and device after the phone stopped mirroring or the
  connection was lost, and that device stayed the "mirrored" target for touches and keys. Both are now cleared when
  the session ends.

## [0.1.0] - 2026-11-08

The first release, in the Microsoft Store. The source code is public since 2026-11-08.

### Added

- Control an iPhone or iPad from the PC: the PC acts as a Bluetooth LE HID device (touch digitizer, home button,
  keyboard). The picture comes from an AirPlay receiver that is installed separately (UxPlay, AirServer,
  LonelyScreen); clicks and drags on its window become touches (iOS Zoom must be on).
- Right button = long press, Ctrl + drag = pinch, mouse wheel = scroll with momentum, Home = home screen,
  Ctrl+R = rotation; keys typed on the mirror window are typed on the device.
- Picture detection in the receiver's window, screen shape from the device model, one target device at a time.
- Status, settings, test (touch, scroll, latency and key probes on a Safari page) and log tabs; tray icon; English
  and Japanese; light and dark themes.
- About tab: version and commit, license, third-party components, data folder, the HID map and settings versions,
  and a button that copies the version info for a problem report.
- `tools\publish.ps1` makes the single-file `IceScreenTouch.exe` (no installer) and a zip with the user guide,
  `LICENSE`, `THIRD-PARTY-NOTICES.txt` and `TRADEMARKS.md`. The only official binary is the Microsoft Store
  version; the GitHub repository holds the source code (build it yourself for free).
- Microsoft Store package (MSIX): manifest and logos under `packaging/msix`, built by `tools/publish.ps1 -Msix`
  (unsigned; the Store signs it). In the packaged build the test page's firewall rule comes from the package, so the
  Test tab accepts it as it is and never shows the "Set up the firewall" button; the exe you build yourself is unchanged.
- Free 24-hour trial in the Microsoft Store version: the app checks its Store license at start and every few minutes,
  shows the time left and a Buy button on the Status tab, stops the session when the trial ends, and starts it again
  right after the purchase. The About tab shows the license. Builds you make yourself have no license check.

### Security

- Touch and key input goes only to devices that are paired with the PC: subscribing to the input reports needs an
  encrypted connection, and a subscriber that Windows does not report as paired gets no input, reads only empty
  reports and is never chosen as the target (it is checked again while it stays subscribed). A device that is not
  the target reads an empty input report.
- The test page needs a per-run key in its URL and accepts input only from the first device that sends with the key.
  Every test stops when the page leaves the front of the device.
- The elevated PowerShell for the firewall scripts gets its values as Base64, loads modules only from the Windows
  folder, and is started by its full path.
- The log never holds touch positions, typed keys or the mirroring key. Device names and IDs are logged only with the
  verbose log on (MAC and DACP IDs are masked), and BLE device IDs are shortened to a hash. UxPlay is found only
  inside the repository.
- Reverse lookups to find the mirrored device ask only peers on the PC's own subnet, and accept only valid host names.
- The Device Information service claims no vendor ID.
