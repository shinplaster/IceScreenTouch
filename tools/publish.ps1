<#
.SYNOPSIS
    Builds the distribution of IceScreen Touch: one self-contained IceScreenTouch.exe, checked and zipped (and, with
    -Msix, packed as an MSIX package for the Microsoft Store).

.DESCRIPTION
    1. dotnet publish with the win-x64 profile (self-contained, single file, trimmed, compressed).
    2. Checks what went into the bundle: only IceScreen Touch's own assemblies and the known packages, and no
       receiver code (UxPlay, GStreamer, FairPlay / AirPlay, MSYS2, OpenSSL, plist). The publish folder must
       hold IceScreenTouch.exe only.
    3. Writes the third-party notices from the packages in the bundle and compares them with THIRD-PARTY-NOTICES.txt
       at the repository root, which the exe embeds for its About tab. When they differ (packages changed), the
       file is rewritten and steps 1-3 run again; commit the new file.
    4. Runs "IceScreenTouch.exe --self-check" (JSON, firewall COM, WinRT Bluetooth, Kestrel, P/Invoke, About).
    5. Zips the exe, LICENSE, CHANGELOG.md, THIRD-PARTY-NOTICES.txt and the user guides (README.md in English, README.ja.md in
       Japanese) into artifacts\IceScreenTouch-<version>-win-x64.zip.
    6. With -Msix: lays out the package under artifacts\msix\layout (the exe, packaging\msix\Package.appxmanifest
       with its placeholders filled in as AppxManifest.xml, the logo images from packaging\msix\Assets, LICENSE.txt and
       THIRD-PARTY-NOTICES.txt), builds resources.pri with makepri (the scale and targetsize qualifiers of the logos
       need it) and packs artifacts\IceScreenTouch-<version>-x64.msix with makeappx. The MSIX is NOT signed: the
       Microsoft Store signs it. To try it on this PC, use tools\msix_test_install.ps1. The MSIX version is the
       product version as X.Y.Z.0.

    Without -Msix nothing about the MSIX is touched: the exe and the zip come out exactly as before.

.PARAMETER NoZip
    Skip the zip (publish and checks only; -Msix still packs the MSIX).

.PARAMETER Msix
    Also build the MSIX package (see step 6).

.PARAMETER IdentityName
    -Msix: Identity Name of the package. The Store value comes from the Partner Center reservation of the app name
    (Product identity). The default is for local test packages only.

.PARAMETER Publisher
    -Msix: Identity Publisher (a distinguished name such as "CN=0123ABCD-..."), from Partner Center. The default is
    for local test packages only (tools\msix_test_install.ps1 signs with a certificate of this subject).

.PARAMETER PublisherDisplayName
    -Msix: the publisher name shown in the Store (Partner Center). The default is for local test packages only.

.EXAMPLE
    .\tools\publish.ps1

.EXAMPLE
    .\tools\publish.ps1 -Msix

.EXAMPLE
    .\tools\publish.ps1 -Msix -IdentityName 'Publisher.IceScreenTouch' -Publisher 'CN=00000000-0000-0000-0000-000000000000' -PublisherDisplayName 'Name'
