// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;
using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

/// <summary>What the real listener does: the address it binds, the port rules, start and stop.</summary>
public class ServerNetworkTests : ServerTestBase
{
    [Fact]
    public void ItListensOnLoopbackIPv4Only()
    {
        Assert.InRange(Port, 1, 65535);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(l => l.Port == Port).ToList();

        Assert.Contains(listeners, l => l.Address.Equals(IPAddress.Loopback));
        Assert.DoesNotContain(listeners, l => !l.Address.Equals(IPAddress.Loopback));
    }

    [Fact]
    public async Task AConnectionToIPv6LoopbackIsRefused()
    {
        if (!Socket.OSSupportsIPv6)
        {
            return;
        }

        using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);

        var error = await Assert.ThrowsAsync<SocketException>(async () => await socket.ConnectAsync(IPAddress.IPv6Loopback, Port));

        Assert.Equal(SocketError.ConnectionRefused, error.SocketErrorCode);
    }

    [Fact]
    public async Task ARealRequestWorksOnTheBoundPort()
    {
        var response = await GetAsync("/v1/status");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(Server.Port, Port);
        Assert.True(Server.IsRunning);
    }

    [Fact]
    public async Task StartIsIdempotent()
    {
        var again = await Server.StartAsync();

        Assert.True(again.IsSuccess);
        Assert.Equal(Port, again.Port);
        Assert.Single(ServerLog, line => line.Contains("listening", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StopIsIdempotentAndClosesThePort()
    {
        var port = Port;

        await Server.StopAsync();
        await Server.StopAsync();

        Assert.False(Server.IsRunning);
        Assert.Equal(0, Server.Port);
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAsync<SocketException>(async () => await socket.ConnectAsync(IPAddress.Loopback, port));
        Assert.Single(ServerLog, line => line == "[api] server stopped");
    }

    [Fact]
    public async Task ItCanBeStartedAgainAfterAStop()
    {
        await Server.StopAsync();

        var started = await Server.StartAsync();

        Assert.True(started.IsSuccess);
        Assert.Equal(200, (int)(await GetAsync("/v1/status")).StatusCode);
    }

    [Fact]
    public async Task StoppingEndsARunningOperationAndLifts()
    {
        var running = PostAsync("/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 5000 }""");
        await WaitUntil(() => Touch.Downs.Any(), "the finger going down");

        await Server.StopAsync();

        Assert.True(Touch.Frames[^1].IsLift || Touch.Frames[^1].IsEmpty);
        Assert.False(Controller.IsOperating);
        try
        {
            await running; // may still get its 499 out before the socket closes
        }
        catch (HttpRequestException)
        {
        }
    }

    [Fact]
    public async Task APortInUseIsAFailureAndNoOtherPortIsTried()
    {
        using var occupied = new TcpListener(IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
        var log = new List<string>();
        await using var second = new ApiServer(Controller, TestToken, port, log: log.Add, time: Time);

        var result = await second.StartAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.PortInUse);
        Assert.Equal(0, result.Port);
        Assert.Contains(port.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Error, StringComparison.Ordinal);
        Assert.False(second.IsRunning);
        Assert.Equal(0, second.Port);
        Assert.Equal(["[api] server could not start: port in use (" + port + ")"], log);
        // The port the first listener holds is unchanged and no server answers on it.
        using var probe = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMilliseconds(300) };
        await Assert.ThrowsAnyAsync<Exception>(() => probe.GetAsync($"http://127.0.0.1:{port}/v1/openapi.json"));
    }

    [Fact]
    public async Task APortInUseByAnotherApiServerFailsToo()
    {
        await using var second = new ApiServer(Controller, TestToken, Port, time: Time);

        var result = await second.StartAsync();

        Assert.False(result.IsSuccess);
        Assert.True(result.PortInUse);
        Assert.Equal(200, (int)(await GetAsync("/v1/status")).StatusCode); // the first server is untouched
    }

    [Fact]
    public async Task ARequestedPortIsUsedAsGiven()
    {
        // The port is probed free and then released; in the (unlikely) case that something else takes it in between, try again.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            int free;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                free = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            await using var server = new ApiServer(Controller, TestToken, free, time: Time);
            var started = await server.StartAsync();
            if (!started.IsSuccess && started.PortInUse)
            {
                continue;
            }

            Assert.True(started.IsSuccess, started.Error);
            Assert.Equal(free, server.Port);
            var response = await Client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{free}/v1/openapi.json"));
            Assert.Equal(200, (int)response.StatusCode);
            return;
        }

        Assert.Fail("no free port could be bound in 5 attempts");
    }

    [Fact]
    public void ThePortMustBeAPort()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiServer(Controller, TestToken, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiServer(Controller, TestToken, 65536));
        Assert.Equal(47821, ApiServer.DefaultPort);
    }

    [Fact]
    public void RoutesCannotBeAddedWhileRunningOrTwice()
    {
        Assert.Throws<InvalidOperationException>(() => Server.AddRoute(new ApiRoute("GET", "/v1/extra", _ => Task.CompletedTask)));
    }

    [Fact]
    public async Task AnExtraRouteAddedBeforeStartGoesThroughTheSameChecks()
    {
        await using var server = new ApiServer(Controller, TestToken, 0, time: Time);
        server.AddRoute(new ApiRoute("GET", "/v1/extra", ctx => ctx.Response.WriteAsync("extra")));
        server.AddRoute(new ApiRoute("GET", "/v1/open-extra", ctx => ctx.Response.WriteAsync("open"), Anonymous: true));
        Assert.Throws<ArgumentException>(() => server.AddRoute(new ApiRoute("GET", "/v1/extra", _ => Task.CompletedTask)));
        Assert.True((await server.StartAsync()).IsSuccess);
        var url = $"http://127.0.0.1:{server.Port}";

        Assert.Equal(401, (int)(await Client.GetAsync($"{url}/v1/extra")).StatusCode);
        Assert.Equal("open", await Client.GetStringAsync($"{url}/v1/open-extra"));
        var request = new HttpRequestMessage(HttpMethod.Get, $"{url}/v1/extra") { Headers = { { "Authorization", $"Bearer {TestToken}" } } };
        Assert.Equal("extra", await (await Client.SendAsync(request)).Content.ReadAsStringAsync());
        var origin = new HttpRequestMessage(HttpMethod.Get, $"{url}/v1/open-extra");
        origin.Headers.TryAddWithoutValidation("Origin", "http://evil.com");
        Assert.Equal(403, (int)(await Client.SendAsync(origin)).StatusCode);
    }
}
