# IceScreen Touch Privacy Policy

[日本語](PRIVACY.ja.md)

Effective date: 2026-11-08

This policy covers IceScreen Touch ("the app"), both the Microsoft Store version and a build you make yourself from the source code on GitHub. Both are built from the same source code and behave the same way with respect to data.

## In short

- The app does not send any data to the developer or to any other third party. It has no accounts, no advertising, no analytics, no crash reporting and no update check.
- Everything the app does with your input stays between your PC and your own iPhone or iPad, over Bluetooth and your local network.
- The app keeps a small settings file and log files on your PC. They are not uploaded anywhere. You can delete them at any time.

## What the app does

IceScreen Touch lets you control an iPhone or iPad from its mirrored screen on a Windows PC. A separate AirPlay receiver app on the PC shows the mirrored screen. IceScreen Touch follows that app's window, and turns mouse clicks, the mouse wheel and keys on the mirrored screen into Bluetooth touch and keyboard input, which the PC sends to your device while posing as a Bluetooth input device.

## What is stored on your PC

The app writes your data only to its own data folder, `%LOCALAPPDATA%\IceScreenTouch` (the About tab shows the folder and has a button to open it). If the environment variable `ICESCREENTOUCH_DATA_DIR` is set, that folder is used instead. In the Microsoft Store version, Windows may redirect this folder to the package's own storage, under `%LOCALAPPDATA%\Packages\<package family name>\`.

| File | What it holds |
|---|---|
| `settings.json` | Your settings: the name the app announces for AirPlay, which receiver app to use and its file path, the screen shape, the rotation, scroll and long-press options, whether keys are sent, the language and theme, and the position of the app window, plus some technical options. If you pick the device that receives input on the Status tab, it also holds that device's Bluetooth identifier and its Bluetooth name (for example "Taro's iPhone"). |
| `logs\IceScreenTouch-<date>-<time>.log` | A diagnostic log, one file per run. The app keeps the latest 10 and deletes older ones. |
| `logs\uxplay-<date>-<time>.log` | Only if you turn on the detailed log and the app starts UxPlay itself: the output of UxPlay. The latest 10 are kept. |

Besides the data folder, the following use the Windows temp folder, and none of it is your data. In both builds, the first time the app starts, .NET extracts the app's native libraries (graphics and text rendering) to `%TEMP%\.net`. The diagnostic switch `--self-check` (run from a terminal; the app does not use it on its own) makes a temporary folder for a test file and deletes it when it finishes.

In a build you make yourself (not the Store build), during the firewall setup for the test page (see below), the app also writes a short temporary file in the Windows temp folder and deletes it when the setup ends. The Store build does not do this.

### What the log contains

The log is meant for finding problems. It contains times, the app version, and what the app is doing. It can also contain:

- names and file paths of the receiver apps it found or started, and of the app's own log files (a path can include your Windows user name);
- the Bluetooth name and the model identifier (such as `iPhone16,1`) of the devices connected to the PC. Raw Bluetooth device identifiers are not written; they are replaced by a short code that cannot be turned back into the identifier;
- the local network addresses (IP addresses) and host names of the devices connected to the receiver app, and the PC's own local address in the address of the test page;
- the name and model of the device that UxPlay reports, when the app starts UxPlay;
- if the PC cannot act as a Bluetooth peripheral, the PC's Bluetooth adapter address.

The log does **not** contain the keys you type (even the detailed log only records how many keys are held at a moment, never which keys), the positions of your clicks or touches, or any picture of the screen.

