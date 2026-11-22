// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using IceScreenTouch.Platform.Windows;

namespace IceScreenTouch.Windows.Tests;

// The TCP table of this very process: a connection to one of the PC's own (non-loopback) addresses.
public sealed class TcpPeersTests
{
    [Fact]
    public async Task FindsTheRemoteEndOfAnEstablishedConnection()
    {
        var local = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
        if (local is null)
        {
            return; // no IPv4 address on a network adapter: nothing to connect to
        }

        using var listener = new TcpListener(local!, 0);
        listener.Start();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(local!, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = await accept;

        var peers = TcpPeers.RemoteAddresses(Environment.ProcessId);
        Assert.Contains(local!, peers);
        Assert.DoesNotContain(peers, IPAddress.IsLoopback);
        Assert.Empty(TcpPeers.RemoteAddresses(int.MaxValue - 1));
    }

    [Fact]
    public async Task TellsAnAcceptedConnectionFromOneThisProcessOpened()
    {
        var local = LocalAddress();
        if (local is null)
        {
            return;
        }

        // Only an outgoing connection: this process listens on nothing it is connected to.
        using var remote = new TcpListener(local, 0);
        remote.Start();
        var port = ((IPEndPoint)remote.LocalEndpoint).Port;
        using (var client = new TcpClient(AddressFamily.InterNetwork))
        {
            var accept = remote.AcceptTcpClientAsync();
            await client.ConnectAsync(local, port);
            using var server = await accept;
            remote.Stop(); // the accepted side stays open, the listening socket is gone
            Assert.Empty(TcpPeers.IncomingAddresses(Environment.ProcessId));
        }

        // A connection accepted on a port this process listens on.
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        using var caller = new TcpClient(AddressFamily.InterNetwork);
        var accepted = listener.AcceptTcpClientAsync();
        await caller.ConnectAsync(local, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var callee = await accepted;
        Assert.Equal([local], TcpPeers.IncomingAddresses(Environment.ProcessId));
    }

    // Issue #3: a computer whose only connection to the receiver is Google Cast is no mirror candidate; a device with
    // an AirPlay connection stays, even when it also has a Cast connection.
    [Fact]
    public void LeavesOutMachinesWithOnlyGoogleCastConnections()
    {
        var phone = IPAddress.Parse("192.0.2.10");
        var phoneV6 = IPAddress.Parse("2001:db8::10");
        var computer = IPAddress.Parse("192.0.2.20");
        var castingPhone = IPAddress.Parse("192.0.2.30");
        var chromecast = IPAddress.Parse("192.0.2.40");
        TcpPeers.Row[] rows =
        [
            new(5, 7000, phone, 51000, 1),
            new(5, 7100, phoneV6, 51001, 1),
            new(5, TcpPeers.GoogleCastPort, computer, 52000, 1),
            new(5, TcpPeers.GoogleCastPort, computer, 52001, 1),
            new(5, TcpPeers.GoogleCastPort, castingPhone, 53000, 1),
            new(5, 7000, castingPhone, 53001, 1),
            new(5, 54000, chromecast, TcpPeers.GoogleCastPort, 1),
        ];

        Assert.Equal([phone, phoneV6, castingPhone], TcpPeers.WithoutGoogleCast(rows));
    }

    // The remote port is read from the right place in both tables: each side of a loopback connection is a row.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task ReadsBothPortsOfAConnection(string loopback)
    {
        var address = IPAddress.Parse(loopback);
        if (address.AddressFamily == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
        {
            return;
        }

        using var listener = new TcpListener(address, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient(address.AddressFamily);
        var accept = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(address, port);
        using var server = await accept;
        var clientPort = ((IPEndPoint)client.Client.LocalEndPoint!).Port;

        var rows = TcpPeers.Established(Environment.ProcessId).ToList();
        Assert.Contains(rows, r => r.LocalPort == clientPort && r.RemotePort == port && r.Remote.Equals(address));
        Assert.Contains(rows, r => r.LocalPort == port && r.RemotePort == clientPort && r.Remote.Equals(address));
    }

    private static IPAddress? LocalAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
}