#>
[CmdletBinding()]
param(
    # Skip the zip (publish and checks only).
    [switch]$NoZip,

    # Also build the MSIX package for the Microsoft Store.
    [switch]$Msix,

    # MSIX identity: the defaults make a package for local tests only, never for the Store.
    [string]$IdentityName = 'IceScreenTouch.Test',
    [string]$Publisher = 'CN=IceScreenTouch Test',
    [string]$PublisherDisplayName = 'IceScreenTouch Test'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\IceScreenTouch.Windows\IceScreenTouch.Windows.csproj'
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish\win-x64'
$manifestPath = Join-Path $artifacts 'publish\bundle-files-win-x64.txt'
$exe = Join-Path $publishDir 'IceScreenTouch.exe'

# Packages that may be in the bundle, with what the notices file says about each. A package that is not
# listed fails the check: look at its license before adding it here.
$knownPackages = [ordered]@{
    'Microsoft.NETCore.App.Runtime.win-x64'    = @{ Name = '.NET Runtime'; License = 'MIT'; Url = 'https://github.com/dotnet/runtime' }
    'Microsoft.AspNetCore.App.Runtime.win-x64' = @{ Name = 'ASP.NET Core Runtime'; License = 'MIT'; Url = 'https://github.com/dotnet/aspnetcore' }
    'Microsoft.Windows.SDK.NET.Ref'            = @{ Name = 'Windows SDK projection for .NET (C#/WinRT)'; License = 'Microsoft Windows SDK license'; Url = 'https://aka.ms/WinSDKLicenseURL' }
    'Avalonia'                                 = @{ Name = 'Avalonia'; License = 'MIT'; Url = 'https://github.com/AvaloniaUI/Avalonia' }
    'Avalonia.Desktop'                         = @{ Name = 'Avalonia.Desktop'; License = 'MIT'; Url = 'https://github.com/AvaloniaUI/Avalonia' }
    'Avalonia.Win32'                           = @{ Name = 'Avalonia.Win32'; License = 'MIT'; Url = 'https://github.com/AvaloniaUI/Avalonia' }
    'Avalonia.Skia'                            = @{ Name = 'Avalonia.Skia'; License = 'MIT'; Url = 'https://github.com/AvaloniaUI/Avalonia' }
    'Avalonia.HarfBuzz'                        = @{ Name = 'Avalonia.HarfBuzz'; License = 'MIT'; Url = 'https://github.com/AvaloniaUI/Avalonia' }
    'Avalonia.Themes.Fluent'                   = @{ Name = 'Avalonia.Themes.Fluent'; License = 'MIT'; Url = 'https://github.com/AvaloniaUI/Avalonia' }
    'Avalonia.Angle.Windows.Natives'           = @{ Name = 'ANGLE (Avalonia build)'; License = 'BSD-3-Clause'; Url = 'https://chromium.googlesource.com/angle/angle' }
    'MicroCom.Runtime'                         = @{ Name = 'MicroCom.Runtime'; License = 'MIT'; Url = 'https://github.com/kekekeks/MicroCom' }
    'SkiaSharp'                                = @{ Name = 'SkiaSharp'; License = 'MIT'; Url = 'https://github.com/mono/SkiaSharp' }
    'SkiaSharp.NativeAssets.Win32'             = @{ Name = 'SkiaSharp native (Skia)'; License = 'MIT (SkiaSharp), BSD-3-Clause (Skia)'; Url = 'https://github.com/mono/SkiaSharp' }
    'HarfBuzzSharp'                            = @{ Name = 'HarfBuzzSharp'; License = 'MIT'; Url = 'https://github.com/mono/SkiaSharp' }
    'HarfBuzzSharp.NativeAssets.Win32'         = @{ Name = 'HarfBuzzSharp native (HarfBuzz)'; License = 'MIT (HarfBuzzSharp), Old MIT (HarfBuzz)'; Url = 'https://github.com/harfbuzz/harfbuzz' }
    'QRCoder'                                  = @{ Name = 'QRCoder'; License = 'MIT'; Url = 'https://github.com/codebude/QRCoder' }
}

# Names that must never be in the bundle: the AirPlay receiver stays outside the distribution (GPL, FairPlay).
$forbidden = '(?i)uxplay|gstreamer|\bgst|playfair|fairplay|airplay|libplist|plist\.dll|msys|libcrypto|libssl|openssl|dnssd|avahi'

function Step([string]$text) { Write-Host "== $text" -ForegroundColor Cyan }

# 1-3. Publish, check the contents, notices ------------------------------------------------------------
# THIRD-PARTY-NOTICES.txt (repository root) is embedded in the exe for the About tab, so it must match the
# bundle: when the packages changed, it is rewritten and the exe published again (commit the new file).
$noticesPath = Join-Path $root 'THIRD-PARTY-NOTICES.txt'
for ($attempt = 1; ; $attempt++) {
    # 1. Publish
    Step 'dotnet publish (win-x64, self-contained, single file, trimmed)'
    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    if (Test-Path $manifestPath) { Remove-Item -Force $manifestPath }
    & dotnet publish $project -p:PublishProfile=win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)" }

    # 2. Contents
    Step 'checking the contents'
    $published = @(Get-ChildItem $publishDir -Recurse -File | ForEach-Object { $_.FullName.Substring($publishDir.Length + 1) })
    if ($published.Count -ne 1 -or $published[0] -ne 'IceScreenTouch.exe') {
        throw "the publish folder must hold IceScreenTouch.exe only, it has: $($published -join ', ')"
    }

    if (-not (Test-Path $manifestPath)) { throw "the bundle manifest is missing: $manifestPath" }
    $entries = @([System.IO.File]::ReadAllLines($manifestPath) | Where-Object { $_ } | ForEach-Object {
        $f = $_ -split "`t"
        [pscustomobject]@{ Path = $f[0]; Package = $f[1]; Version = $(if ($f.Count -gt 2) { $f[2] } else { '' }) }
    })
    $problems = @()
    foreach ($e in $entries) {
        if ($e.Path -match $forbidden -or $e.Package -match $forbidden) {
            $problems += "receiver code: $($e.Path) ($($e.Package))"
        }
        elseif (-not $e.Package) {
            # Without a package: the projects of this repository only, and their translations (ja\IceScreenTouch.App.resources.dll).
            if ($e.Path -notmatch '^(?i)([a-z]{2}(-[a-z]+)?\\)?IceScreenTouch(\.[A-Za-z.]+)?\.(dll|exe|json)$') { $problems += "unknown file: $($e.Path)" }
        }
        elseif (-not $knownPackages.Contains($e.Package)) {
            $problems += "unknown package: $($e.Package) $($e.Version) ($($e.Path)); check its license and add it to `$knownPackages"
        }
    }
    if ($problems) { throw "bundle check failed:`n  $($problems -join "`n  ")" }

    # Sorted by id: Group-Object lists the groups in a different order on Windows PowerShell 5.1 and PowerShell 7, and the
    # notices file must not change with the shell that ran the script (review, 2026-11-07).
    $packages = $entries | Where-Object Package | Group-Object Package | ForEach-Object {
        [pscustomobject]@{ Id = $_.Name; Version = $_.Group[0].Version; Files = $_.Count }
    } | Sort-Object Id
    $own = @($entries | Where-Object { -not $_.Package }).Count
    Write-Host "  $($entries.Count) files in the bundle: $own of IceScreen Touch, the rest from $(@($packages).Count) packages; no receiver code"


    Step 'third-party notices'
    $nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    $notices = [System.Text.StringBuilder]::new()
    [void]$notices.AppendLine('IceScreen Touch - third-party notices')
    [void]$notices.AppendLine('')
    [void]$notices.AppendLine('IceScreenTouch.exe bundles the following components. IceScreen Touch does not include an AirPlay receiver;')
    [void]$notices.AppendLine('the receiver (AirServer, UxPlay, ...) is a separate program that you install yourself.')
    [void]$notices.AppendLine('')
    foreach ($p in $packages) {
        $info = $knownPackages[$p.Id]
        [void]$notices.AppendLine(('- {0} ({1} {2}): {3} - {4}' -f $info.Name, $p.Id, $p.Version, $info.License, $info.Url))
    }
    foreach ($p in $packages) {
        $dir = Join-Path $nuget ("{0}\{1}" -f $p.Id.ToLowerInvariant(), $p.Version)
        $texts = @(Get-ChildItem $dir -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(LICENSE|THIRD-PARTY-NOTICES)' } | Sort-Object Name)
        foreach ($t in $texts) {
            [void]$notices.AppendLine('')
            [void]$notices.AppendLine(('=' * 100))
            [void]$notices.AppendLine(('{0} {1}: {2}' -f $p.Id, $p.Version, $t.Name))
            [void]$notices.AppendLine(('=' * 100))
            # ReadAllText reads BOM-less UTF-8 (the "(c)" of a license text) as UTF-8; Get-Content -Raw on Windows PowerShell 5.1 would read it as ANSI.
            [void]$notices.AppendLine([System.IO.File]::ReadAllText($t.FullName).TrimEnd())
        }
        if (-not $texts -and $knownPackages[$p.Id].License -eq 'MIT') {
            # Packages that carry only a license expression: the MIT text with the copyright of the package.
            $nuspec = Get-ChildItem $dir -Filter *.nuspec | Select-Object -First 1
            $metadata = ([xml][System.IO.File]::ReadAllText($nuspec.FullName)).package.metadata
            # Some packages (QRCoder) state no copyright: name the authors instead.
            $copyright = if ($metadata.SelectSingleNode('*[local-name()="copyright"]')) { $metadata.copyright } else { "Copyright (c) $($metadata.authors)" }
            [void]$notices.AppendLine('')
            [void]$notices.AppendLine(('=' * 100))
            [void]$notices.AppendLine(('{0} {1}: MIT License' -f $p.Id, $p.Version))
            [void]$notices.AppendLine(('=' * 100))
            [void]$notices.AppendLine($copyright)
            [void]$notices.AppendLine(@'

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of
the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
IN THE SOFTWARE.
'@)
        }
    }

    # One line ending everywhere: git may check the file out with CRLF.
    $text = $notices.ToString().Replace("`r`n", "`n")
    $current = if (Test-Path $noticesPath) { [System.IO.File]::ReadAllText($noticesPath).Replace("`r`n", "`n") } else { '' }
    if ($current -eq $text) {
        Write-Host '  THIRD-PARTY-NOTICES.txt matches the bundle (the exe embeds it)'
        break
    }
    [System.IO.File]::WriteAllText($noticesPath, $text, [System.Text.UTF8Encoding]::new($false))
    if ($attempt -ge 2) { throw 'THIRD-PARTY-NOTICES.txt changed again after publishing with it' }
    Write-Host '  THIRD-PARTY-NOTICES.txt updated (commit it); publishing again so that the exe embeds it' -ForegroundColor Yellow
}

# 4. Self-check of the published executable -------------------------------------------------------------
Step 'self-check of the published IceScreenTouch.exe'
$output = & $exe --self-check | Out-String
$code = $LASTEXITCODE
Write-Host ($output.TrimEnd() -replace '(?m)^', '  ')
if ($code -ne 0) { throw "the self-check failed (exit code $code)" }

# 5. Zip -------------------------------------------------------------------------------------------------
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$sizeMb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
Write-Host "  IceScreenTouch.exe $version, $sizeMb MB, SHA256 $hash"
if (-not $NoZip) {
    Step 'zip'
    $staging = Join-Path $artifacts 'zip'
    if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
    New-Item -ItemType Directory $staging | Out-Null
    Copy-Item $exe $staging
    # The user guides become README.md (English) and README.ja.md (Japanese); their links to each other follow.
    foreach ($guide in @(@{ From = 'USER-GUIDE.md'; To = 'README.md' }, @{ From = 'USER-GUIDE.ja.md'; To = 'README.ja.md' })) {
        $text = [System.IO.File]::ReadAllText((Join-Path $root "docs\$($guide.From)"))
        $text = $text.Replace('(USER-GUIDE.ja.md)', '(README.ja.md)').Replace('(USER-GUIDE.md)', '(README.md)')
        [System.IO.File]::WriteAllText((Join-Path $staging $guide.To), $text, [System.Text.UTF8Encoding]::new($false))
    }
    Copy-Item (Join-Path $root 'LICENSE') (Join-Path $staging 'LICENSE.txt')
    Copy-Item (Join-Path $root 'CHANGELOG.md') $staging
    Copy-Item $noticesPath (Join-Path $staging 'THIRD-PARTY-NOTICES.txt')
    $zip = Join-Path $artifacts "IceScreenTouch-$version-win-x64.zip"
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
    Remove-Item -Recurse -Force $staging
    Write-Host "  $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
}

# 6. MSIX (Microsoft Store) -------------------------------------------------------------------------------
if (-not $Msix) { return }

. (Join-Path $PSScriptRoot 'windows_kit.ps1')
Step 'MSIX package'
$makeappx = Find-WindowsKitTool 'makeappx.exe'
$makepri = Find-WindowsKitTool 'makepri.exe'
$msixSource = Join-Path $root 'packaging\msix'
$msixDir = Join-Path $artifacts 'msix'
$layout = Join-Path $msixDir 'layout'

# The MSIX version has four numbers and the Store wants the last one to be 0: X.Y.Z of the product becomes X.Y.Z.0.
if ($version -notmatch '^(\d+)\.(\d+)\.(\d+)') { throw "cannot make an MSIX version from '$version'" }
$msixVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).0"

