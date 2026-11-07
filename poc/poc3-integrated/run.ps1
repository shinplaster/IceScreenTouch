# Start PoC 3: UxPlay mirror window + BLE HID touch bridge.
# Usage: .\run.ps1 [-v] [--dry-run] [--no-uxplay] ...   (all arguments are passed to mirror_touch.py)
# Quit any running poc/poc2-ble-hid instance first; it would hold the same GATT services.
$py = Join-Path $PSScriptRoot "..\poc2-ble-hid\.venv\Scripts\python.exe"
if (-not (Test-Path $py)) { throw "Python venv not found: $py (see poc/poc2-ble-hid/README.md)" }
& $py -X utf8 (Join-Path $PSScriptRoot "mirror_touch.py") @args
