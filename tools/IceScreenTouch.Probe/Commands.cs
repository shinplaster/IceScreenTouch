// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using IceScreenTouch.Ble;
using IceScreenTouch.Core;

namespace IceScreenTouch.Probe;

/// <summary>The interactive console commands of PoC 2 (poc/poc2-ble-hid/ble_hid_poc.py run_command).</summary>
internal sealed class Commands(HidPeripheral hid, ProbeServer? web, TextWriter output)
{
    public const string Help = """
        commands (touch coordinates are 0..10000, (0,0) = top-left):
          status                         show advertising / subscription state
          tap X Y [hold_ms]              absolute tap
          swipe X1 Y1 X2 Y2 [ms]         absolute swipe
          pinch CX CY R1 R2 [ms]         two-finger pinch (radius R1 -> R2)
          down X Y / move X Y / up       manual single-finger drag
          m DX DY                        relative mouse move (pixels-ish)
          home                           relative mouse: pin pointer to top-left
          click                          relative mouse: left click
          grid                           tap a 3x3 grid of points (blind, no measurement)
          homekey                        consumer Menu key (the home button)
          consumer 0xNNNN                press and release any consumer usage
          eject / globe                  consumer Eject (on-screen keyboard?) / AC Next Keyboard Layout Select
          (keyboard; not with --no-keyboard)
          type TEXT                      type ASCII text key by key (\n = Enter) for the current layout
          key NAME [NAME ...]            press keys: enter esc bs tab space left right up down del f1..f12
                                         kana eisu hankaku henkan muhenkan, a character, or 0xNN;
                                         modifiers as prefixes: ctrl+space cmd+a shift+tab alt+x
          layout [us|jis]                show / set the device's hardware keyboard layout (default us)
          keys                           type into the keyboard page (/keys, cursor in its box) and check it
          probe [r0|r90|r180|r270]       3x3 taps + swipe + pinch, compared with what the Safari probe page received
          scroll [trials] [step%]        wheel scrolls per momentum setting on the scroll page (/scroll)
          events [N]                     print the last N events the probe page received (default 20)
          clear                          forget collected probe events
          help / quit

        Measurement: open the URL printed at startup in iPhone Safari (same network as this PC).
        """;

    private readonly TouchPlayer _player = new(hid);

    private HidKeyboardLayout _layout = HidKeyboardLayout.Us;

