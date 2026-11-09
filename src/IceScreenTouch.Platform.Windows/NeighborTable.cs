// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// The system's neighbor table (ARP and IPv6 neighbor discovery): which addresses on the local network belong to
/// the same network adapter. An iPhone connects to a receiver from a temporary IPv6 address that it does not
/// answer mDNS reverse queries for; its IPv4 address on the same adapter does answer (2026-11-06).
/// </summary>
public static partial class NeighborTable
{
    private const ushort AfInet = 2;
    private const ushort AfInet6 = 23;
    private const int RowSize = 88;
    private const int RowsOffset = 8;

    /// <summary>
    /// The other addresses (IPv4 first) that the neighbor table lists with the same hardware address as
    /// <paramref name="address"/>; empty when it is not in the table.
    /// </summary>
    public static IReadOnlyList<IPAddress> SameDevice(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var rows = Read();
        var mac = rows.FirstOrDefault(r => r.Address.Equals(address)).Mac;
        if (mac is null)
        {
            return [];
        }

        return [.. rows
            .Where(r => r.Mac == mac && !r.Address.Equals(address))
            .Select(r => r.Address)
            .Distinct()
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)];
    }

    private static List<(IPAddress Address, string? Mac)> Read()
    {
        var rows = new List<(IPAddress, string?)>();
        if (GetIpNetTable2(0, out var table) != 0 || table == 0)
        {
            return rows;
        }

        try
        {
            var count = Marshal.ReadInt32(table);
            var row = new byte[RowSize];
            for (var i = 0; i < count; i++)
            {
                Marshal.Copy(table + RowsOffset + (i * RowSize), row, 0, RowSize);
                if (Parse(row) is { } parsed)
                {
                    rows.Add(parsed);
                }
            }
        }
        finally
        {
            FreeMibTable(table);
        }

        return rows;
    }

    // MIB_IPNET_ROW2: SOCKADDR_INET (28 bytes), InterfaceIndex, InterfaceLuid (at 32), PhysicalAddress[32] (at 40),
    // PhysicalAddressLength (at 72), State (at 76), Flags, ReachabilityTime.
    private static (IPAddress, string?)? Parse(ReadOnlySpan<byte> row)
    {
        var family = BinaryPrimitives.ReadUInt16LittleEndian(row);
        IPAddress address;
        if (family == AfInet)
        {
            address = new IPAddress(row.Slice(4, 4));
        }
        else if (family == AfInet6)
        {
            address = new IPAddress(row.Slice(8, 16), BinaryPrimitives.ReadUInt32LittleEndian(row[24..]));
        }
        else
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(row[72..]);
        if (length is <= 0 or > 32)
        {
            return (address, null);
        }

        var mac = row.Slice(40, length);
        // All zero (incomplete) or broadcast and multicast entries say nothing about a device.
        if (!mac.ContainsAnyExcept((byte)0) || (mac[0] & 1) != 0)
        {
            return (address, null);
        }

        return (address, Convert.ToHexString(mac));
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetIpNetTable2(ushort family, out nint table);

    [LibraryImport("iphlpapi.dll")]
    private static partial void FreeMibTable(nint memory);
}
