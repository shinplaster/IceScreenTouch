# Copyright (c) 2026 Shin-plaster
# SPDX-License-Identifier: MIT

<#
.SYNOPSIS
    TEST ONLY: installs the unsigned test MSIX of IceScreen Touch on this PC (or removes it again). Not part of any release.

.DESCRIPTION
    *** FOR LOCAL TESTING OF THE MSIX PACKAGE ONLY. The Microsoft Store signs the real package; builds of your own
    *** are plain unsigned exes. Do not give the certificate or the signed package to anyone.

    Install (default):
      1. Copies the package to artifacts\IceScreenTouch-<version>-x64.test-signed.msix. The Store-named package is NEVER
         signed or changed, so a test-signed package cannot be submitted by mistake.
      2. Reads the Publisher from the AppxManifest.xml inside the package and creates a self-signed code-signing
         certificate whose subject equals it (CurrentUser\My, friendly name "IceScreen Touch TEST ...", about 30 days,
         private key NonExportable).
      3. Signs the copy with signtool, then deletes the certificate AND its private key from CurrentUser\My, so no
         signing key is left behind on this PC.
      4. Imports the public certificate into LocalMachine\TrustedPeople. THIS NEEDS ELEVATION: the script opens one UAC
         prompt for that step only. The certificate travels inside the elevated command as Base64 (no file in a
         user-writable folder), and the elevated side checks its thumbprint against the expected one before importing.
         (The CurrentUser store alone is not enough: Add-AppxPackage then fails with 0x800B0109, the root is not trusted.)
      5. Add-AppxPackage of the test-signed copy. Installing needs no elevation. The package brings the firewall rule
         of the manifest and the app execution alias "IceScreenTouch.exe".
      The installed package name is recorded in artifacts\msix-test-installed.txt (one name per line) so that -Remove also
      finds packages of a Store-identity test build. If a package with the same name is already installed, the script
      prints a hint first (Add-AppxPackage may fail with 0x80073CFB for the same version: run -Remove first).
      If a step fails, what this run made in CurrentUser is removed again; the LocalMachine certificate (if it was already
      imported) and a half-installed package are removed by .\tools\msix_test_install.ps1 -Remove. The signing key in
      CurrentUser\My is also deleted on Ctrl+C or when the console is closed.

    -Remove:
      Removes the packages (this also removes their firewall rule and alias) and every test certificate this script made,
      from CurrentUser\My, CurrentUser\TrustedPeople and LocalMachine\TrustedPeople (the last one with one UAC prompt).
      Packages: -IdentityName plus every name recorded in artifacts\msix-test-installed.txt at install time.
      Certificates are matched by the friendly name above (whatever their subject, so certificates made for a
      Store-identity test build are removed too; the machine copy gets the name at import) or by the thumbprint of such a
      certificate still in CurrentUser\My, so other certificates are left alone. The test-signed copies of the package in
      artifacts\ and the record file are deleted too.

    Run it from Windows PowerShell 5.1 (powershell.exe); in PowerShell 7 the Appx cmdlets are loaded through the
    compatibility layer.

.PARAMETER Msix
    The unsigned package to install. Default: the newest artifacts\IceScreenTouch-*-x64.msix (built by tools\publish.ps1 -Msix; *.test-signed.msix and *.unsigned.msix are ignored). It is copied and the copy is signed.

.PARAMETER Remove
    Remove the test package and the test certificates instead of installing.

.PARAMETER IdentityName
    A package identity name to remove (-Remove), in addition to the names recorded at install time. Default: the test identity of tools\publish.ps1.

.EXAMPLE
    .\tools\publish.ps1 -Msix; .\tools\msix_test_install.ps1

.EXAMPLE
    .\tools\msix_test_install.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$Msix,
    [switch]$Remove,
    [string]$IdentityName = 'IceScreenTouch.Test'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'windows_kit.ps1')
$friendlyName = 'IceScreen Touch TEST (tools/msix_test_install.ps1)'
$artifacts = Join-Path $root 'artifacts'
$recordFile = Join-Path $artifacts 'msix-test-installed.txt'

Write-Host '*** TEST ONLY: local test install of the IceScreen Touch MSIX. Not for release. ***' -ForegroundColor Yellow

if ($PSVersionTable.PSEdition -eq 'Core') { Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue }

function Test-Elevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

