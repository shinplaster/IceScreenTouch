# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

# Launch the locally built UxPlay (AirPlay mirroring receiver) with the MSYS2 UCRT64 runtime on PATH.
# Usage: .\run_uxplay.ps1 [-Name iScrMirror] [-Sink d3d11videosink] [-Decoder ""] [-Debug] [-ExtraArgs "..."]
param(
    [string]$Name = "iScrMirror",
    [string]$Sink = "d3d11videosink",
    [string]$Decoder = "",
    [switch]$DebugLog,
    [string[]]$ExtraArgs = @()
)

$msys = "$env:USERPROFILE\scoop\apps\msys2\current\ucrt64\bin"
$exe = Join-Path $PSScriptRoot "UxPlay\build\uxplay.exe"
if (-not (Test-Path $exe)) { throw "uxplay.exe not found: $exe (build it first, see README.md)" }

$env:PATH = "$msys;$env:PATH"

$uxArgs = @("-n", $Name, "-nh", "-vs", $Sink, "-vsync", "no")
if ($Decoder) { $uxArgs += @("-vd", $Decoder) }
if ($DebugLog) { $uxArgs += "-d" }
$uxArgs += $ExtraArgs

Write-Host "uxplay $($uxArgs -join ' ')"
& $exe @uxArgs
