// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Ble;
using IceScreenTouch.Bridge;
using IceScreenTouch.Platform.Windows;

// Console version of the bridge (M3). See BridgeCommandLine.Usage.
BridgeSettings settings;
try
{
    settings = BridgeCommandLine.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine(BridgeCommandLine.Usage);
    return 64;
}

if (settings.Help)
{
    Console.WriteLine(BridgeCommandLine.Usage);
    return 0;
}

// Physical pixels everywhere (hook, cursor, windows), and a 1 ms timer for the 4 ms loop.
ProcessSetup.EnablePerMonitorDpiAwareness();
using var timerResolution = ProcessSetup.HighResolutionTimer();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
if (settings.DurationSeconds is { } seconds)
{
    cts.CancelAfter(TimeSpan.FromSeconds(seconds));
}

try
{
    return await BridgeApp.RunAsync(settings, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("cancelled");
    return 130;
}
catch (BleException ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    Console.Error.WriteLine("Is another process (PoC 2 / PoC 3 / IceScreenTouch.Probe) publishing the HID service?");
    return 2;
}
