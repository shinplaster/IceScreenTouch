# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
    Builds the app icon (IceScreenTouch.ico) from the SVG sources in docs/brand.

.DESCRIPTION
    Draws docs/brand/icon-small.svg at 16, 20, 24 and 32 px and docs/brand/icon-large.svg at 48, 64, 128 and
    256 px with headless Microsoft Edge (transparent background, each size drawn at its own size rather than
    scaled down), then packs the eight PNG images into one .ico and writes it to
    src/IceScreenTouch.Windows/IceScreenTouch.ico (the exe) and src/IceScreenTouch.App/Assets/icon.ico (the
    window and the notification area).
#>
param(
    [string]$Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$brand = Join-Path $root 'docs\brand'
$outputs = @(
    (Join-Path $root 'src\IceScreenTouch.Windows\IceScreenTouch.ico'),
    (Join-Path $root 'src\IceScreenTouch.App\Assets\icon.ico')
)
if (-not (Test-Path $Edge)) { throw "Microsoft Edge not found: $Edge" }

# Size, source picture, and x offset on the rendered sheet (side by side, 4 px apart, top aligned).
$sizes = @(
    @(16, 'icon-small.svg'), @(20, 'icon-small.svg'), @(24, 'icon-small.svg'), @(32, 'icon-small.svg'),
    @(48, 'icon-large.svg'), @(64, 'icon-large.svg'), @(128, 'icon-large.svg'), @(256, 'icon-large.svg')
)
$x = 0
$images = foreach ($s in $sizes) {
    [pscustomobject]@{ Size = $s[0]; Svg = $s[1]; X = $x }
    $x += $s[0] + 4
}

$work = Join-Path ([IO.Path]::GetTempPath()) "icescreentouch-icon-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $tags = foreach ($i in $images) {
        $src = 'file:///' + ((Join-Path $brand $i.Svg) -replace '\\', '/')
        "<img src=""$src"" style=""left:$($i.X)px"" width=""$($i.Size)"" height=""$($i.Size)"">"
    }
    $html = Join-Path $work 'sheet.html'
    $sheet = Join-Path $work 'sheet.png'
    # WriteAllText rather than Set-Content -Encoding utf8NoBOM, which Windows PowerShell 5.1 does not have.
    [System.IO.File]::WriteAllText($html, @"
<!doctype html><html><head><meta charset="utf-8">
<style>html, body { margin: 0; background: transparent; } img { position: absolute; top: 0; }</style>
</head><body>$($tags -join "`n")</body></html>
"@, [System.Text.UTF8Encoding]::new($false))
    $width = $x; $height = 260
    # One argument string with the paths quoted: Windows PowerShell 5.1 joins an array with spaces and no quotes, so
    # a path with a space (a user name such as "John Smith") would be split in two.
    $url = 'file:///' + (($html -replace '\\', '/') -replace ' ', '%20')
    Start-Process $Edge -Wait -ArgumentList ('--headless=new --disable-gpu --hide-scrollbars --allow-file-access-from-files ' +
        "--default-background-color=00000000 --force-device-scale-factor=1 --window-size=$width,$height " +
        "`"--screenshot=$sheet`" `"$url`"")
    if (-not (Test-Path $sheet)) { throw 'Edge did not write the screenshot' }

    # Crop each size and keep it as PNG data.
    $bitmap = [System.Drawing.Bitmap]::FromFile($sheet)
    try {
        $pngs = foreach ($i in $images) {
            $crop = $bitmap.Clone([System.Drawing.Rectangle]::new($i.X, 0, $i.Size, $i.Size), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                if ($crop.GetPixel(0, 0).A -ne 0) { throw "the $($i.Size) px image has no transparent corner" }
                $ms = New-Object System.IO.MemoryStream
                $crop.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
                [pscustomobject]@{ Size = $i.Size; Data = $ms.ToArray() }
            }
            finally { $crop.Dispose() }
        }
    }
    finally { $bitmap.Dispose() }

    # ICONDIR + one ICONDIRENTRY per image + the PNG data (PNG entries work on Windows Vista and later).
    $ico = New-Object System.IO.MemoryStream
    $w = New-Object System.IO.BinaryWriter $ico
    $w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$pngs.Count)
    $offset = 6 + 16 * $pngs.Count
    foreach ($p in $pngs) {
        $dim = if ($p.Size -ge 256) { 0 } else { $p.Size }   # 0 means 256
        $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
        $w.Write([uint16]1); $w.Write([uint16]32)
        $w.Write([uint32]$p.Data.Length); $w.Write([uint32]$offset)
        $offset += $p.Data.Length
    }
    foreach ($p in $pngs) { $w.Write($p.Data) }
    $w.Flush()
    foreach ($out in $outputs) {
        [IO.File]::WriteAllBytes($out, $ico.ToArray())
        Write-Host "wrote $out ($($ico.Length) bytes, $($pngs.Count) sizes: $(($pngs | ForEach-Object Size) -join ', ') px)"
    }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
