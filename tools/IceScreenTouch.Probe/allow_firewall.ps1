# Run elevated. Lets the probe page server (IceScreenTouch.Probe.exe by default, or IceScreenTouch.exe with -Exe;
# TCP 8000 by default) accept connections from the local subnet only, and removes any other inbound rules
# for the same program (for example the ones the "allow access" dialog creates, which allow every remote
# address). The app (IceScreenTouch.exe) runs this script elevated with its own -Exe and -Name. Rules are matched
# by program only: other copies of the program (a debug build, a published one) use the same name.
param(
    [string]$Exe = [System.IO.Path]::Combine($PSScriptRoot, "bin\Debug\net10.0-windows10.0.22621.0\IceScreenTouch.Probe.exe"),
    [int]$Port = 8000,
    [string]$Name = "IceScreen Touch Probe page",
    [string]$LogPath = ""
)

# Before any cmdlet loads its module: modules only from Windows' own folder. The user's Documents\WindowsPowerShell\
# Modules comes first in the default path and any program of the user may write there, so a planted NetSecurity
# (or Microsoft.PowerShell.*) module would run with the administrator rights this script gets. No cmdlet and no
# environment variable is used to build the path.
$env:PSModulePath = [System.IO.Path]::Combine([System.Environment]::SystemDirectory, "WindowsPowerShell\v1.0\Modules")
$ErrorActionPreference = "Stop"
if ($LogPath) { Start-Transcript -LiteralPath $LogPath -Force | Out-Null }

try {
    $Exe = (Resolve-Path -LiteralPath $Exe).Path
    Write-Host "target: $Exe (TCP $Port)"

    # Windows keeps program paths lower-cased in rules, so compare case-insensitively. The filters are read in
    # one call: asking rule by rule takes minutes on a PC with hundreds of rules.
    $old = @(Get-NetFirewallApplicationFilter -All -ErrorAction SilentlyContinue |
        Where-Object { $_.Program -and ($_.Program -ieq $Exe) } |
        Get-NetFirewallRule -ErrorAction SilentlyContinue |
        Where-Object { $_.Direction -eq 'Inbound' })
    foreach ($r in $old) {
        Write-Host ("removing rule: {0} ({1}, {2})" -f $r.DisplayName, $r.Action, $r.Profile)
        $r | Remove-NetFirewallRule
    }

    New-NetFirewallRule -DisplayName $Name -Direction Inbound -Action Allow -Program $Exe `
        -Profile Any -Protocol TCP -LocalPort $Port -RemoteAddress LocalSubnet | Out-Null
    Write-Host "added: Allow inbound TCP $Port for $(Split-Path $Exe -Leaf) from LocalSubnet (all profiles)"
    Get-NetFirewallApplicationFilter -Program $Exe | Get-NetFirewallRule | Select-Object DisplayName, Action, Direction, Profile, Enabled | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "DONE"
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)"
    exit 1
}
finally {
    if ($LogPath) { Stop-Transcript | Out-Null }
}
