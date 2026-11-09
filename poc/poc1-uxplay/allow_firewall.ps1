# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

# Run elevated. Replaces the auto-generated "Query User" BLOCK rules for the locally built uxplay.exe
# with a single ALLOW rule that only accepts connections from the local subnet.
param(
    [string]$Exe = (Join-Path $PSScriptRoot "UxPlay\build\uxplay.exe"),
    [string]$LogPath = ""
)
$ErrorActionPreference = "Stop"
if ($LogPath) { Start-Transcript -Path $LogPath -Force | Out-Null }

try {
    $Exe = (Resolve-Path $Exe).Path
    Write-Host "target: $Exe"

    # Windows keeps program paths lower-cased in rules, so compare case-insensitively.
    $old = Get-NetFirewallRule -Direction Inbound -ErrorAction SilentlyContinue | Where-Object {
        $prog = ($_ | Get-NetFirewallApplicationFilter).Program
        $prog -and ($prog -ieq $Exe)
    }
    foreach ($r in $old) {
        Write-Host ("removing rule: {0} ({1}, {2})" -f $r.DisplayName, $r.Action, $r.Profile)
        $r | Remove-NetFirewallRule
    }

    New-NetFirewallRule -DisplayName "UxPlay (iScrMirror PoC)" -Direction Inbound -Action Allow `
        -Program $Exe -Profile Any -Protocol Any -RemoteAddress LocalSubnet | Out-Null
    Write-Host "added: Allow inbound for uxplay.exe from LocalSubnet (all profiles)"
    Get-NetFirewallRule -DisplayName "UxPlay (iScrMirror PoC)" | Select-Object DisplayName, Action, Direction, Profile, Enabled | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "DONE"
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($LogPath) { Stop-Transcript | Out-Null }
}
