# Using IceScreen Touch

[日本語](USER-GUIDE.ja.md)

IceScreen Touch lets you control an iPhone from its mirrored screen on a Windows PC, with the mouse and the keyboard. The PC poses as a Bluetooth touch input device and keyboard: clicks and drags on the mirrored screen go to the iPhone as touches, and the keys you type go to it as keyboard input.

**IceScreen Touch does not receive the video itself.** To show the iPhone's screen on the PC you need a separate AirPlay receiver (see [Installing an AirPlay receiver](#installing-an-airplay-receiver)). IceScreen Touch follows that program's window.

## What you need

| Item | Requirement |
|---|---|
| PC | Windows 10 (version 2004 or later) or Windows 11, 64-bit |
| Bluetooth | An adapter that can act as a BLE peripheral (the accessory role). The setup steps in IceScreen Touch's Status tab tell you whether yours can |
| iPhone / iPad | Tested with an iPhone (iOS 26) and an iPad Air (iPadOS 18). Connect it to the same network (Wi-Fi) as the PC |
| AirPlay receiver | A program that receives AirPlay and shows it on the PC. Tested: 5KPlayer, AirServer, LonelyScreen, Reflector 4 and UxPlay (how to get each one: [Installing an AirPlay receiver](#installing-an-airplay-receiver)). Another receiver can be followed by its process name, but it is not tested or supported yet. Run only one receiver at a time: with several running, an entry can be missing from the iPhone's list or the iPhone may end up in another one |

Nothing else needs to be installed, not even .NET.

## Installing

Install IceScreen Touch from the Microsoft Store. The Store version is signed and updates itself.

The Store version has a free 24-hour trial, so you can check that your PC's Bluetooth works with it. The **Status** tab shows how much time is left. When the trial ends, the app stops working until you buy it in the Store; use the **Buy** button in the Status tab. It works again right away.

You can also build it yourself from the source code (see the README of the GitHub repository). A build of your own is a single file, `IceScreenTouch.exe`, and it is not code-signed:

- When you open it for the first time, Windows may show "Windows protected your PC". Click "More info", then "Run anyway".
- On a Windows 11 PC with **Smart App Control turned on**, unsigned programs cannot start (see Windows Security > App & browser control > Smart App Control).
- On a company PC, the administrator's policies may block it.

The Store version and a build of your own keep separate settings.

## Installing an AirPlay receiver

IceScreen Touch does not receive the video. A separate program, an AirPlay receiver, shows the iPhone's screen on the PC, and IceScreen Touch follows its window. Install one receiver before the setup steps below.

**About this list**

- IceScreen Touch has no relationship whatsoever with the developers of these programs. It receives no referral fees or any other payments from them.
- The programs are listed in name order. The order implies no ranking.
- The links go to the developers' official sites only. Prices and terms are the ones shown on the official sites in November 2026. They can change, so check the official site before you download or buy.
- Each program is a separate product with its own license, terms and support. The names are trademarks of their owners.
- They were tested on Windows 11 with an iPhone 15 Pro. Other setups were not tested.

**What applies to all of them**

- Run **only one** receiver at a time. Several receivers running together can get in each other's way, and it is not guaranteed to work. In a test, with UxPlay running together with LonelyScreen and Reflector 4, the iPhone's Screen Mirroring list showed only "LonelyScreen" and the PC's name; UxPlay's name was missing until the other two were closed.
- The iPhone and the PC must be on the same network.
- Each receiver shows its own entry in the iPhone's list (Control Center > Screen Mirroring). The name is given in its section below.
- In IceScreen Touch, the **Status** tab lists the receivers on this PC under "AirPlay receivers on this PC": the ones installed in their usual folders and the ones that are running. Press **Start** to start one, then **Use** so that IceScreen Touch follows it (IceScreen Touch restarts its session). The list updates by itself, or press **Search again**. A receiver installed in another folder appears only while it is running.
- AirServer from the Microsoft Store can be followed only by IceScreen Touch **0.2.0 or later**. From 0.2.0, 5KPlayer and Reflector 4 are in the receiver list, and dragging over the picture does not move their window.

**At a glance**

Each receiver's name links to its developer's official site.

| Receiver | Developer | Cost (official site, November 2026) | Trial | Tested version | How to install | Notes |
|---|---|---|---|---|---|---|
| [5KPlayer](https://www.5kplayer.com/) | DearMob / Digiarty | Free | Not applicable (free) | 6.11 | Installer from the official site | Shows a free-registration dialog on start; mirroring worked without registering (the dialog was closed) |
| [AirServer](https://www.airserver.com/) | App Dynamic | US$19.99 per PC ([consumer license](https://airserver.com/Buy/Consumer)) | The Microsoft Store edition shows "Your AirServer trial will expire in 31 days" with a "Continue Trial" button on first start | Microsoft Store edition: 2025.2.20<br>Installer edition (AirServer Universal): 5.7.2 | Microsoft Store edition, or the installer edition with winget | Two editions (Store and installer); IceScreen Touch lists them as one "AirServer" |
| [LonelyScreen](https://www.lonelyscreen.com/) | IMTIGER Technologies | US$19.95 per year (Personal Use, a yearly subscription) | A free trial can be downloaded from the [download page](https://www.lonelyscreen.com/download.html) | 1.2.16 | Small downloader from the official site | A firewall prompt appears on first start |
| [Reflector 4](https://www.airsquirrels.com/reflector) | Squirrels | Not shown on the official product page | 7 days, with a "TRIAL MODE" watermark over the picture until you buy it ([trial page](https://www.airsquirrels.com/reflector/try)) | 4.1.3 | Installer from the official site, or winget | Turn off "Show Frame" once (see below) |
| [UxPlay](https://github.com/FDH2/UxPlay) | The UxPlay project | Free (open source, GPLv3) | Not applicable | The source at commit `b1be2bd` (UxPlay 1.74) | Build it yourself from the source code | For those who can build it themselves |

### 5KPlayer

1. Open the Windows download page, <https://www.5kplayer.com/5kplayer-download-windows.htm>, and download the installer (`5kplayer-64bit.exe`, signed by Digiarty Software, Inc.). Run it.
2. On first start, 5KPlayer shows a free-registration dialog that asks for a country, a name and an e-mail address. Mirroring worked without registering: the dialog was closed.
3. On the iPhone, the entry in Screen Mirroring is "5KPlayer-" followed by the PC's name.
4. In IceScreen Touch, "5KPlayer" appears in the **Status** tab list. **Start** starts 5KPlayer. The picture is shown by a separate program, `Airplay.exe`, which 5KPlayer starts (closing 5KPlayer ends it). The mirror window opens when you start mirroring and closes when mirroring stops. Press **Use**.
5. The mirror window has no title bar. While IceScreen Touch is in control, dragging the picture does not move it: IceScreen Touch sends the drag to the iPhone, so swipes work. If you want to move the window, hold **Alt** and drag.

### AirServer

1. Install one of the two editions. Both were tested.
   - **Microsoft Store edition**: search for "AirServer Windows Desktop Edition" (Store product ID 9NBLGGH52639). The official download page, <https://www.airserver.com/download>, sends Windows 10 users to the Microsoft Store. This edition needs IceScreen Touch 0.2.0 or later.
   - **Installer edition** (AirServer Universal): install it with `winget install --id AppDynamic.AirServer -e`.
2. On first start, the Microsoft Store edition shows a trial screen with a "Continue Trial" button. Choose it to try AirServer.
3. On the iPhone, the entry in Screen Mirroring is the PC's name.
4. In IceScreen Touch, both editions appear as one row, "AirServer". Press **Use**.

### LonelyScreen

1. Download `lonelyscreen-win-installer.exe` from the [download page](https://www.lonelyscreen.com/download.html) (a small downloader) and run it. LonelyScreen is installed in `C:\Program Files (x86)\LonelyScreen`.
2. On first start, Windows shows a firewall prompt that asks whether to allow access. Allow it.
3. On the iPhone, the entry in Screen Mirroring is "LonelyScreen".
4. In IceScreen Touch, "LonelyScreen" appears in the **Status** tab list. Press **Start**, then **Use**.
5. Two LonelyScreen messages are covered in [Troubleshooting](#troubleshooting): flicker, and "blocked by the firewall".

### Reflector 4

1. Download the trial from the [trial page](https://www.airsquirrels.com/reflector/try) and run the installer. It can also be installed with `winget install --id Squirrels.Reflector.4 -e`.
2. The installer creates its own firewall rule. No prompt appeared on first start.
3. On the iPhone, the entry in Screen Mirroring is the PC's name.
4. In IceScreen Touch, "Reflector 4" appears in the **Status** tab list. Press **Start**, then **Use**. The mirror window opens separately (its title is "Reflector 4 - " followed by the device name) and closes when mirroring stops.
5. **Turn off the device frame once.** By default Reflector 4 draws a frame around the picture. With the frame shown, IceScreen Touch takes the frame as part of the picture, so taps can land away from where you click. While mirroring, open the settings from the gear icon on the bar under the mirror window (or from the device row's drop-down in the small Reflector window), choose "Frame" and uncheck "Show Frame". The setting is saved.
6. Reflector 4 moves its mirror window when it is dragged. IceScreen Touch (0.2.0 or later) keeps drags over the picture from Reflector 4 and sends them to the iPhone, so the window stays put and swipes work. If you want to move the window, hold **Alt** and drag.

### UxPlay (for those who can build it themselves)

1. You build UxPlay yourself from the source code. The steps are in [Building UxPlay on Windows](BUILD-UXPLAY.md).
2. On first start, Windows shows a firewall prompt for `uxplay.exe`. Allow it. If you do not, Windows creates a block rule, and the iPhone cannot connect.
3. On the iPhone, the entry in Screen Mirroring is the **AirPlay name** set in IceScreen Touch's **Settings** tab (the default is "IceScreen Touch").
4. IceScreen Touch can start UxPlay itself: in the **Settings** tab choose "Start UxPlay" and set the location of `uxplay.exe` (details in the building guide). It then adds the decoder options for low latency by itself.

## First-time setup

1. Install an AirPlay receiver and start it (see [Installing an AirPlay receiver](#installing-an-airplay-receiver)). Run only one.
2. Start IceScreen Touch. The **Status** tab shows the state at the top (for example "Ready" or "Waiting for the iPhone"), what to do next, and the numbered **Setup** steps. Work through the steps from the top until each has a green dot. The AirPlay receivers found on this PC are listed under "AirPlay receivers on this PC" (it opens by itself when the receiver needs you). Press **Use** on the one you installed.
3. **Bluetooth**: on the iPhone, open Settings > Bluetooth and choose this PC's name. The first time, confirm the pairing. After that it connects by itself. The list may show this PC's name twice. Pairing only one of them does not bring touches through, so choose and pair both.
4. **Zoom**: on the iPhone, turn on Settings > Accessibility > Zoom (there is no need to zoom in). **Without Zoom, touches do not arrive.**
5. **Screen Mirroring**: open Control Center on the iPhone, tap Screen Mirroring and choose the receiver's entry (the name each receiver shows is in its section above).
6. Click the mirrored screen. If the iPhone responds, you are done.

If touches do not arrive, check with **Test touches** in the **Test** tab (the Zoom step has a button that takes you there). Press the button first, then open the URL it shows in Safari on the iPhone.

**The shape of the screen** (iPhone, iPhone with a Home button, iPad) is told by the device over Bluetooth: "Screen of the mirrored device" in the **Settings** tab is "Automatic" by default. Receivers that do not report the picture size, such as AirServer and LonelyScreen, are searched for a picture of that shape. If touches land in the wrong place on a model IceScreen Touch does not know, choose the shape there yourself.

**If several devices are connected** (an iPhone and an iPad, say), touches and keys go to one of them only: the device the mirror window shows, or, when IceScreen Touch cannot tell, the device that connected last. "Input to" in the **Status** tab says which one and why, and lets you choose a device yourself (back to "Automatic" to undo). The device shown in the mirror window is recognized by its name; a name with no Latin letters or digits at all (all Japanese, for example) can be recognized only when IceScreen Touch starts UxPlay itself.

## Controls

| Mouse or key | On the iPhone |
|---|---|
| Left click, left drag | Tap, swipe (one finger) |
| Ctrl + left drag | Pinch (the second finger mirrors the first around the center of the screen) |
| Alt + click or drag | Passed to the receiver, not sent to the iPhone (to move its window, for example). Works with any receiver (IceScreen Touch 0.2.0 or later) |
| Right click | Long press (you can keep dragging) |
| Wheel | Scroll (Shift for sideways) |
| Home key | Home (sends the Home button key) |
| Ctrl + R | Press it when you turned the device and touches land on the opposite side (90° ⇄ 270° in landscape, 0° ⇄ 180° in portrait) |
| Other keys | Typed on the iPhone (next section) |

The keys work only while the mirror window is in front. When the iPhone is turned sideways, IceScreen Touch cannot tell which of the two landscape orientations it is. If touches land on the opposite side, press Ctrl + R (or "Switch rotation" in the **Status** tab). Touches are reversed as well when an iPad is held upside down in portrait: pressing it in portrait switches 0° ⇄ 180° (180° is not saved; it goes back to 0° when the session starts again).

Reflector 4 and 5KPlayer move their window when you drag the picture. IceScreen Touch keeps those drags for the iPhone, so to move the window of those receivers, hold Alt and drag it.

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
| The receiver does not appear in the iPhone's Screen Mirroring list | Is the receiver running, and is it the only one? With several receivers running, an entry can be missing (in a test, UxPlay's name was missing while LonelyScreen and Reflector 4 were running, and appeared after they were closed). Are the iPhone and the PC on the same network? If Windows asked to allow the receiver through the firewall, was it allowed? (For UxPlay, not allowing it creates a block rule, and the iPhone cannot connect: see [Building UxPlay on Windows](BUILD-UXPLAY.md).) |
| Taps land away from where I click (Reflector 4) | Reflector 4 draws a device frame around the picture by default, and with it taps can land away from where you click. Turn off "Show Frame" in its settings (see [Reflector 4](#reflector-4)) |
| I cannot move the receiver's window by dragging the picture (Reflector 4, 5KPlayer) | Hold **Alt** and drag. A drag without Alt goes to the iPhone |
| The Status tab says "a device is connected, but its window was not found" | The mirror window may be minimized. Restore it. This message needs IceScreen Touch 0.2.0 or later |

## License and notes

- IceScreen Touch is under the MIT License (`LICENSE.txt`) and comes **without warranty**. It relies on the way the iPhone's Zoom accepts touches, so an iOS update may stop it from working.
- The licenses of the bundled components are in `THIRD-PARTY-NOTICES.txt`. IceScreen Touch contains no AirPlay receiver (no UxPlay, no FairPlay code).
- The name and icon are not covered by the MIT License (see [TRADEMARKS.md](../TRADEMARKS.md)).
- IceScreen Touch is not affiliated with Apple Inc. iPhone, iPad and AirPlay are trademarks of Apple Inc.
