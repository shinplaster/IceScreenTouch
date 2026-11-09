# Architecture

IceScreen Touch lets you control an iPhone or iPad from a Windows PC: you click and drag on the mirrored screen, and the device receives real touches. This page outlines how it works and where the code lives. To install and use the app, see the [user guide](USER-GUIDE.md).

## Overview

```
 iPhone / iPad                                Windows PC
 ┌──────────────┐   AirPlay (Wi-Fi)    ┌──────────────────────────────┐
 │ Screen       │ ───────────────────▶ │ AirPlay receiver (separate   │
 │ Mirroring    │                      │ program: UxPlay, AirServer,  │
 │              │                      │ LonelyScreen, ...)           │
 │              │                      └──────────────┬───────────────┘
 │              │                                     │ its window
 │              │                      ┌──────────────▼───────────────┐
 │ Zoom         │   BLE HID (touch,    │ IceScreenTouch.exe           │
 │ (Accessibil- │ ◀─────────────────── │  finds the picture in the    │
 │  ity) on     │   home key, keys)    │  window, turns mouse and     │
 └──────────────┘                      │  keyboard into HID reports   │
                                       └──────────────────────────────┘
```

- **Video**: the app does not receive AirPlay itself. You run an AirPlay receiver of your choice, and the app follows its window. This keeps the receiver's code (and its license) out of the app: `tools/publish.ps1` checks on every build that no receiver code is in the published files.
- **Input**: iOS has no public API for injecting touches from outside. The PC instead presents itself to the device as a Bluetooth LE HID peripheral (HID over GATT). With **Settings > Accessibility > Zoom** turned on, iOS accepts the absolute-position digitizer reports as real touches.

## Input over Bluetooth LE

- **Report map** (263 bytes): a relative mouse (report 1), a two-contact absolute digitizer (report 2), consumer control (report 3; Menu 0x0C:0x0040 acts as the home button on iPhone and iPad), and a keyboard (report 4). Changing the map requires the device to forget and re-pair the PC, and iOS does not always re-read it, so the map is versioned and fixed by a test (`HidReports.ReportMapVersion`).
- **Coordinates**: 0 to 10000 covers the whole screen in the device's portrait orientation. In landscape the app rotates the point before sending it. iOS does not report which way the device is turned, so `Ctrl+R` switches between 90° and 270° (and between 0° and 180° in portrait).
- **Lifting a finger** sends the last position with the tip switch off. Sending (0,0) instead is read as a jump to the top-left corner.
- **Scrolling**: the mouse wheel becomes a swipe with the finger kept down, and the app produces the momentum itself. iOS's own momentum varies widely when BLE reports arrive in bursts.
- **Latency**: on Windows 11 the app asks for a connection without peripheral latency, which cut the touch delay from about 47 ms to about 14 ms in our measurement.
- **One target**: when several devices are connected, touches and keys go to one of them only: the device chosen in the app, else the one shown in the mirror window, else the one that connected last. The app switches targets only while no finger or key is down.
- **Identifying the mirrored device**: the app reads each device's model number (Device Information service) over the HID connection, and matches the Bluetooth name against the device name in the UxPlay log or the mDNS host name of the receiver's TCP peer (`TouchTargetSelector`, `MdnsReverseLookup`). The model number also selects the screen shape.

## Video: following the receiver window

- **UxPlay started by the app**: a small patch (`poc/poc1-uxplay/patches/`, GPLv3) makes UxPlay print the size it draws the picture at. UxPlay does not crop the picture, so this gives the exact rectangle. The patch is optional: without it the app reads the picture size from UxPlay's standard debug output (the codec packet lines; the app starts UxPlay with `-d` for this). An unpatched build was verified on a device on 2026-11-09.
- **A receiver that is already running**: the app captures the window and finds the picture by its black borders (`PictureLocator`). It reads the screen directly when the window is visible, and uses `PrintWindow` when it is covered.
- **Tested receivers**: only 5KPlayer, AirServer, LonelyScreen, Reflector 4 and UxPlay have been tested. The app can follow any other receiver by its process name, but such receivers are not tested or supported yet.
- **Receiver quirks** (`ReceiverQuirks`): AirServer resizes its own window to the picture; LonelyScreen draws a band along the bottom edge and uneven margins, and flickers on every `PrintWindow`. LonelyScreen counts as mirroring only while a device has a TCP connection to it, so its idle screen is never mistaken for a phone. Reflector 4 and 5KPlayer (its `Airplay.exe`) move their window when the picture is dragged (`MovesWindowOnDrag`): the app keeps the button presses over the picture from them, and Alt + click or drag goes through to the receiver instead of the phone. A receiver hosted by the Windows app frame (AirServer from the Microsoft Store) is followed by the `CoreWindow` inside its `ApplicationFrameWindow`.
- **Window shape**: for a UxPlay window that is already running, the app reshapes the window to the picture (`WindowShapeFollower`).

## Keyboard

While the mirror window is in front, a low-level keyboard hook sends keys to the device (`KeyboardForwarder` decides, `KeyboardRelay` builds the reports). `Home` is the home button, `Ctrl+R` rotates, `Ctrl` becomes ⌘, `Ctrl+Space` switches the input source, and combinations with `Alt` or `Win` stay with Windows. iOS treats the HID keyboard as US whatever its own layout setting says. Japanese is typed through the device's Romaji keyboard, which does the conversion.

## Projects

| Project | Role |
|---|---|
| `src/IceScreenTouch.Core` | Platform-neutral: HID reports, geometry and rotation, gestures and scroll momentum, picture locator, target selection, log privacy |
| `src/IceScreenTouch.Ble` | The BLE HID peripheral (Windows GATT server), connection tuning |
| `src/IceScreenTouch.Platform.Windows` | Win32: mouse and keyboard hooks, window capture, firewall rules, package detection |
| `src/IceScreenTouch.Video` | Receiver window sources (UxPlay started by the app, any window) and receiver quirks |
| `src/IceScreenTouch.App` | Avalonia UI and view models (platform-neutral); English and Japanese text in `Resources/Strings*.resx` |
| `src/IceScreenTouch.Windows` | `IceScreenTouch.exe`: wires the Windows services into the UI |
| `src/IceScreenTouch.Probe.Core` | The test pages (touch, scroll, latency, keys) opened in Safari on the device |
| `tools/IceScreenTouch.Probe` | Console tool: checks the Bluetooth adapter and runs the device tests |
| `tools/IceScreenTouch.Bridge` | Console version of the bridge |
| `tests/` | xUnit tests (a few move the real mouse and run only with `ICESCREENTOUCH_INPUT_TESTS=1`) |
| `packaging/msix/` | The Microsoft Store package manifest |
| `poc/` | The original proofs of concept in Python and the UxPlay patch |

The UI uses Avalonia so that the platform-neutral parts can later run on macOS and Linux; only the Windows parts exist today.

## Privacy and network

The app sends nothing to the internet. The test pages are served on TCP port 8000 only while a test is running; each run uses a one-time random key in the URL, and only the first device that uses it can post results. The firewall rule for the port allows only the local subnet in a build of your own, and is declared by the package in the Store build. Logs leave out session keys, touch positions, typed keys and the connected devices' Bluetooth identifiers (`LogPrivacy`). See the [privacy policy](PRIVACY.md).

## Limits

- The touch path depends on iOS's Zoom accepting digitizer input; an iOS update could change it.
- The two landscape orientations cannot be told apart automatically.
- You need a separate AirPlay receiver. Only 5KPlayer, AirServer, LonelyScreen, Reflector 4 and UxPlay are tested and supported so far.
