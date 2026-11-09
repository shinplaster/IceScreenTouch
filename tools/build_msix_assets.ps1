# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
    Builds the logo images of the Microsoft Store (MSIX) package from the SVG sources in docs/brand.

.DESCRIPTION
    Draws docs/brand/icon-small.svg (up to 32 px) and docs/brand/icon-large.svg (larger) with headless Microsoft Edge
    on a transparent background, the same way tools/build_icon.ps1 does, and writes the PNG files that
    packaging/msix/Package.appxmanifest refers to into packaging/msix/Assets:

      Square44x44Logo   scale-100/200 and targetsize-16/24/32/48/256, each target size also as altform-unplated
      Square150x150Logo scale-100/200 (the icon at 75 % of the tile, centered)
      Wide310x150Logo   scale-100/200 (the icon at 75 % of the height, centered)
      StoreLogo         scale-100/200

    The PNG files are committed; run this script only when the icon changes. tools/publish.ps1 -Msix builds
    resources.pri from them (the scale and targetsize qualifiers need it).
#>
param(
    [string]$Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$brand = Join-Path $root 'docs\brand'
$outDir = Join-Path $root 'packaging\msix\Assets'
if (-not (Test-Path $Edge)) { throw "Microsoft Edge not found: $Edge" }

# One entry per picture: file names (the same picture is written under each), canvas, and the icon size inside it.
function Item([string[]]$Names, [int]$W, [int]$H, [int]$Icon) {
    [pscustomobject]@{ Names = $Names; W = $W; H = $H; Icon = $Icon; Svg = $(if ($Icon -le 32) { 'icon-small.svg' } else { 'icon-large.svg' }) }
}
$items = @(
    (Item @('Square44x44Logo.scale-100.png') 44 44 44),
    (Item @('Square44x44Logo.scale-200.png') 88 88 88)
)
foreach ($t in 16, 24, 32, 48, 256) {
    $items += Item @("Square44x44Logo.targetsize-$t.png", "Square44x44Logo.targetsize-${t}_altform-unplated.png") $t $t $t
}
$items += @(
    (Item @('Square150x150Logo.scale-100.png') 150 150 112),
    (Item @('Square150x150Logo.scale-200.png') 300 300 225),
    (Item @('Wide310x150Logo.scale-100.png') 310 150 112),
    (Item @('Wide310x150Logo.scale-200.png') 620 300 225),
    (Item @('StoreLogo.scale-100.png') 50 50 50),
    (Item @('StoreLogo.scale-200.png') 100 100 100)
)

# Lay the pictures out one below the other, 4 px apart, on one sheet (a single Edge run).
$y = 0
$tags = foreach ($i in $items) {
    $i | Add-Member -NotePropertyName Y -NotePropertyValue $y
    $src = 'file:///' + ((Join-Path $brand $i.Svg) -replace '\\', '/')
    $left = [int](($i.W - $i.Icon) / 2); $top = [int](($i.H - $i.Icon) / 2)
    "<img src=""$src"" style=""left:${left}px;top:$($y + $top)px"" width=""$($i.Icon)"" height=""$($i.Icon)"">"
    $y += $i.H + 4
}
$width = ($items | Measure-Object W -Maximum).Maximum
$height = $y

$work = Join-Path ([IO.Path]::GetTempPath()) "icescreentouch-msix-assets-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $html = Join-Path $work 'sheet.html'
    $sheet = Join-Path $work 'sheet.png'
    [System.IO.File]::WriteAllText($html, @"
<!doctype html><html><head><meta charset="utf-8">
<style>html, body { margin: 0; background: transparent; } img { position: absolute; }</style>
</head><body>$($tags -join "`n")</body></html>
"@, [System.Text.UTF8Encoding]::new($false))
    $url = 'file:///' + (($html -replace '\\', '/') -replace ' ', '%20')
    Start-Process $Edge -Wait -ArgumentList ('--headless=new --disable-gpu --hide-scrollbars --allow-file-access-from-files ' +
        "--default-background-color=00000000 --force-device-scale-factor=1 --window-size=$width,$height " +
        "`"--screenshot=$sheet`" `"$url`"")
    if (-not (Test-Path $sheet)) { throw 'Edge did not write the screenshot' }

    New-Item -ItemType Directory -Force $outDir | Out-Null
    $bitmap = [System.Drawing.Bitmap]::FromFile($sheet)
    try {
        if ($bitmap.Width -lt $width -or $bitmap.Height -lt $height) { throw "the sheet is $($bitmap.Width)x$($bitmap.Height), expected at least ${width}x$height" }
        foreach ($i in $items) {
            $crop = $bitmap.Clone([System.Drawing.Rectangle]::new(0, $i.Y, $i.W, $i.H), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                if ($crop.GetPixel(0, 0).A -ne 0) { throw "$($i.Names[0]) has no transparent corner" }
                foreach ($name in $i.Names) {
                    $crop.Save((Join-Path $outDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
                    Write-Host ("wrote {0} ({1}x{2})" -f $name, $i.W, $i.H)
                }
            }
            finally { $crop.Dispose() }
        }
    }
    finally { $bitmap.Dispose() }
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
