namespace IceScreenTouch.Core;

/// <summary>One finger in a touch report. X and Y are 0..<see cref="HidConstants.AxisMax"/>.</summary>
/// <param name="Id">Contact identifier; must stay the same for the whole gesture of one finger.</param>
/// <param name="X">Horizontal position in the phone's portrait frame.</param>
/// <param name="Y">Vertical position in the phone's portrait frame.</param>
/// <param name="Tip">True while the finger touches the screen.</param>
public readonly record struct TouchContact(byte Id, ushort X, ushort Y, bool Tip = true)
{
    /// <summary>The same finger at the same place, lifted.</summary>
    public TouchContact Lifted() => this with { Tip = false };
}

/// <summary>
/// HID report descriptor and input report encoding. <see cref="ReportMap"/> is byte-for-byte identical to PoC 2
/// (poc/poc2-ble-hid/ble_hid_poc.py). The app publishes <see cref="KeyboardReportMap"/> (plus the home button and the
/// keyboard). The iPhone caches the GATT layout, so any change here
/// would force the user to unpair and pair again.
/// </summary>
public static class HidReports
{
    /// <summary>Size of the touch input report (without the report ID): 2 x 6 bytes + contact count.</summary>
    public const int TouchReportLength = (HidConstants.MaxContacts * 6) + 1;

    /// <summary>Size of the mouse input report (without the report ID).</summary>
    public const int MouseReportLength = 4;

    private static readonly byte[] MouseDescriptor =
    [
        0x05, 0x01,        // Usage Page (Generic Desktop)
        0x09, 0x02,        // Usage (Mouse)
        0xA1, 0x01,        // Collection (Application)
        0x85, HidConstants.MouseReportId, // Report ID (1)
        0x09, 0x01,        //   Usage (Pointer)
        0xA1, 0x00,        //   Collection (Physical)
        0x05, 0x09,        //     Usage Page (Button)
        0x19, 0x01,        //     Usage Minimum (1)
        0x29, 0x03,        //     Usage Maximum (3)
        0x15, 0x00,        //     Logical Minimum (0)
        0x25, 0x01,        //     Logical Maximum (1)
        0x95, 0x03,        //     Report Count (3)
        0x75, 0x01,        //     Report Size (1)
        0x81, 0x02,        //     Input (Data,Var,Abs)
        0x95, 0x01,        //     Report Count (1)
        0x75, 0x05,        //     Report Size (5)
        0x81, 0x03,        //     Input (Const,Var,Abs) - padding
        0x05, 0x01,        //     Usage Page (Generic Desktop)
        0x09, 0x30,        //     Usage (X)
        0x09, 0x31,        //     Usage (Y)
        0x09, 0x38,        //     Usage (Wheel)
        0x15, 0x81,        //     Logical Minimum (-127)
        0x25, 0x7F,        //     Logical Maximum (127)
        0x75, 0x08,        //     Report Size (8)
        0x95, 0x03,        //     Report Count (3)
        0x81, 0x06,        //     Input (Data,Var,Rel)
        0xC0,              //   End Collection
        0xC0,              // End Collection
    ];

    private static readonly byte[] FingerDescriptor =
    [
        0x05, 0x0D,        //   Usage Page (Digitizer)
        0x09, 0x22,        //   Usage (Finger)
        0xA1, 0x02,        //   Collection (Logical)
        0x09, 0x42,        //     Usage (Tip Switch)
        0x15, 0x00,        //     Logical Minimum (0)
        0x25, 0x01,        //     Logical Maximum (1)
        0x75, 0x01,        //     Report Size (1)
        0x95, 0x01,        //     Report Count (1)
        0x81, 0x02,        //     Input (Data,Var,Abs)
        0x75, 0x07,        //     Report Size (7)
        0x95, 0x01,        //     Report Count (1)
        0x81, 0x03,        //     Input (Const) - padding
        0x09, 0x51,        //     Usage (Contact Identifier)
        0x75, 0x08,        //     Report Size (8)
        0x95, 0x01,        //     Report Count (1)
        0x15, 0x00,        //     Logical Minimum (0)
        0x26, 0xFF, 0x00,  //     Logical Maximum (255)
        0x81, 0x02,        //     Input (Data,Var,Abs)
        0x05, 0x01,        //     Usage Page (Generic Desktop)
        0x09, 0x30,        //     Usage (X)
        0x15, 0x00,        //     Logical Minimum (0)
        0x26, HidConstants.AxisMax & 0xFF, (HidConstants.AxisMax >> 8) & 0xFF, // Logical Maximum (AxisMax)
        0x75, 0x10,        //     Report Size (16)
        0x95, 0x01,        //     Report Count (1)
        0x81, 0x02,        //     Input (Data,Var,Abs)
        0x09, 0x31,        //     Usage (Y)
        0x81, 0x02,        //     Input (Data,Var,Abs)
        0xC0,              //   End Collection
    ];

