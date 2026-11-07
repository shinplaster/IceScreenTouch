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

The source code is public since 2026-11-08. No build has been released yet; the first release will be 0.1.0.

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
