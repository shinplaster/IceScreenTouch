// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using IceScreenTouch.Core;
using Windows.Devices.Bluetooth;

namespace IceScreenTouch.Ble;

/// <summary>
/// Asks Windows for throughput-optimized connection parameters for every device that subscribed. iOS gave the
/// HID link (with the keyboard) a peripheral latency of 4 at a 15 ms interval: the PC side may then sleep through
/// four connection events, and a report waits up to 75 ms (touch part of the latency 47 ms instead of 16 ms,
/// 2026-11-06). The request brings the latency to 0 and stays in force while it is held. Windows 11 (build 22000)
/// and later; does nothing elsewhere.
/// </summary>
internal sealed class ConnectionTuner(Action<string> log) : IDisposable
{
    private readonly Dictionary<string, (BluetoothLEDevice Device, IDisposable Request)> _held = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    /// <summary>Whether the OS has the API.</summary>
    [SupportedOSPlatformGuard("windows10.0.22000.0")]
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>Requests the parameters for new devices and releases those that left.</summary>
    public async Task UpdateAsync(IReadOnlyCollection<string> deviceIds)
    {
        if (!Supported)
        {
            return;
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            foreach (var gone in _held.Keys.Where(id => !deviceIds.Contains(id)).ToList())
            {
                Release(gone);
            }

            foreach (var id in deviceIds.Where(id => !_held.ContainsKey(id)))
            {
                await RequestAsync(id).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>The parameters of a held device, for the log ("interval 15 ms, peripheral latency 0, ...").</summary>
    [SupportedOSPlatform("windows10.0.22000.0")]
    public static string Describe(BluetoothLEDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        var p = device.GetConnectionParameters();
        return string.Create(CultureInfo.InvariantCulture, $"interval {p.ConnectionInterval * 1.25:0.##} ms, peripheral latency {p.ConnectionLatency}, supervision timeout {p.LinkTimeout * 10} ms");
    }

    public void Dispose()
    {
        _lock.Wait();
        try
        {
            _disposed = true;
            foreach (var id in _held.Keys.ToList())
            {
                Release(id);
            }
        }
        finally
        {
            _lock.Release();
        }

        _lock.Dispose();
    }

    [SupportedOSPlatform("windows10.0.22000.0")]
    private async Task RequestAsync(string id)
    {
        BluetoothLEDevice? device = null;
        try
        {
            device = await BluetoothLEDevice.FromIdAsync(id);
            if (device is null)
            {
                return;
            }

            var before = Describe(device);
            var request = device.RequestPreferredConnectionParameters(BluetoothLEPreferredConnectionParameters.ThroughputOptimized);
            log($"connection {LogPrivacy.LogId(id)}: {before}; requested throughput-optimized parameters: {request.Status}");
            if (request.Status != BluetoothLEPreferredConnectionParametersRequestStatus.Success)
            {
                request.Dispose();
                return;
            }

            var d = device;
            device.ConnectionParametersChanged += (_, _) =>
            {
                try
                {
                    log($"connection {LogPrivacy.LogId(id)}: {Describe(d)}");
                }
                catch (Exception ex) when (ex is COMException or ObjectDisposedException)
                {
                }
            };
            _held[id] = (device, request);
            device = null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException)
        {
            log($"connection {LogPrivacy.LogId(id)}: cannot request parameters ({ex.Message})");
        }
        finally
        {
            device?.Dispose();
        }
    }

    private void Release(string id)
    {
        if (_held.Remove(id, out var held))
        {
            held.Request.Dispose();
            held.Device.Dispose();
        }
    }
}
