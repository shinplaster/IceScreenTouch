// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Core;

/// <summary>Constants shared by the HID report map and the coordinate conversion.</summary>
public static class HidConstants
{
    /// <summary>Logical maximum of the absolute touch axes (0..AxisMax spans the whole screen).</summary>
    public const int AxisMax = 10000;

    /// <summary>Number of fingers in one touch report.</summary>
    public const int MaxContacts = 2;

    /// <summary>Report ID of the relative mouse (AssistiveTouch fallback).</summary>
    public const byte MouseReportId = 1;

    /// <summary>Report ID of the absolute multi-touch digitizer.</summary>
    public const byte TouchReportId = 2;

    /// <summary>Report ID of the consumer control keys (optional: only with the extended report map).</summary>
    public const byte ConsumerReportId = 3;

    /// <summary>Consumer page usage "AC Home" (0x0C:0x0223). Did not go home on iPadOS 18.6.2 (2026-11-06).</summary>
    public const ushort ConsumerAcHome = 0x0223;

    /// <summary>Consumer page usage "Menu" (0x0C:0x0040): the home button on iPadOS 18.6.2 (confirmed 2026-11-06).</summary>
    public const ushort ConsumerMenu = 0x0040;

    /// <summary>Consumer page usage "Eject" (0x0C:0x00B8). On Apple keyboards it shows or hides the on-screen keyboard (unverified).</summary>
    public const ushort ConsumerEject = 0x00B8;

    /// <summary>Consumer page usage "AC Next Keyboard Layout Select" (0x0C:0x029D): the globe key (unverified).</summary>
    public const ushort ConsumerNextKeyboardLayout = 0x029D;

    /// <summary>Report ID of the keyboard (optional: only with the keyboard report map).</summary>
    public const byte KeyboardReportId = 4;
}