    /// <summary>Runs one command line. Returns false to quit.</summary>
    public async Task<bool> RunAsync(string line, CancellationToken ct = default)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return true;
        }

        var cmd = parts[0].ToLowerInvariant();
        int Arg(int i, int? fallback = null) =>
            i + 1 < parts.Length ? int.Parse(parts[i + 1], CultureInfo.InvariantCulture)
            : fallback ?? throw new ArgumentException($"{cmd}: argument {i + 1} is missing");
        AxisPoint P(int i) => TouchPlayer.Axis(Arg(i), Arg(i + 1));

        switch (cmd)
        {
            case "quit" or "exit":
                return false;
            case "help":
                output.WriteLine(Help);
                break;
            case "status":
                PrintStatus(hid, output);
                await PrintConnectionAsync(hid, output).ConfigureAwait(false);
                break;
            case "tap":
                await _player.TapAsync(P(0), TimeSpan.FromMilliseconds(Arg(2, 60)), ct).ConfigureAwait(false);
                break;
            case "swipe":
                await _player.SwipeAsync(P(0), P(2), TimeSpan.FromMilliseconds(Arg(4, 300)), cancellationToken: ct).ConfigureAwait(false);
                break;
            case "pinch":
                await _player.PinchAsync(P(0), Arg(2), Arg(3), TimeSpan.FromMilliseconds(Arg(4, 400)), cancellationToken: ct).ConfigureAwait(false);
                break;
            case "down" or "move":
                await _player.DownAsync(P(0), ct).ConfigureAwait(false);
                break;
            case "up":
                await _player.LiftAsync(ct).ConfigureAwait(false);
                break;
            case "m":
                await MouseMoveAsync(Arg(0), Arg(1), ct).ConfigureAwait(false);
                break;
            case "home":
                for (var i = 0; i < 40; i++)
                {
                    // Push the pointer into the top-left corner (iOS clamps it to the screen).
                    await hid.SendMouseAsync(-127, -127, cancellationToken: ct).ConfigureAwait(false);
                    await Task.Delay(5, ct).ConfigureAwait(false);
                }

                break;
            case "click":
                await hid.SendMouseAsync(buttons: 1, cancellationToken: ct).ConfigureAwait(false);
                await Task.Delay(50, ct).ConfigureAwait(false);
                await hid.SendMouseAsync(cancellationToken: ct).ConfigureAwait(false);
                break;
            case "grid":
                foreach (var gy in new[] { 1000, 5000, 9000 })
                {
                    foreach (var gx in new[] { 1000, 5000, 9000 })
                    {
                        await _player.TapAsync(TouchPlayer.Axis(gx, gy), TimeSpan.FromMilliseconds(60), ct).ConfigureAwait(false);
                        await Task.Delay(400, ct).ConfigureAwait(false);
                    }
                }

                break;
            case "homekey":
                await hid.PressConsumerKeyAsync(HidConstants.ConsumerMenu, cancellationToken: ct).ConfigureAwait(false);
                break;
            case "consumer":
                await hid.PressConsumerKeyAsync(ParseHex16(parts.Length > 1 ? parts[1] : throw new ArgumentException("consumer: usage is missing")), cancellationToken: ct).ConfigureAwait(false);
                break;
            case "eject":
                await hid.PressConsumerKeyAsync(HidConstants.ConsumerEject, cancellationToken: ct).ConfigureAwait(false);
                break;
            case "globe":
                await hid.PressConsumerKeyAsync(HidConstants.ConsumerNextKeyboardLayout, cancellationToken: ct).ConfigureAwait(false);
                break;
            case "layout":
                if (parts.Length > 1)
                {
                    _layout = parts[1].ToLowerInvariant() switch
                    {
                        "us" => HidKeyboardLayout.Us,
                        "jis" => HidKeyboardLayout.Jis,
                        _ => throw new ArgumentException("layout: us or jis"),
                    };
                }

                output.WriteLine($"layout: {_layout}");
                break;
            case "type":
                {
                    // Everything after "type " verbatim (spaces included); a literal \n is Enter.
                    var start = line.IndexOf("type", StringComparison.OrdinalIgnoreCase) + 4;
                    var text = start < line.Length ? line[(start + 1)..] : string.Empty;
                    var skipped = await hid.TypeAsync(text.Replace("\\n", "\n", StringComparison.Ordinal), _layout, cancellationToken: ct).ConfigureAwait(false);
                    if (skipped.Length > 0)
                    {
                        output.WriteLine($"skipped (no key on {_layout}): {skipped}");
                    }

                    break;
                }

            case "key":
                foreach (var name in parts.Skip(1))
                {
                    if (!HidKeyboard.TryParse(name, _layout, out var key))
                    {
                        throw new ArgumentException($"key: unknown key '{name}'");
                    }

                    await hid.PressKeyAsync(key, cancellationToken: ct).ConfigureAwait(false);
                    await Task.Delay(40, ct).ConfigureAwait(false);
                }

                break;
            case "probe" when web is not null:
                // Through the page guard like the app's probes: a phone that locked or left the page stops the run
                // instead of tapping the lock screen's passcode pad (review, 2026-11-07).
                await new ProbeRunner(new TouchPlayer(new PageGuardSink(hid, web, ProbePage.Touch, output)), web, output).RunAsync(parts.Length > 1 ? parts[1] : "r0", ct: ct).ConfigureAwait(false);
                break;
            case "scroll" when web is not null:
                await new ScrollProbe(new PageGuardSink(hid, web, ProbePage.Scroll, output), web, output).RunAsync(Arg(0, 3), Arg(1, 10) / 100.0, ct: ct).ConfigureAwait(false);
                break;
            case "events" when web is not null:
                foreach (var e in web.Snapshot().TakeLast(Arg(0, 20)))
                {
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  {e.Type,-13} {e.PointerType,-6} id={e.Id} norm=({e.Nx},{e.Ny}) buttons={e.Buttons} viewport={e.Vw}x{e.Vh}"));
                }

                break;
            case "clear" when web is not null:
                web.Clear();
                break;
            case "keys" when web is not null:
                await new KeyboardProbe(hid, web, output).RunAsync(_layout, ct).ConfigureAwait(false);
                break;
            case "probe" or "scroll" or "events" or "clear" or "keys":
                output.WriteLine("the probe web server is not running");
                break;
            default:
                output.WriteLine("unknown command; type 'help'");
                break;
        }

        return true;
    }

    public static void PrintStatus(HidPeripheral hid, TextWriter output)
    {
        foreach (var s in hid.GetServiceStates())
        {
            output.WriteLine($"   service {s.Name} ({s.Uuid}) -> {s.Status}");
        }

        output.WriteLine($"   subscribed: mouse={hid.SubscribedCount(HidConstants.MouseReportId)} touch={hid.SubscribedCount(HidConstants.TouchReportId)} consumer={hid.SubscribedCount(HidConstants.ConsumerReportId)} keyboard={hid.SubscribedCount(HidConstants.KeyboardReportId)}");
        foreach (var d in hid.SubscribedDevices())
        {
            output.WriteLine($"   client: {d}");
        }

        var st = hid.Stats;
        output.WriteLine($"   protocol mode={hid.ProtocolMode} control point={hid.ControlPoint}; notifications sent={st.Sent} skipped={st.Skipped} failed={st.Failed} busy={st.Busy}");
    }

    /// <summary>
    /// The BLE connection parameters of each subscribed device, as Windows sees them (the interval bounds how
    /// soon a report can leave). Windows 11 (build 22000) and later.
    /// </summary>
    public static async Task PrintConnectionAsync(HidPeripheral hid, TextWriter output)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            output.WriteLine("   connection: parameters need Windows 11");
            return;
        }

        foreach (var id in hid.SubscribedDevices())
        {
            try
            {
                using var device = await Windows.Devices.Bluetooth.BluetoothLEDevice.FromIdAsync(id);
                if (device is null)
                {
                    output.WriteLine($"   connection: {id} not found");
                    continue;
                }

                var p = device.GetConnectionParameters();
                var phy = device.GetConnectionPhy();
                output.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"   connection: interval {p.ConnectionInterval * 1.25:0.##} ms, peripheral latency {p.ConnectionLatency} intervals, supervision timeout {p.LinkTimeout * 10} ms; PHY tx {(phy.TransmitInfo.IsUncoded1MPhy ? "1M" : phy.TransmitInfo.IsUncoded2MPhy ? "2M" : "coded")} rx {(phy.ReceiveInfo.IsUncoded1MPhy ? "1M" : phy.ReceiveInfo.IsUncoded2MPhy ? "2M" : "coded")} ({device.Name})"));
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException or UnauthorizedAccessException)
            {
                output.WriteLine($"   connection: cannot read the parameters ({ex.Message})");
            }
        }
    }

    private static ushort ParseHex16(string text) =>
        ushort.Parse(text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private async Task MouseMoveAsync(int dx, int dy, CancellationToken ct)
    {
        // Split into int8-sized steps.
        while (dx != 0 || dy != 0)
        {
            var sx = Math.Clamp(dx, -127, 127);
            var sy = Math.Clamp(dy, -127, 127);
            await hid.SendMouseAsync(sx, sy, cancellationToken: ct).ConfigureAwait(false);
            dx -= sx;
            dy -= sy;
            await Task.Delay(5, ct).ConfigureAwait(false);
        }
    }
}
