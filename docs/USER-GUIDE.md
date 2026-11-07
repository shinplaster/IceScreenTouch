# Using IceScreen Touch

[日本語](USER-GUIDE.ja.md)

IceScreen Touch lets you control an iPhone from its mirrored screen on a Windows PC, with the mouse and the keyboard. The PC poses as a Bluetooth touch input device and keyboard: clicks and drags on the mirrored screen go to the iPhone as touches, and the keys you type go to it as keyboard input.

**IceScreen Touch does not receive the video itself.** To show the iPhone's screen on the PC you need a separate AirPlay receiver (AirServer, for example). IceScreen Touch follows that program's window.

## What you need

| Item | Requirement |
|---|---|
| PC | Windows 10 (version 2004 or later) or Windows 11, 64-bit |
| Bluetooth | An adapter that can act as a BLE peripheral (the accessory role). The setup steps in IceScreen Touch's Status tab tell you whether yours can |
| iPhone / iPad | Tested with an iPhone (iOS 26) and an iPad Air (iPadOS 18). Connect it to the same network (Wi-Fi) as the PC |
| AirPlay receiver | A program that receives AirPlay and shows it on the PC (tested with UxPlay, AirServer and LonelyScreen; another receiver can be followed by its process name, but it is not tested or supported yet). Run only one receiver at a time: with two, the iPhone may end up in the other one |

Nothing else needs to be installed, not even .NET.

## Installing

Install IceScreen Touch from the Microsoft Store. The Store version is signed and updates itself.

The Store version has a free 24-hour trial, so you can check that your PC's Bluetooth works with it. The **Status** tab shows how much time is left. When the trial ends, the app stops working until you buy it in the Store; use the **Buy** button in the Status tab. It works again right away.

You can also build it yourself from the source code (see the README of the GitHub repository). A build of your own is a single file, `IceScreenTouch.exe`, and it is not code-signed:

- When you open it for the first time, Windows may show "Windows protected your PC". Click "More info", then "Run anyway".
- On a Windows 11 PC with **Smart App Control turned on**, unsigned programs cannot start (see Windows Security > App & browser control > Smart App Control).
- On a company PC, the administrator's policies may block it.

The Store version and a build of your own keep separate settings.

## First-time setup

1. Install and start an AirPlay receiver.
2. Start IceScreen Touch. The **Status** tab shows the state at the top (for example "Ready" or "Waiting for the iPhone"), what to do next, and the numbered **Setup** steps. Work through the steps from the top until each has a green dot. The AirPlay receivers found on this PC are listed under "AirPlay receivers on this PC" (it opens by itself when the receiver needs you).
3. **Bluetooth**: on the iPhone, open Settings > Bluetooth and choose this PC's name. The first time, confirm the pairing. After that it connects by itself. The list may show this PC's name twice. Pairing only one of them does not bring touches through, so choose and pair both.
4. **Zoom**: on the iPhone, turn on Settings > Accessibility > Zoom (there is no need to zoom in). **Without Zoom, touches do not arrive.**
5. **Screen Mirroring**: open Control Center on the iPhone, tap Screen Mirroring and choose the receiver's entry.
6. Click the mirrored screen. If the iPhone responds, you are done.

If touches do not arrive, check with **Test touches** in the **Test** tab (the Zoom step has a button that takes you there). Press the button first, then open the URL it shows in Safari on the iPhone.

**The shape of the screen** (iPhone, iPhone with a Home button, iPad) is told by the device over Bluetooth: "Screen of the mirrored device" in the **Settings** tab is "Automatic" by default. Receivers that do not report the picture size, such as AirServer and LonelyScreen, are searched for a picture of that shape. If touches land in the wrong place on a model IceScreen Touch does not know, choose the shape there yourself.

