# Building UxPlay on Windows

[日本語](BUILD-UXPLAY.ja.md)

[UxPlay](https://github.com/FDH2/UxPlay) is an open-source AirPlay receiver. The official repository provides source code, so on Windows you build it yourself. This page is for those who can use development tools: it uses the command line.

UxPlay is one of the AirPlay receivers that IceScreen Touch can follow (see [Installing an AirPlay receiver](USER-GUIDE.md#installing-an-airplay-receiver)). IceScreen Touch has no relationship whatsoever with the UxPlay project.

## License

UxPlay is under the GNU General Public License version 3 (GPLv3). It is a separate program and is not part of IceScreen Touch: `IceScreenTouch.exe` contains no UxPlay code. The UxPlay program you build here is yours to use under the GPLv3.

## Before you start

- Windows 10 or 11, 64-bit, and PowerShell.
- [scoop](https://scoop.sh/) installed. This page installs MSYS2 with scoop. (If you installed MSYS2 another way, see "Point IceScreen Touch to it" below for how to tell IceScreen Touch where it is.)
- Administrator rights are not needed. Only the firewall step, if you need it, needs them.
- Build the official source as it is. The commit that was tested is `b1be2bd` (UxPlay 1.74). A newer commit was not tested.

## 1. Install MSYS2

```powershell
scoop install msys2
```

## 2. Update MSYS2 and install the packages

The first update can end after updating the core, so run it twice.

```powershell
$env:MSYSTEM = 'UCRT64'
$env:CHERE_INVOKING = '1'
$bash = "$env:USERPROFILE\scoop\apps\msys2\current\usr\bin\bash.exe"

& $bash -lc 'pacman -Syu --noconfirm'
& $bash -lc 'pacman -Syu --noconfirm'
& $bash -lc 'pacman -S --needed --noconfirm mingw-w64-ucrt-x86_64-gcc mingw-w64-ucrt-x86_64-cmake mingw-w64-ucrt-x86_64-ninja mingw-w64-ucrt-x86_64-libplist mingw-w64-ucrt-x86_64-gstreamer mingw-w64-ucrt-x86_64-gst-plugins-base mingw-w64-ucrt-x86_64-gst-plugins-good mingw-w64-ucrt-x86_64-gst-plugins-bad mingw-w64-ucrt-x86_64-gst-libav mingw-w64-ucrt-x86_64-openssl mingw-w64-ucrt-x86_64-pkgconf git'
```

## 3. Get the source and build it

Continue in the same PowerShell window as step 2 (the commands use `$bash`). Pick a folder for it. Replace `<you>` with your Windows user name. The command below does not quote the path, so use a folder whose path has no spaces. In the MSYS2 path, `C:\Users\<you>\UxPlay-build` is written `/c/Users/<you>/UxPlay-build`.

```powershell
New-Item -ItemType Directory -Force 'C:\Users\<you>\UxPlay-build' | Out-Null
& $bash -lc 'cd /c/Users/<you>/UxPlay-build && git clone https://github.com/FDH2/UxPlay.git && cd UxPlay && git checkout b1be2bd && mkdir build && cd build && cmake -G Ninja .. && ninja'
```

The program is `C:\Users\<you>\UxPlay-build\UxPlay\build\uxplay.exe`. UxPlay 1.74 has its own mDNS, so the build does not use Bonjour.

## 4. First start and the firewall

On the first start, Windows asks whether to allow `uxplay.exe` through the firewall. **Allow it.** If you do not, Windows creates a block rule, and the iPhone cannot connect. A block rule wins over an allow rule.

To fix a block rule, open Windows PowerShell (the one built into Windows) **as administrator**; the commands below were checked with it. This removes the rules for `uxplay.exe` and adds one that accepts connections only from your local subnet (the second line can take a minute):

```powershell
$exe = 'C:\Users\<you>\UxPlay-build\UxPlay\build\uxplay.exe'
Get-NetFirewallRule -Direction Inbound | Where-Object { (($_ | Get-NetFirewallApplicationFilter).Program) -ieq $exe } | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'UxPlay' -Direction Inbound -Action Allow -Program $exe -Profile Any -Protocol Any -RemoteAddress LocalSubnet
```

## 5. Point IceScreen Touch to it

1. In IceScreen Touch, open the **Settings** tab.
2. Choose "Start UxPlay".
3. Press "Browse…" next to "Location of uxplay.exe" and choose the `uxplay.exe` you built.
4. "MSYS2 ucrt64\bin (empty = the default scoop location)" is inside the "Advanced (for testing and troubleshooting)" section of the **Settings** tab; open that section to see it. Leave it empty if you installed MSYS2 with scoop as above. If MSYS2 is somewhere else, enter its `ucrt64\bin` folder.
5. The "AirPlay name (shown in Screen Mirroring on the iPhone)" field sets the name shown in the iPhone's Screen Mirroring list (the default is "IceScreen Touch").
6. Press "Save and apply".

IceScreen Touch starts UxPlay for you, adds the decoder options for low latency, and stops UxPlay when it stops. On the iPhone, open Control Center > Screen Mirroring and choose the AirPlay name.

## Start it by hand (optional)

You can start UxPlay yourself and let IceScreen Touch follow its window. Put the MSYS2 `ucrt64\bin` folder on `PATH` first, and add the two decoder options. Without them the picture arrives late.

```powershell
$env:PATH = "$env:USERPROFILE\scoop\apps\msys2\current\ucrt64\bin;$env:PATH"
& 'C:\Users\<you>\UxPlay-build\UxPlay\build\uxplay.exe' -n "IceScreen Touch" -nh -vs d3d11videosink -vsync no -vd "d3d11h264dec compliance=flexible" -vc d3d11convert
```

Then, in the **Status** tab of IceScreen Touch, press **Use** on "UxPlay (running)". IceScreen Touch does not get the device's name from a UxPlay you started yourself, so a device whose name has no Latin letters or digits cannot be recognized as the mirrored device.
