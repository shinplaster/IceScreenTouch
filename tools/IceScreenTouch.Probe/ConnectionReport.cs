// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace IceScreenTouch.Probe;

/// <summary>
/// The `conn` mode: the connection parameters (interval, peripheral latency, PHY) of the connected BLE devices,
/// and optionally a request for other parameters held for a while. Publishes nothing, so it works next to the app.
/// </summary>
[SupportedOSPlatform("windows10.0.22000.0")]
internal static class ConnectionReport
{
    /// <summary>Prints the parameters; with <paramref name="request"/> (throughput, balanced, power) asks for others and holds the request.</summary>
    public static async Task<int> RunAsync(string? request, int holdSeconds, CancellationToken ct)
    {
        var selector = BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected);
        var devices = await DeviceInformation.FindAllAsync(selector);
        Console.WriteLine($"{devices.Count} connected Bluetooth LE device(s)");
        var held = new List<(BluetoothLEDevice Device, IDisposable Request)>();
        foreach (var info in devices)
        {
            BluetoothLEDevice? device = null;
            try
            {
                device = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (device is null)
                {
                    continue;
                }

                Console.WriteLine($"  {Describe(device)}");
                if (request is { } kind)
                {
                    var preferred = kind switch
                    {
                        "throughput" => BluetoothLEPreferredConnectionParameters.ThroughputOptimized,
                        "power" => BluetoothLEPreferredConnectionParameters.PowerOptimized,
                        _ => BluetoothLEPreferredConnectionParameters.Balanced,
                    };
                    var handle = device.RequestPreferredConnectionParameters(preferred);
                    Console.WriteLine($"    requested {kind}: {handle.Status}");
                    var d = device;
                    d.ConnectionParametersChanged += (_, _) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] changed: {Describe(d)}");
                    held.Add((device, handle));
                    device = null; // kept open while the request is held
                }
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or UnauthorizedAccessException)
            {
                Console.WriteLine($"  {info.Name}: cannot read or change the parameters ({ex.Message})");
            }
            finally
            {
                device?.Dispose();
            }
        }

        if (held.Count == 0)
        {
            return 0;
        }

        Console.WriteLine($"holding the request for {holdSeconds} s (Ctrl+C to release)...");
        try
        {
            for (var s = 0; s < holdSeconds; s += 30)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, holdSeconds - s)), ct);
                foreach (var (d, _) in held)
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {Describe(d)}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var (d, r) in held)
        {
            r.Dispose();
            d.Dispose();
        }

        Console.WriteLine("released");
        return 0;
    }

    private static string Describe(BluetoothLEDevice device)
    {
        var p = device.GetConnectionParameters();
        var phy = device.GetConnectionPhy();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{device.DeviceId}: interval {p.ConnectionInterval * 1.25:0.##} ms, peripheral latency {p.ConnectionLatency} intervals, supervision timeout {p.LinkTimeout * 10} ms, PHY tx {Phy(phy.TransmitInfo)} rx {Phy(phy.ReceiveInfo)}");
    }

    private static string Phy(BluetoothLEConnectionPhyInfo info) => info.IsUncoded1MPhy ? "1M" : info.IsUncoded2MPhy ? "2M" : "coded";
}
