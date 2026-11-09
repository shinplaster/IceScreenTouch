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

    private static IPAddress? LocalAddress() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Select(a => a.Address)
        .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
}
