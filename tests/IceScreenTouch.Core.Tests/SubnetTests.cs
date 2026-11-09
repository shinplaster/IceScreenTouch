// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using IceScreenTouch.Core;

namespace IceScreenTouch.Core.Tests;

public class SubnetTests
{
    [Theory]
    [InlineData("192.168.1.5", 24, "192.168.1.178", true)]
    [InlineData("192.168.1.5", 24, "192.168.2.178", false)]
    [InlineData("10.1.2.3", 20, "10.1.15.200", true)] // a prefix that is not a whole byte
    [InlineData("10.1.2.3", 20, "10.1.16.1", false)]
    [InlineData("192.168.1.5", 24, "8.8.8.8", false)] // a server on the internet
    [InlineData("192.168.1.5", 24, "::ffff:192.168.1.9", true)] // IPv4-mapped
    [InlineData("2001:db8:1:2::10", 64, "2001:db8:1:2:a1b2:c3d4:e5f6:1", true)] // the phone's temporary IPv6 address
    [InlineData("2001:db8:1:2::10", 64, "2001:db8:9:9::1", false)]
    [InlineData("192.168.1.5", 24, "2001:db8:1:2::1", false)] // other family
    [InlineData("192.168.1.5", 0, "8.8.8.8", false)] // no prefix is not "everything"
    public void TellsWhetherAnAddressIsOnTheSubnet(string own, int prefix, string candidate, bool expected) =>
        Assert.Equal(expected, Subnet.Contains(IPAddress.Parse(own), prefix, IPAddress.Parse(candidate)));
}