    private static readonly byte[] TouchHeader =
    [
        0x05, 0x0D,        // Usage Page (Digitizer)
        0x09, 0x04,        // Usage (Touch Screen)
        0xA1, 0x01,        // Collection (Application)
        0x85, HidConstants.TouchReportId, // Report ID (2)
    ];

    private static readonly byte[] TouchFooter =
    [
        0x05, 0x0D,        //   Usage Page (Digitizer)
        0x09, 0x54,        //   Usage (Contact Count)
        0x15, 0x00,        //   Logical Minimum (0)
        0x25, HidConstants.MaxContacts, // Logical Maximum
        0x75, 0x08,        //   Report Size (8)
        0x95, 0x01,        //   Report Count (1)
        0x81, 0x02,        //   Input (Data,Var,Abs)
        0xC0,              // End Collection
    ];

    // One consumer usage (16-bit array) per report: the key held down, 0 = released.
    private static readonly byte[] ConsumerDescriptor =
    [
        0x05, 0x0C,        // Usage Page (Consumer)
        0x09, 0x01,        // Usage (Consumer Control)
        0xA1, 0x01,        // Collection (Application)
        0x85, HidConstants.ConsumerReportId, // Report ID (3)
        0x15, 0x00,        //   Logical Minimum (0)
        0x26, 0xFF, 0x03,  //   Logical Maximum (0x3FF)
        0x19, 0x00,        //   Usage Minimum (0)
        0x2A, 0xFF, 0x03,  //   Usage Maximum (0x3FF)
        0x75, 0x10,        //   Report Size (16)
        0x95, 0x01,        //   Report Count (1)
        0x81, 0x00,        //   Input (Data,Array,Abs)
        0xC0,              // End Collection
    ];

    // Keyboard: modifier bits, a reserved byte and 6 key slots (boot keyboard layout, but the key range covers
    // 0x00..0xFF so the Japanese LANG1/LANG2 keys (0x90/0x91) fit). No LED output report.
    private static readonly byte[] KeyboardDescriptor =
    [
        0x05, 0x01,        // Usage Page (Generic Desktop)
        0x09, 0x06,        // Usage (Keyboard)
        0xA1, 0x01,        // Collection (Application)
        0x85, HidConstants.KeyboardReportId, // Report ID (4)
        0x05, 0x07,        //   Usage Page (Keyboard/Keypad)
        0x19, 0xE0,        //   Usage Minimum (Left Control)
        0x29, 0xE7,        //   Usage Maximum (Right GUI)
        0x15, 0x00,        //   Logical Minimum (0)
        0x25, 0x01,        //   Logical Maximum (1)
        0x75, 0x01,        //   Report Size (1)
        0x95, 0x08,        //   Report Count (8)
        0x81, 0x02,        //   Input (Data,Var,Abs) - modifiers
        0x75, 0x08,        //   Report Size (8)
        0x95, 0x01,        //   Report Count (1)
        0x81, 0x01,        //   Input (Const) - reserved
        0x15, 0x00,        //   Logical Minimum (0)
        0x26, 0xFF, 0x00,  //   Logical Maximum (255)
        0x19, 0x00,        //   Usage Minimum (0)
        0x2A, 0xFF, 0x00,  //   Usage Maximum (255)
        0x75, 0x08,        //   Report Size (8)
        0x95, KeyboardKeySlots, // Report Count (6)
        0x81, 0x00,        //   Input (Data,Array,Abs) - keys
        0xC0,              // End Collection
    ];

    private static readonly byte[] ReportMapBytes = BuildReportMap(consumer: false, keyboard: false);

    private static readonly byte[] ExtendedReportMapBytes = BuildReportMap(consumer: true, keyboard: false);

    private static readonly byte[] KeyboardReportMapBytes = BuildReportMap(consumer: true, keyboard: true);

    private static readonly byte[] HidInformationBytes = [0x11, 0x01, 0x00, 0x02];

    /// <summary>The HID report map (characteristic 0x2A4B): relative mouse (ID 1) + 2-finger digitizer (ID 2).</summary>
    public static ReadOnlySpan<byte> ReportMap => ReportMapBytes;

    /// <summary>
    /// <see cref="ReportMap"/> followed by consumer control keys (ID 3, for the home button). A different map:
    /// devices paired with the other one must be unpaired and paired again.
    /// </summary>
    public static ReadOnlySpan<byte> ExtendedReportMap => ExtendedReportMapBytes;

    /// <summary>
    /// <see cref="ExtendedReportMap"/> followed by a keyboard (ID 4, for text input). What every tool
    /// publishes since 2026-11-06 (Probe <c>--no-keyboard</c> for <see cref="ExtendedReportMap"/>); a different map
    /// again, so devices must pair again.
    /// </summary>
    public static ReadOnlySpan<byte> KeyboardReportMap => KeyboardReportMapBytes;

