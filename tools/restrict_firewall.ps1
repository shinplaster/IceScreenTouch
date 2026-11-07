# Narrows existing inbound ALLOW rules for the given programs so that they only accept connections from the
# local subnet. Rules are changed in place (names, ports and profiles stay), so an uninstaller that removes its
# own rules by name still finds them. Rules that already restrict the remote address are left alone.
#
#   .\tools\restrict_firewall.ps1 -Exe "C:\Program Files\App Dynamic\AirServer\AirServer.exe" -WhatIf
#   (elevated) .\tools\restrict_firewall.ps1 -Exe <path>[,<path>...] [-LogPath <file>]
#
# -WhatIf only lists what would change and does not need elevation.
param(
    [Parameter(Mandatory = $true)][string[]]$Exe,
    [switch]$WhatIf,
    [string]$LogPath = ""
)

# Run elevated: modules only from Windows' own folder (see allow_firewall.ps1), before any cmdlet loads one.
$env:PSModulePath = [System.IO.Path]::Combine([System.Environment]::SystemDirectory, "WindowsPowerShell\v1.0\Modules")
$ErrorActionPreference = "Stop"
if ($LogPath) { Start-Transcript -LiteralPath $LogPath -Force | Out-Null }

try {
    # Reading the application filter of every rule one by one takes minutes; read them all once instead.
    # The persistent store (the default) is the one that can be changed; the active store is read-only.
    $filters = @(Get-NetFirewallApplicationFilter -All -ErrorAction SilentlyContinue)
    function Show-Rules([string]$path) {
        $filters | Where-Object {
            $_.Program -and ([Environment]::ExpandEnvironmentVariables($_.Program) -ieq $path)
        } | Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.Direction -eq 'Inbound' }
    }

    $changed = 0
    $failed = 0
    foreach ($e in $Exe) {
        $path = [Environment]::ExpandEnvironmentVariables($e)
        if (Test-Path -LiteralPath $path) { $path = (Resolve-Path -LiteralPath $path).Path }
        Write-Host "target: $path"
        $rules = @(Show-Rules $path)
        if ($rules.Count -eq 0) { Write-Host "  no inbound rules" }
        foreach ($r in $rules) {
            $remote = ($r | Get-NetFirewallAddressFilter).RemoteAddress -join ","
            $proto = ($r | Get-NetFirewallPortFilter).Protocol
            $line = "  {0} [{1}, {2}, {3}, {4}] remote={5}" -f $r.DisplayName, $r.Action, $proto, $r.Profile, $(if ($r.Enabled -eq 'True') { 'enabled' } else { 'disabled' }), $remote
            if ($r.Action -ne 'Allow' -or $remote -ne 'Any') {
                Write-Host "$line (kept)"
                continue
            }
            # Rules made by the "allow access" dialog use EdgeTraversalPolicy DeferToUser, which Windows only
            # accepts on rules without extra conditions. Block edge traversal (Teredo) so a remote address
            # condition can be added; a receiver on the local subnet never needs it.
            $edge = $r.EdgeTraversalPolicy
            $newEdge = if ($edge -eq 'DeferToUser' -or $edge -eq 'DeferToApp') { 'Block' } else { $edge }
            $note = if ($newEdge -ne $edge) { ", edge traversal $edge -> $newEdge" } else { "" }
            if ($WhatIf) {
                Write-Host "$line -> would become LocalSubnet$note"
                $changed++
                continue
            }
            try {
                $r | Set-NetFirewallRule -RemoteAddress LocalSubnet -EdgeTraversalPolicy $newEdge -ErrorAction Stop
            }
            catch {
                Write-Host "$line -> FAILED: $($_.Exception.Message)"
                $failed++
                continue
            }
            $after = (Get-NetFirewallRule -Name $r.Name | Get-NetFirewallAddressFilter).RemoteAddress -join ","
            if ($after -eq 'LocalSubnet') {
                Write-Host "$line -> $after$note"
                $changed++
            }
            else {
                Write-Host "$line -> still $after (FAILED)"
                $failed++
            }
        }
    }
    Write-Host ("{0} rule(s) {1}, {2} failed" -f $changed, $(if ($WhatIf) { 'would change' } else { 'changed' }), $failed)
    if ($failed -gt 0) { exit 1 }
    Write-Host "DONE"
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($LogPath) { Stop-Transcript | Out-Null }
}