**If several devices are connected** (an iPhone and an iPad, say), touches and keys go to one of them only: the device the mirror window shows, or, when IceScreen Touch cannot tell, the device that connected last. "Input to" in the **Status** tab says which one and why, and lets you choose a device yourself (back to "Automatic" to undo). The device shown in the mirror window is recognized by its name; a name with no Latin letters or digits at all (all Japanese, for example) can be recognized only when IceScreen Touch starts UxPlay itself.

## Controls

| Mouse or key | On the iPhone |
|---|---|
| Left click, left drag | Tap, swipe (one finger) |
| Ctrl + left drag | Pinch (the second finger mirrors the first around the center of the screen) |
| Right click | Long press (you can keep dragging) |
| Wheel | Scroll (Shift for sideways) |
| Home key | Home (sends the Home button key) |
| Ctrl + R | Press it when you turned the device and touches land on the opposite side (90° ⇄ 270° in landscape, 0° ⇄ 180° in portrait) |
| Other keys | Typed on the iPhone (next section) |

The keys work only while the mirror window is in front. When the iPhone is turned sideways, IceScreen Touch cannot tell which of the two landscape orientations it is. If touches land on the opposite side, press Ctrl + R (or "Switch rotation" in the **Status** tab). Touches are reversed as well when an iPad is held upside down in portrait: pressing it in portrait switches 0° ⇄ 180° (180° is not saved; it goes back to 0° when the session starts again).

## Typing

While the mirror window is in front, the keys you type on the PC reach the iPhone (it sees a Bluetooth keyboard). Click an input field on the iPhone first. The on-screen keyboard stays, so you can still type with a finger.

| PC key | On the iPhone |
|---|---|
| Letters, digits, symbols, Enter, Backspace, Tab, Esc, arrows, Delete | The same |
| Ctrl + key | ⌘ + key (Ctrl+A selects all, Ctrl+C / Ctrl+V copy and paste, Ctrl+Z undoes) |
| Ctrl + Space, Zenkaku/Hankaku | Switch the input source (for example English ⇄ Japanese) |
| Anything with Alt or the Windows key, F1–F12, volume and media keys | Not sent: Windows keeps them (Alt+Tab and so on) |

- **Japanese**: add the "**Japanese – Romaji**" keyboard in Settings > General > Keyboard > Keyboards on the iPhone ("Japanese – Kana" alone types Latin letters from a hardware keyboard). Switch to it with Ctrl+Space, type in romaji, press Space to convert and Enter to commit. The iPhone does the conversion. The switch cycles through the keyboards you added, so press Ctrl+Space again if it did not land on Japanese.
- **Where the symbols are**: whether the PC's keyboard is US or JIS, the symbol you type on the PC is the one that appears (IceScreen Touch sends the keys as a US keyboard).
- **Full Keyboard Access on an iPad**: while it is on, Space and the arrow keys do nothing (the iPad uses them itself). Turn it off in Settings > Accessibility > Keyboards.
- **Typing in other programs**: keys are sent only while the mirror window is in front. To keep them on the PC, turn off "Send the keys typed in the mirror window to the device" in the **Settings** tab (Home and Ctrl+R still work).

## The Test tab

With a test page opened in Safari on the iPhone, IceScreen Touch can measure the following. **Press the button first**: the page can be opened only from the moment you press it until the measurement ends. Until the page is open, a **QR code** of its address is shown: point the iPhone's camera at it and tap the link that appears to open the page in Safari (no typing). For the iPhone to reach the page, the page's port (TCP 8000) must be let in. The Store version does this when it is installed. For a build of your own, press "Set up the firewall (administrator)" once: it lets the port in from the same network only (Windows asks for permission).