# The firewall rule of the manifest must be for the port the app serves the measurement page on.
$serviceSource = [System.IO.File]::ReadAllText((Join-Path $root 'src\IceScreenTouch.Windows\WindowsProbeService.cs'))
if ($serviceSource -notmatch 'DefaultPort\s*=\s*(\d+)\s*;') { throw 'WindowsProbeService.DefaultPort not found' }
$probePort = $Matches[1]

if ($IdentityName -eq 'IceScreenTouch.Test' -or $Publisher -eq 'CN=IceScreenTouch Test') {
    Write-Host '  test identity (default): for local tests only, not for the Store. Pass -IdentityName, -Publisher and -PublisherDisplayName from Partner Center.' -ForegroundColor Yellow
}

if (Test-Path $msixDir) { Remove-Item -Recurse -Force $msixDir }
New-Item -ItemType Directory $layout | Out-Null
Copy-Item $exe $layout
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $layout 'LICENSE.txt')
Copy-Item $noticesPath (Join-Path $layout 'THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $msixSource 'Assets') $layout -Recurse

$manifest = [System.IO.File]::ReadAllText((Join-Path $msixSource 'Package.appxmanifest'))
$values = [ordered]@{
    IDENTITY_NAME          = $IdentityName
    PUBLISHER              = $Publisher
    PUBLISHER_DISPLAY_NAME = $PublisherDisplayName
    VERSION                = $msixVersion
    PROBE_PORT             = $probePort
}
foreach ($key in $values.Keys) {
    $manifest = $manifest.Replace("{{$key}}", [System.Security.SecurityElement]::Escape($values[$key]))
}
if ($manifest -match '\{\{[A-Z_]+\}\}') { throw "an unfilled placeholder is left in the manifest: $($Matches[0])" }
[System.IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, [System.Text.UTF8Encoding]::new($false))

# resources.pri: the logos are named by scale and target size, which the package resolves through this index.
$priConfig = Join-Path $msixDir 'priconfig.xml'
function Invoke-Kit([string]$What, [scriptblock]$Command) {
    # The kit tools are quiet when they work; their output is shown when they fail.
    $text = & $Command 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit code $LASTEXITCODE):`n$text" }
}
Invoke-Kit 'makepri createconfig' { & $makepri createconfig /cf $priConfig /dq en-US /o }
# One resources.pri: without the packaging block makepri would also write resources.scale-200.pri (a split for bundles).
$config = [System.IO.File]::ReadAllText($priConfig) -replace '(?s)\s*<packaging>.*?</packaging>', ''
[System.IO.File]::WriteAllText($priConfig, $config, [System.Text.UTF8Encoding]::new($false))
Invoke-Kit 'makepri new' { & $makepri new /pr $layout /cf $priConfig /mn (Join-Path $layout 'AppxManifest.xml') /of (Join-Path $layout 'resources.pri') /o }

$msixPath = Join-Path $artifacts "IceScreenTouch-$version-x64.msix"
if (Test-Path $msixPath) { Remove-Item -Force $msixPath }
# Test-signed and unsigned copies left by tools\msix_test_install.ps1 (this or an older version) must not outlive the package.
Get-ChildItem (Join-Path $artifacts '*.msix') | Where-Object { $_.Name -like '*.test-signed.msix' -or $_.Name -like '*.unsigned.msix' } |
    Remove-Item -Force
Invoke-Kit 'makeappx pack' { & $makeappx pack /d $layout /p $msixPath /o }
Write-Host "  $msixPath ($([math]::Round((Get-Item $msixPath).Length / 1MB, 1)) MB, unsigned; version $msixVersion, identity $IdentityName)"