    /// <summary>
    /// Version of what the app publishes (<see cref="KeyboardReportMap"/>), kept apart from the app version: 1 = the
    /// PoC 2 map (191 bytes), 2 = with the home button (216), 3 = with the keyboard (263). Raise it with every
    /// change of that map (a test pins the bytes to this number): devices must pair again, so the release notes
    /// say so and the app's minor version goes up too (CHANGELOG.md).
    /// </summary>
    public const int ReportMapVersion = 3;

    /// <summary>The report map for the given optional collections (the keyboard needs the consumer keys).</summary>
    public static ReadOnlySpan<byte> ReportMapFor(bool consumer, bool keyboard) =>
        keyboard ? (consumer ? KeyboardReportMap : throw new ArgumentException("the keyboard map includes the consumer keys", nameof(consumer)))
        : consumer ? ExtendedReportMap : ReportMap;

    /// <summary>Number of key slots in one keyboard report.</summary>
    public const byte KeyboardKeySlots = 6;

    /// <summary>Size of the keyboard input report (without the report ID): modifiers, reserved, 6 keys.</summary>
    public const int KeyboardReportLength = 2 + KeyboardKeySlots;

    /// <summary>
    /// Encodes a keyboard report (ID 4): <paramref name="modifiers"/> (bit 0 = Left Control .. bit 7 = Right GUI)
    /// and up to 6 keys held down (Keyboard/Keypad page usages). No keys = everything released.
    /// </summary>
    public static byte[] EncodeKeyboard(byte modifiers = 0, ReadOnlySpan<byte> keys = default)
    {
        if (keys.Length > KeyboardKeySlots)
        {
            throw new ArgumentException($"at most {KeyboardKeySlots} keys fit in one report", nameof(keys));
        }

        var report = new byte[KeyboardReportLength];
        report[0] = modifiers;
        keys.CopyTo(report.AsSpan(2));
        return report;
    }

    /// <summary>Size of the consumer control input report (without the report ID).</summary>
    public const int ConsumerReportLength = 2;

    /// <summary>Encodes a consumer control report (ID 3): <paramref name="usage"/> held down, or 0 for released.</summary>
    public static byte[] EncodeConsumer(ushort usage = 0) => [(byte)(usage & 0xFF), (byte)(usage >> 8)];

    /// <summary>HID Information (characteristic 0x2A4A): bcdHID 1.11, country 0, normally connectable.</summary>
    public static ReadOnlySpan<byte> HidInformation => HidInformationBytes;

    /// <summary>Report Reference descriptor (0x2908) value of an input report.</summary>
    public static byte[] InputReportReference(byte reportId) => [reportId, 0x01];

    private static byte[] BuildReportMap(bool consumer, bool keyboard)
    {
        var map = new List<byte>(256);
        map.AddRange(MouseDescriptor);
        map.AddRange(TouchHeader);
        for (var i = 0; i < HidConstants.MaxContacts; i++)
        {
            map.AddRange(FingerDescriptor);
        }

        map.AddRange(TouchFooter);
        if (consumer)
        {
            map.AddRange(ConsumerDescriptor);
        }

        if (keyboard)
        {
            map.AddRange(KeyboardDescriptor);
        }

        return [.. map];
    }

    /// <summary>
    /// Encodes a touch report (ID 2). Unused finger slots are zero. The contact count is the number of
    /// contacts given, including lifted ones.
    /// </summary>
    /// <remarks>
    /// A lift must be sent as <c>Tip = false</c> at the finger's LAST position. Sending (0, 0) for a
    /// released finger makes iOS see the finger jump to the top-left corner before it is lifted.
    /// </remarks>
    public static byte[] EncodeTouch(ReadOnlySpan<TouchContact> contacts)
    {
        if (contacts.Length > HidConstants.MaxContacts)
        {
            throw new ArgumentException($"at most {HidConstants.MaxContacts} contacts fit in one report", nameof(contacts));
        }

        var report = new byte[TouchReportLength];
        for (var i = 0; i < contacts.Length; i++)
        {
            var c = contacts[i];
            var x = Math.Min(c.X, (ushort)HidConstants.AxisMax);
            var y = Math.Min(c.Y, (ushort)HidConstants.AxisMax);
            var o = i * 6;
            report[o] = c.Tip ? (byte)0x01 : (byte)0x00;
            report[o + 1] = c.Id;
            report[o + 2] = (byte)(x & 0xFF);
            report[o + 3] = (byte)(x >> 8);
            report[o + 4] = (byte)(y & 0xFF);
            report[o + 5] = (byte)(y >> 8);
        }

        report[^1] = (byte)contacts.Length;
        return report;
    }

    /// <summary>Encodes a relative mouse report (ID 1). Movement is clamped to -127..127.</summary>
    public static byte[] EncodeMouse(int dx = 0, int dy = 0, int wheel = 0, int buttons = 0) =>
        [(byte)(buttons & 0x07), S8(dx), S8(dy), S8(wheel)];

    private static byte S8(int v) => unchecked((byte)(sbyte)Math.Clamp(v, -127, 127));
}
