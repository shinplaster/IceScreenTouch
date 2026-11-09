// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

public class HidReportsTests
{
    // Golden values dumped from poc/poc2-ble-hid/ble_hid_poc.py (REPORT_MAP, touch_report, mouse_report).
    // The iPhone caches the GATT layout, so the C# port must stay byte-identical to the verified PoC.
    private const string PocReportMap =
        "05010902a10185010901a1000509190129031500250195037501810295017505810305010930093109381581257f750895038106c0c0" +
        "050d0904a1018502" +
        "050d0922a102094215002501750195018102750795018103095175089501150026ff00810205010930150026102775109501810209318102c0" +
        "050d0922a102094215002501750195018102750795018103095175089501150026ff00810205010930150026102775109501810209318102c0" +
        "050d095415002502750895018102c0";

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    [Fact]
    public void ReportMapIsByteIdenticalToThePoc()
    {
        Assert.Equal(191, HidReports.ReportMap.Length);
        Assert.Equal(PocReportMap, Hex(HidReports.ReportMap));
    }

    // The map the app publishes, per HidReports.ReportMapVersion. A change of the map fails here: raise the version
    // (devices must pair again; CHANGELOG.md says so) and add its hash.
    [Fact]
    public void PublishedReportMapMatchesItsVersion()
    {
        Assert.Equal(3, HidReports.ReportMapVersion);
        Assert.Equal(263, HidReports.KeyboardReportMap.Length);
        Assert.Equal("9d2447f44df1dc363ebaf4f9dc620d57f64b7069618a0b1a73fce228dc4518a0", Hex(System.Security.Cryptography.SHA256.HashData(HidReports.KeyboardReportMap)));
    }

    [Fact]
    public void FixedGattValuesMatchThePoc()
    {
        Assert.Equal("11010002", Hex(HidReports.HidInformation));
        Assert.Equal("0201", Hex(HidReports.InputReportReference(HidConstants.TouchReportId)));
        Assert.Equal("0101", Hex(HidReports.InputReportReference(HidConstants.MouseReportId)));
    }

    [Fact]
    public void EmptyTouchReportIsAllZero()
    {
        Assert.Equal("00000000000000000000000000", Hex(HidReports.EncodeTouch([])));
        Assert.Equal(HidReports.TouchReportLength, HidReports.EncodeTouch([]).Length);
    }

    [Fact]
    public void OneFingerIsLittleEndianWithCountOne()
    {
        Assert.Equal("0100d204942600000000000001", Hex(HidReports.EncodeTouch([new TouchContact(0, 1234, 9876)])));
    }

    [Fact]
    public void TwoFingers()
    {
        var report = HidReports.EncodeTouch([new TouchContact(0, 2500, 7000), new TouchContact(1, 7500, 3000)]);
        Assert.Equal("0100c409581b01014c1db80b02", Hex(report));
    }

    [Fact]
    public void LiftKeepsTheLastPositionAndTheCount()
    {
        // The released fingers must NOT be reported at (0, 0): iOS would see a jump to the top-left corner.
        var report = HidReports.EncodeTouch([new TouchContact(0, 2500, 7000, Tip: false), new TouchContact(1, 7500, 3000, Tip: false)]);
        Assert.Equal("0000c409581b00014c1db80b02", Hex(report));
    }

    [Fact]
    public void CoordinatesAboveTheRangeAreClamped()
    {
        Assert.Equal("01050000102700000000000001", Hex(HidReports.EncodeTouch([new TouchContact(5, 0, 20000)])));
    }

    [Fact]
    public void MoreContactsThanSlotsIsAnError()
    {
        var three = new TouchContact[] { new(0, 1, 1), new(1, 2, 2), new(2, 3, 3) };
        Assert.Throws<ArgumentException>(() => HidReports.EncodeTouch(three));
    }

    [Fact]
    public void MouseReportClampsToInt8()
    {
        Assert.Equal("01058101", Hex(HidReports.EncodeMouse(5, -300, 1, 9)));
        Assert.Equal("00000000", Hex(HidReports.EncodeMouse()));
        Assert.Equal(HidReports.MouseReportLength, HidReports.EncodeMouse().Length);
    }

    [Fact]
    public void TheExtendedMapAddsConsumerKeysAfterTheUnchangedMap()
    {
        var map = HidReports.ReportMap.ToArray();
        var extended = HidReports.ExtendedReportMap.ToArray();

        Assert.Equal(map, extended.AsSpan(0, map.Length).ToArray());
        Assert.Equal("050c0901a1018503150026ff0319002aff03751095018100c0", Hex(extended.AsSpan(map.Length).ToArray()));
        Assert.Equal("2302", Hex(HidReports.EncodeConsumer(HidConstants.ConsumerAcHome)));
        Assert.Equal("0000", Hex(HidReports.EncodeConsumer()));
        Assert.Equal(HidReports.ConsumerReportLength, HidReports.EncodeConsumer().Length);
    }

    [Fact]
    public void TheKeyboardMapAddsAKeyboardAfterTheExtendedMap()
    {
        var extended = HidReports.ExtendedReportMap.ToArray();
        var keyboard = HidReports.KeyboardReportMap.ToArray();

        Assert.Equal(216, extended.Length);
        Assert.Equal(extended, keyboard.AsSpan(0, extended.Length).ToArray());
        Assert.Equal(
            "05010906a1018504" + "050719e029e7150025017501950881027508950181011500" + "26ff0019002aff00750895068100c0",
            Hex(keyboard.AsSpan(extended.Length).ToArray()));
        Assert.Equal(keyboard, HidReports.ReportMapFor(consumer: true, keyboard: true).ToArray());
        Assert.Equal(extended, HidReports.ReportMapFor(consumer: true, keyboard: false).ToArray());
        Assert.Equal(HidReports.ReportMap.ToArray(), HidReports.ReportMapFor(consumer: false, keyboard: false).ToArray());
        Assert.Throws<ArgumentException>(() => HidReports.ReportMapFor(consumer: false, keyboard: true).ToArray());
    }

    [Fact]
    public void KeyboardReportHasModifiersReservedAndSixKeys()
    {
        Assert.Equal("0000000000000000", Hex(HidReports.EncodeKeyboard()));
        Assert.Equal("0200040500000000", Hex(HidReports.EncodeKeyboard(HidModifiers.LeftShift, [0x04, 0x05])));
        Assert.Equal(HidReports.KeyboardReportLength, HidReports.EncodeKeyboard().Length);
        Assert.Throws<ArgumentException>(() => HidReports.EncodeKeyboard(0, [1, 2, 3, 4, 5, 6, 7]));
    }
}
