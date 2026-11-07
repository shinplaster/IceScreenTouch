# Dot-sourced by tools/publish.ps1 (-Msix) and tools/msix_test_install.ps1: finds a tool of the Windows SDK
# (makeappx.exe, makepri.exe, signtool.exe).

function Find-WindowsKitTool([Parameter(Mandatory)][string]$Name) {
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $found = $null
    if (Test-Path $kits) {
        # The newest SDK version that has the tool for x64 (folders are named 10.0.<build>.0).
        $found = Get-ChildItem $kits -Directory |
            Where-Object { $_.Name -match '^10\.0\.\d+\.\d+$' } |
            Sort-Object { [version]$_.Name } -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
            Where-Object { Test-Path $_ } |
            Select-Object -First 1
    }
    if (-not $found) {
        $onPath = Get-Command $Name -ErrorAction SilentlyContinue
        if ($onPath) { $found = $onPath.Source }
    }
    if (-not $found) { throw "$Name not found: install the Windows SDK (Windows Kits\10\bin\<version>\x64)" }
    return $found
}