If you attach a log to a bug report, please look through it first. The detailed UxPlay log, which is off by default, also contains what UxPlay prints about the connection (for example the device's name, its operating system build and its network addresses). The app removes the hardware-style identifiers and the session keys that UxPlay prints, but not everything.

### How long data is kept

Settings stay until you delete them. Logs are limited to the latest 10 files of each kind (see above). Nothing else is kept: touches, key presses and screen pictures are processed in memory and are not saved.

## Keyboard and mouse input

To work, the app installs two low-level Windows hooks (keyboard and mouse) while a session is running (it starts when the app starts, and lasts until you press Stop or exit). A hook lets a program see input events that go to other programs. The app uses them only for the following:

- **Keyboard.** The hook is notified of every key press and release on the PC. The app acts on a key only while the mirror window is the window in front. Then it sends the key to the device you chose, over Bluetooth, and Windows does not receive that key (Alt and Windows key combinations, function keys and media keys stay with Windows). While another window is in front, the app only notes which keys are held (not stored, not logged), so that its Home and rotate shortcuts work, and passes every key on unchanged. If you turn off "Send the keys typed in the mirror window to the device" in the Settings tab, no key goes to the device (only the Home and rotate shortcuts are still taken in the mirror window).
- **Mouse.** The hook is notified of every left or right button press, release and wheel turn. The app uses an event only if it is over the visible mirror picture, and ignores the others without keeping them. It never blocks or changes mouse input, and it does not track plain mouse movement through the hook.

Whatever you type in the mirror window, including passwords, goes over Bluetooth to the one device chosen as the input target and is not stored by the app. The app sends input only to devices that have paired with the PC and subscribed over an encrypted connection. Pair only your own devices with the PC, and check the input target on the Status tab.

You can stop the session (the Stop button, or the tray menu), which removes both hooks and stops the Bluetooth input device, or close the app from the tray menu (Exit). Closing the app window alone only hides it in the notification area (a setting, on by default), and the session keeps running.

## Screen content

To find where the picture is inside the receiver app's window, the app reads the pixels of that window from the screen, and in the latency test it reads the brightness of a small square of the screen. These pixels are examined in memory and discarded. They are not saved, recorded or sent anywhere. The app does not record the screen, take screenshots, or look at any other window's content.

## Bluetooth

- The PC acts as a Bluetooth Low Energy input device (touch, mouse, a Home key and a keyboard) while a session is running (it starts when the app starts, and lasts until you press Stop or exit). It can be found in the Bluetooth list of nearby devices. Only a device that has paired with the PC, over an encrypted connection, can receive its touch and key input; pairing needs approval on both the device and the PC. The app sends input to one device only: the input target shown on the Status tab.
- The app announces the manufacturer name "IceScreen Touch" in the device information and a fixed battery level. It does not send your name, your PC's serial number or similar details.
- From a connected iPhone or iPad the app reads its Bluetooth name and its model identifier (the standard "Model Number" item of the device information, such as `iPhone16,1`). It uses them to tell which device is which and to choose the screen shape. They are kept in memory and written to the log as described above; the name is also saved in `settings.json` if you choose that device on the Status tab.
- The pairing itself is stored by Windows and by your device, not by the app. You can remove it in Windows Settings > Bluetooth & devices and in the device's Bluetooth settings.

## Local network

The app does not connect to the internet. On your local network it does the following:

- **Finding out which device the receiver shows.** The app looks at which devices on your local network are connected to the receiver app (from the system's connection table), and asks such a device for its host name with a small local network (mDNS) query sent straight to that device. Only addresses inside the PC's own networks are asked.
- **Test page.** When you press a test button (touch, scroll, latency, typing), the app starts a small web server on TCP port 8000 on the PC. It runs only until the test ends. You open it from the iPhone or iPad in Safari (the app shows a QR code). The page sends back touch and key events to the PC for the measurement. They are kept in memory for the test, not saved, and are discarded when the test ends. Each test run uses a one-time random key, and only the first device that uses it can post results. For the iPhone to reach the page, Windows Firewall must let TCP port 8000 in, and this works differently in the two builds:
  - **Store (MSIX) build:** the package declares an inbound rule for TCP port 8000 (for all network profiles, from any remote address), which Windows creates when the app is installed and removes when it is uninstalled. The app only reads the firewall rules to show their state; it never changes them, and it writes no temporary file for this. The web server listens only while a test runs, each run needs a random key in the URL, and only the first device that sends an event with the key is accepted.
  - **A build of your own:** the Test tab has a button for this; Windows asks for administrator permission before anything is changed, the rule allows only the local subnet, and the app writes the short temporary file mentioned above while it runs.
- **The receiver app** you use shows the picture. See the next section.

## Other software and services

- **The AirPlay receiver app** (AirServer, UxPlay and so on) is a separate product. It is not part of IceScreen Touch and has its own privacy policy and its own network use. IceScreen Touch can start the receiver app for you when you ask it to, and follows its window; it does not read what the receiver app stores. If the app starts UxPlay itself, UxPlay runs on your PC as a child process, and its output is read by IceScreen Touch (see the log above).
- **Your iPhone or iPad and Apple.** The app does not contact Apple. What your device does with the touches and keys is governed by Apple's terms and policy and your device settings (for example, Accessibility > Zoom must be on for the touches to work).
- **Links.** The About tab has buttons that open the project's GitHub page and its issue page in your browser. They do nothing until you click them. After that, GitHub's privacy statement applies.
- **Microsoft Store and GitHub.** If you get the app from the Microsoft Store, Microsoft may collect data about the download and the use of the package under its own terms; the developer may receive reports from Microsoft in aggregated form. If you download the source code from GitHub, GitHub's terms apply to the download. The app itself does not report anything to either.

## Deleting your data

1. Close the app (tray menu > Exit).
2. Uninstall it (Windows Settings > Apps). For the Microsoft Store version, Windows may remove the app's data with it, if the data is in the package's own storage; for a build of your own, only delete the folder where you put the exe.
3. Delete the data folder: `%LOCALAPPDATA%\IceScreenTouch`. If the app was installed from the Microsoft Store, data may also be under `%LOCALAPPDATA%\Packages\<package family name>\LocalCache\Local\IceScreenTouch`; delete it too if it is still there. If you used an earlier version of the app under its former name (iScrMirror), also delete `%LOCALAPPDATA%\iScrMirror` if present.
4. If you no longer want the pairing, remove the PC from the Bluetooth list on your iPhone or iPad and in Windows Settings > Bluetooth & devices.

Because the app sends nothing to the developer, there is no data held by the developer to delete.

## Changes to this policy

If the app starts to handle data differently, this policy will be changed before that version is released, and the date at the top will be updated. The current text is always in the project repository (`docs/PRIVACY.md`) and, for the Store version, on the Store page.

## Contact

Questions about privacy: [GitHub Issues](https://github.com/shinplaster/IceScreenTouch/issues)