# Runs a PowerShell command line elevated (one UAC prompt) and waits for it; runs it directly when already elevated.
function Invoke-Elevated([string]$Command) {
    if (Test-Elevated) {
        & ([scriptblock]::Create($Command))
        return
    }
    Write-Host '  asking for administrator rights (UAC) for the LocalMachine certificate store...'
    # The full path, not a bare name, so that nothing planted earlier on the PATH runs elevated.
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes("`$ErrorActionPreference = 'Stop'; $Command"))
    $p = Start-Process $powershell -Verb RunAs -Wait -PassThru -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded)
    if ($p.ExitCode -ne 0) { throw "the elevated command failed (exit code $($p.ExitCode))" }
}

if ($Remove) {
    Write-Host '== removing the test package and the test certificates' -ForegroundColor Cyan
    $names = @($IdentityName)
    if (Test-Path $recordFile) { $names += @(Get-Content $recordFile | ForEach-Object { $_.Trim() } | Where-Object { $_ }) }
    $anyPackage = $false
    foreach ($packageName in @($names | Select-Object -Unique)) {
        foreach ($package in @(Get-AppxPackage -Name $packageName)) {
            $anyPackage = $true
            Write-Host "  Remove-AppxPackage $($package.PackageFullName)"
            Remove-AppxPackage -Package $package.PackageFullName
        }
    }
    if (-not $anyPackage) { Write-Host "  no package named $((@($names | Select-Object -Unique)) -join ', ') is installed" }

    # Our certificates carry the friendly name (set at import for the machine copy), whatever their subject (a Store-identity
    # test build has another publisher). Older runs of this script did not set it on the machine copy, so it is also matched
    # by the thumbprints of the test certificates that are still in CurrentUser\My.
    $ourThumbprints = @(Get-ChildItem 'Cert:\CurrentUser\My' |
        Where-Object { $_.FriendlyName -eq $friendlyName } | ForEach-Object Thumbprint)
    $isTest = { $_.FriendlyName -eq $friendlyName -or $ourThumbprints -contains $_.Thumbprint }
    foreach ($store in 'Cert:\CurrentUser\My', 'Cert:\CurrentUser\TrustedPeople') {
        foreach ($cert in @(Get-ChildItem $store | Where-Object $isTest)) {
            Write-Host "  removing $($cert.Thumbprint) from $store"
            if ($store -eq 'Cert:\CurrentUser\My') { Remove-Item $cert.PSPath -DeleteKey } else { Remove-Item $cert.PSPath }
        }
    }
    $machine = @(Get-ChildItem 'Cert:\LocalMachine\TrustedPeople' | Where-Object $isTest)
    if ($machine) {
        $thumbprints = ($machine | ForEach-Object { "'$($_.Thumbprint)'" }) -join ','
        Write-Host "  removing $($machine.Count) certificate(s) from LocalMachine\TrustedPeople"
        Invoke-Elevated "foreach (`$t in @($thumbprints)) { Remove-Item (Join-Path 'Cert:\LocalMachine\TrustedPeople' `$t) }"
    }
    foreach ($stale in @(Get-ChildItem $artifacts -Filter '*.test-signed.msix' -ErrorAction SilentlyContinue)) {
        Write-Host "  deleting $($stale.Name)"
        Remove-Item $stale.FullName -Force
    }
    if (Test-Path $recordFile) { Remove-Item $recordFile -Force }
    Write-Host 'done'
    return
}

# Install ---------------------------------------------------------------------------------------------------
if (-not $Msix) {
    $Msix = Get-ChildItem $artifacts -Filter 'IceScreenTouch-*-x64.msix' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.test-signed.msix' -and $_.Name -notlike '*.unsigned.msix' } |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $Msix) { throw 'no artifacts\IceScreenTouch-*-x64.msix: run tools\publish.ps1 -Msix first' }
}
$Msix = (Resolve-Path $Msix).Path
if ($Msix -like '*.test-signed.msix' -or $Msix -like '*.unsigned.msix') { throw 'pass the unsigned package (IceScreenTouch-<version>-x64.msix), not a test-signed copy' }
if ($Msix -notlike '*.msix') { throw "not an .msix file: $Msix" }
$signtool = Find-WindowsKitTool 'signtool.exe'

