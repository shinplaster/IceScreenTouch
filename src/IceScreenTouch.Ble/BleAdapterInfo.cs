// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using Windows.Devices.Bluetooth;

namespace IceScreenTouch.Ble;

/// <summary>What the default Bluetooth adapter can do. Reading it does not advertise anything.</summary>
/// <param name="Present">An adapter exists.</param>
/// <param name="LowEnergySupported">The adapter supports Bluetooth LE.</param>
/// <param name="PeripheralRoleSupported">The adapter can act as an LE peripheral (required for HID over GATT).</param>
/// <param name="Address">Bluetooth address as 12 hex digits, or empty when there is no adapter.</param>
public sealed record BleAdapterInfo(bool Present, bool LowEnergySupported, bool PeripheralRoleSupported, string Address)
{
    /// <summary>True when the adapter can publish the HID service.</summary>
    public bool CanBeHidPeripheral => Present && LowEnergySupported && PeripheralRoleSupported;

    /// <summary>Queries the default adapter.</summary>
    public static async Task<BleAdapterInfo> GetDefaultAsync(CancellationToken cancellationToken = default)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (adapter is null)
        {
            return new BleAdapterInfo(false, false, false, string.Empty);
        }

        return new BleAdapterInfo(
            true,
            adapter.IsLowEnergySupported,
            adapter.IsPeripheralRoleSupported,
            adapter.BluetoothAddress.ToString("X12", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A one-line explanation for the user.</summary>
    public string Describe() => !Present
        ? "no Bluetooth adapter"
        : $"adapter {Address}: LE supported = {LowEnergySupported}, peripheral role supported = {PeripheralRoleSupported}";
}