- **Test touches**: whether taps at 9 points, a swipe and a pinch land 1:1 on the iPhone's screen.
- **Measure scrolling**: how far and how smoothly the wheel scrolls.
- **Measure latency**: the time from a press to the response on the PC screen, split into the touch part and the picture part. Keep the mirror window uncovered while it measures.
- **Test typing**: whether letters, digits, symbols, Enter, arrows, Backspace and ⌘A / ⌘C / ⌘V typed into the page's box arrive as they should. After opening the page, **tap the box so the cursor is in it** (that starts the test). The iPhone's input must be English. If only the symbols come out as on a JIS layout, it asks you to set the device's hardware keyboard layout to QWERTY (US). If only Space and the arrows do not arrive, it asks you to turn off Full Keyboard Access. Japanese conversion is not tested.

## Settings and logs

- Settings: `settings.json` in the data folder (change them in the **Settings** tab). The data folder is `%LOCALAPPDATA%\IceScreenTouch` for a build of your own; the Store version keeps it inside its package folder. The **About** tab shows the folder and opens it.
- Language: IceScreen Touch follows the Windows display language (Japanese for Japanese, English otherwise). You can also choose English or 日本語 under "Language" in the **Settings** tab; a new choice takes effect the next time IceScreen Touch starts.
- Light or dark: IceScreen Touch follows the Windows app mode. You can also choose Light or Dark under "Appearance (light / dark)" in the **Settings** tab; it changes as soon as you press "Save and apply".
- Logs: `logs\` in the data folder ("Open the log folder" in the **Log** tab)
- Version and licenses: the **About** tab (version, license, third-party software, data folder). When you report a problem, paste the text that "Copy version info" copies.
- The environment variable `ICESCREENTOUCH_DATA_DIR` moves the settings and logs to another folder.

## Troubleshooting

| Problem | What to check |
|---|---|
| "Bluetooth adapter" is red | The adapter cannot act as a BLE peripheral. A USB adapter that can is needed |
| The PC does not appear in the iPhone's Bluetooth list | Is IceScreen Touch running? Is another program that publishes a BLE HID service (an older IceScreen Touch, for example) running? |
| The iPhone does not respond to clicks | On the iPhone, is Settings > Accessibility > Zoom turned on? Does step 2 "Connect the iPhone (Bluetooth)" in the **Status** tab have a green dot? |
| Touches land in the wrong place | If the receiver puts margins around the picture, turn on "Find the picture inside the window" in the **Settings** tab. Check "Screen of the mirrored device" there as well |
| The wrong device responds | Check "Input to" in the **Status** tab, and choose the device there if needed |
| Space and the arrow keys do nothing (iPad) | Turn off "Full Keyboard Access" in Settings > Accessibility > Keyboards |
| Touches are reversed on an iPad held upside down | Press Ctrl + R (or "Switch rotation") while it is in portrait |
| The mirrored picture flickers | IceScreen Touch looks at the receiver's window now and then. While another window covers part of it, some receivers (LonelyScreen) flicker; keep the mirror window uncovered |
| LonelyScreen says it is blocked by the firewall | If mirroring works, it is not: LonelyScreen also says so after its firewall rule was narrowed to the local network |
| It worked before but no longer connects | On the iPhone, open Settings > Bluetooth, forget this PC and pair again |
| After an update, neither touches nor Home work | Versions of IceScreen Touch may present a different input device to the iPhone (a Home key and a keyboard were added during 0.1.0). On the iPhone, forget this PC and pair again. If that does not help, also remove the iPhone in Windows Settings > Bluetooth & devices, then pair again |

## License and notes

- IceScreen Touch is under the MIT License (`LICENSE.txt`) and comes **without warranty**. It relies on the way the iPhone's Zoom accepts touches, so an iOS update may stop it from working.
- The licenses of the bundled components are in `THIRD-PARTY-NOTICES.txt`. IceScreen Touch contains no AirPlay receiver (no UxPlay, no FairPlay code).
- The name and icon are not covered by the MIT License (see [TRADEMARKS.md](../TRADEMARKS.md)).
- IceScreen Touch is not affiliated with Apple Inc. iPhone, iPad and AirPlay are trademarks of Apple Inc.