# The certificate subject must equal the Publisher of the package.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Msix)
try {
    $entry = $zip.GetEntry('AppxManifest.xml')
    $reader = [IO.StreamReader]::new($entry.Open())
    try { $manifest = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
}
finally { $zip.Dispose() }
$subject = $manifest.Package.Identity.Publisher
$name = $manifest.Package.Identity.Name
Write-Host "== $Msix`n   identity $name, publisher $subject" -ForegroundColor Cyan

# The Store-named package stays unsigned: the copy is what gets signed and installed.
$signed = $Msix -replace '\.msix$', '.test-signed.msix'
if ($signed -eq $Msix) { throw 'internal error: the signed copy would overwrite the original package' }
$copied = $false
$cert = $null
try {
    Write-Host '== creating the self-signed test certificate (CurrentUser\My, 30 days, key not exportable)' -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -FriendlyName $friendlyName `
        -KeyUsage DigitalSignature -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddDays(30) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Write-Host "  $($cert.Thumbprint)"

    Write-Host "== signing a copy: $signed" -ForegroundColor Cyan
    Copy-Item $Msix $signed -Force
    $copied = $true
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $signed
    if ($LASTEXITCODE -ne 0) { throw "signtool failed (exit code $LASTEXITCODE)" }

    # Only the public part is needed from here on; the signing key is deleted with the certificate.
    $publicBase64 = [Convert]::ToBase64String($cert.RawData)
    $thumbprint = $cert.Thumbprint
    Remove-Item $cert.PSPath -DeleteKey
    $cert = $null

    Write-Host '== trusting the certificate (LocalMachine\TrustedPeople, needs elevation)' -ForegroundColor Cyan
    # Base64 and a hex thumbprint are safe inside the command; the friendly name has its quotes doubled. No file is
    # written, so nothing can be swapped between the export and the import. The elevated side checks the thumbprint.
    $quotedName = $friendlyName.Replace("'", "''")
    $importCommand = @"
`$bytes = [Convert]::FromBase64String('$publicBase64')
`$c = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (,`$bytes)
if (`$c.Thumbprint -ne '$thumbprint') { throw 'the certificate does not match the expected thumbprint' }
`$c.FriendlyName = '$quotedName'
`$store = New-Object System.Security.Cryptography.X509Certificates.X509Store 'TrustedPeople', 'LocalMachine'
`$store.Open('ReadWrite')
try {
    `$store.Add(`$c)
    foreach (`$found in `$store.Certificates.Find('FindByThumbprint', '$thumbprint', `$false)) {
        if (`$found.FriendlyName -ne '$quotedName') { `$found.FriendlyName = '$quotedName' }
    }
}
finally { `$store.Close() }
"@
    Invoke-Elevated $importCommand

    Write-Host '== installing' -ForegroundColor Cyan
    # Recorded before the install so that -Remove finds the package even if Add-AppxPackage fails half-way.
    $recorded = if (Test-Path $recordFile) { @(Get-Content $recordFile) } else { @() }
    if ($recorded -notcontains $name) { Set-Content -Path $recordFile -Value ($recorded + $name) -Encoding ASCII }
    if (Get-AppxPackage -Name $name) {
        Write-Host "  note: a package named $name is already installed; Add-AppxPackage may fail with 0x80073CFB for the same version. Run .\tools\msix_test_install.ps1 -Remove first." -ForegroundColor Yellow
    }
    Add-AppxPackage -Path $signed
    Get-AppxPackage -Name $name | Format-List Name, PackageFullName, InstallLocation
    Write-Host 'installed. Start it from the Start menu, or run "IceScreenTouch.exe --self-check" in a new terminal.'
    Write-Host 'Remove everything again with: .\tools\msix_test_install.ps1 -Remove'
}
catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    # What this run made in CurrentUser is removed here. A certificate that was already imported into LocalMachine
    # and a half-installed package are removed by -Remove (the machine copy is found by its friendly name).
    if ($cert) { Remove-Item $cert.PSPath -DeleteKey -ErrorAction SilentlyContinue }
    if ($copied) { Remove-Item $signed -Force -ErrorAction SilentlyContinue }
    Write-Host 'Clean up with: .\tools\msix_test_install.ps1 -Remove' -ForegroundColor Yellow
    throw
}
finally {
    # Also runs on Ctrl+C and when the console is closed: the signing key never stays in CurrentUser\My.
    if ($cert -and (Test-Path $cert.PSPath)) { Remove-Item $cert.PSPath -DeleteKey -ErrorAction SilentlyContinue }
}
