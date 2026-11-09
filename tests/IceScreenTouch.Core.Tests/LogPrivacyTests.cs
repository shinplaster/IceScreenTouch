// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

public class LogPrivacyTests
{
    private const string Phone = "BluetoothLE#BluetoothLE02:11:22:33:44:55-02:aa:bb:cc:dd:01";
    private const string Pad = "BluetoothLE#BluetoothLE02:11:22:33:44:55-02:aa:bb:cc:dd:02";

    [Fact]
    public void TheTokenIsStableAndShort()
    {
        var token = LogPrivacy.LogId(Phone);

        Assert.Equal(token, LogPrivacy.LogId(Phone));
        Assert.Matches("^device-[0-9a-f]{6}$", token);
    }

    [Fact]
    public void TheTokenDoesNotRevealTheAddresses()
    {
        var token = LogPrivacy.LogId(Phone);

        Assert.DoesNotContain("02:11", token, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("02:aa:bb:cc:dd:01", token, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Bluetooth", token, StringComparison.Ordinal);
    }

    [Fact]
    public void DifferentDevicesGetDifferentTokens()
    {
        // Same PC adapter, different phone address.
        Assert.NotEqual(LogPrivacy.LogId(Phone), LogPrivacy.LogId(Pad));
    }

    [Fact]
    public void NoIdIsHandled()
    {
        Assert.Equal("device-none", LogPrivacy.LogId(null));
        Assert.Equal("device-none", LogPrivacy.LogId(string.Empty));
    }
}
