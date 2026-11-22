// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace IceScreenTouch.Platform.Windows;

/// <summary>
/// The remote ends of a process's established TCP connections (IPv4 and IPv6), from the system TCP tables. A
/// receiver showing a device holds connections from it (AirServer: ports 7000, 7100 and 5001 from the iPhone;
/// it also holds Google Cast connections from other machines, 2026-11-06).
/// </summary>
public static partial class TcpPeers
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;
    private const int TcpTableOwnerPidAll = 5;
    private const uint StateListen = 2;
    private const uint StateEstablished = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const int Row4Size = 24;
    private const int Row6Size = 56;

    /// <summary>
    /// The Google Cast port. AirServer keeps Cast connections from other computers on the network open on it
    /// (2026-11-06); a device mirroring over AirPlay connects to the AirPlay ports instead.
    /// </summary>
    internal const int GoogleCastPort = 8009;

    /// <summary>
    /// The distinct remote addresses of the established connections owned by <paramref name="processId"/>,
    /// leaving out loopback (receivers talk to the local Bonjour service).
    /// </summary>
    public static IReadOnlyList<IPAddress> RemoteAddresses(int processId) => Distinct(Established(processId));

    /// <summary>
    /// Like <see cref="RemoteAddresses"/>, but leaving out the machines whose every connection to the process is a
    /// Google Cast one: other computers casting to the receiver (issue #3). A device mirroring over AirPlay stays,
    /// as it always has connections on the AirPlay ports.
    /// </summary>
    public static IReadOnlyList<IPAddress> MirrorCandidates(int processId) => WithoutGoogleCast(Established(processId));

    /// <summary>
    /// The distinct remote addresses of <paramref name="connections"/>, leaving out Google Cast connections (port
    /// 8009 at either end).
    /// </summary>
    internal static IReadOnlyList<IPAddress> WithoutGoogleCast(IEnumerable<Row> connections) =>
        Distinct(connections.Where(r => r.LocalPort != GoogleCastPort && r.RemotePort != GoogleCastPort));

    /// <summary>The established connections owned by <paramref name="processId"/>.</summary>
    internal static IEnumerable<Row> Established(int processId) =>
        ReadAll().Where(r => r.Pid == (uint)processId && r.State == StateEstablished);

    /// <summary>
    /// Like <see cref="RemoteAddresses"/>, but only the connections that other machines opened to the process: on a
    /// local port it listens on (a device mirroring to a receiver connects to its AirPlay port), not the ones it
    /// opened itself (a license server, an update check), and not Google Cast ones (other computers keep them open to
    /// AirServer while nothing is mirrored).
    /// </summary>
    public static IReadOnlyList<IPAddress> IncomingAddresses(int processId) =>
        Incoming(ReadAll().Where(r => r.Pid == (uint)processId));

    /// <summary>
    /// The distinct remote addresses of the established connections among <paramref name="rows"/> (one process's)
    /// on a local port that has a listening row, leaving out the Google Cast port.
    /// </summary>
    internal static IReadOnlyList<IPAddress> Incoming(IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        var listening = list.Where(r => r.State == StateListen).Select(r => r.LocalPort).ToHashSet();
        listening.Remove(GoogleCastPort);
        return Distinct(list.Where(r => r.State == StateEstablished && listening.Contains(r.LocalPort)));
    }

    private static List<IPAddress> Distinct(IEnumerable<Row> rows)
    {
        var found = new List<IPAddress>();
        foreach (var row in rows)
        {
            var address = row.Remote;
            if (!IPAddress.IsLoopback(address) && !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.Any) && !found.Contains(address))
            {
                found.Add(address);
            }
        }

        return found;
    }

    private static IEnumerable<Row> ReadAll() => Read(AfInet).Concat(Read(AfInet6));

    private static List<Row> Read(int family)
    {
        var size = 0;
        var buffer = Array.Empty<byte>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, family, TcpTableOwnerPidAll, 0);
            if (result == 0)
            {
                return Parse(buffer, family);
            }

            if (result != ErrorInsufficientBuffer)
            {
                return [];
            }

            buffer = new byte[size + 1024]; // connections may come and go between the calls
            size = buffer.Length;
        }

        return [];
    }

    private static List<Row> Parse(ReadOnlySpan<byte> table, int family)
    {
        var rows = new List<Row>();
        if (table.Length < 4)
        {
            return rows;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(table);
        var rowSize = family == AfInet ? Row4Size : Row6Size;
        for (var i = 0; i < count && 4 + ((i + 1) * rowSize) <= table.Length; i++)
        {
            var row = table.Slice(4 + (i * rowSize), rowSize);
            if (family == AfInet)
            {
                // dwState, dwLocalAddr, dwLocalPort, dwRemoteAddr, dwRemotePort, dwOwningPid
                rows.Add(new Row(
                    BinaryPrimitives.ReadUInt32LittleEndian(row),
                    Port(row[8..]),
                    new IPAddress(row.Slice(12, 4)),
                    Port(row[16..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(row[20..])));
            }
            else
            {
                // ucLocalAddr[16], dwLocalScopeId, dwLocalPort, ucRemoteAddr[16], dwRemoteScopeId, dwRemotePort, dwState, dwOwningPid
                var scope = BinaryPrimitives.ReadUInt32LittleEndian(row[40..]);
                var remote = new IPAddress(row.Slice(24, 16), scope);
                rows.Add(new Row(
                    BinaryPrimitives.ReadUInt32LittleEndian(row[48..]),
                    Port(row[20..]),
                    remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote,
                    Port(row[44..]),
                    BinaryPrimitives.ReadUInt32LittleEndian(row[52..])));
            }
        }

        return rows;
    }

    // A port in the table is a DWORD whose first two bytes hold the port in network byte order.
    private static int Port(ReadOnlySpan<byte> field) => BinaryPrimitives.ReadUInt16BigEndian(field);

    /// <summary>One row of a TCP table.</summary>
    internal readonly record struct Row(uint State, int LocalPort, IPAddress Remote, int RemotePort, uint Pid);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint GetExtendedTcpTable(byte[] table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order, int family, int tableClass, uint reserved);
}
