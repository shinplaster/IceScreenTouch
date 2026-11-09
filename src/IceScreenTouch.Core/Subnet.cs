// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;

namespace IceScreenTouch.Core;

/// <summary>Whether an address is on the same subnet as one of this PC's addresses.</summary>
public static class Subnet
{
    /// <summary>
    /// True when <paramref name="candidate"/> shares the first <paramref name="prefixLength"/> bits with
    /// <paramref name="own"/> (both IPv4 or both IPv6; an IPv4-mapped IPv6 address counts as IPv4).
    /// </summary>
    public static bool Contains(IPAddress own, int prefixLength, IPAddress candidate)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(candidate);
        own = own.IsIPv4MappedToIPv6 ? own.MapToIPv4() : own;
        candidate = candidate.IsIPv4MappedToIPv6 ? candidate.MapToIPv4() : candidate;
        if (own.AddressFamily != candidate.AddressFamily)
        {
            return false;
        }

        var a = own.GetAddressBytes();
        var b = candidate.GetAddressBytes();
        if (prefixLength <= 0 || prefixLength > a.Length * 8)
        {
            return false; // no usable prefix: do not treat the whole internet as local
        }

        var whole = prefixLength / 8;
        for (var i = 0; i < whole; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        var rest = prefixLength % 8;
        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (a[whole] & mask) == (b[whole] & mask);
    }
}
