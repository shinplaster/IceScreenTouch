// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

/// <summary>Round-1 review items for the server: configuration isolation, client-side failures, shutdown.</summary>
public class ServerHardeningTests : ServerTestBase
{
    [Fact]
    public async Task EnvironmentVariablesCannotAddEndpoints()
    {
        string[] names = ["ASPNETCORE_URLS", "DOTNET_URLS", "ASPNETCORE_HTTP_PORTS", "DOTNET_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS"];
        var saved = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable("DOTNET_URLS", "http://0.0.0.0:0");
            Environment.SetEnvironmentVariable("ASPNETCORE_HTTP_PORTS", "0");
            Environment.SetEnvironmentVariable("DOTNET_HTTP_PORTS", "0");
            Environment.SetEnvironmentVariable("Kestrel__Endpoints__Evil__Url", "http://0.0.0.0:0");
            await using var server = new ApiServer(Controller, TestToken, 0, time: Time);

            var result = await server.StartAsync();

            Assert.True(result.IsSuccess, result.Error);
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(l => l.Port == server.Port).ToList();
            Assert.Equal([IPAddress.Loopback], listeners.Select(l => l.Address).Distinct());
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            Environment.SetEnvironmentVariable("Kestrel__Endpoints__Evil__Url", null);
        }
    }

    [Fact]
    public async Task AClientThatHangsUpMidBodyIsNotAnInternalError()
    {
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await socket.ConnectAsync(IPAddress.Loopback, Port);
            var head = $"POST /v1/touch/tap HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\nAuthorization: Bearer {TestToken}\r\nContent-Type: application/json\r\nContent-Length: 1000\r\n\r\n{{\"x\":";
            await socket.SendAsync(Encoding.ASCII.GetBytes(head));
        } // closed with 990 bytes still to come

        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await socket.ConnectAsync(IPAddress.Loopback, Port);
            var head = $"POST /v1/keys/type HTTP/1.1\r\nHost: 127.0.0.1:{Port}\r\nAuthorization: Bearer {TestToken}\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\nZZ\r\n";
            await socket.SendAsync(Encoding.ASCII.GetBytes(head)); // a broken chunk size
            var buffer = new byte[4096];
            await socket.ReceiveAsync(buffer);
        }

        // The server is fine, and none of that was logged as its own failure.
        Assert.Equal(200, (int)(await GetAsync("/v1/status")).StatusCode);
        await Task.Delay(100);
        Assert.DoesNotContain(ServerLog, line => line.Contains("internal_error", StringComparison.Ordinal));
        Assert.Empty(Touch.Frames);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task AnAbortedRequestIsNotLoggedAsAnInternalError()
    {
        using var cts = new CancellationTokenSource();
        var running = SendAsync(HttpMethod.Post, "/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 5000 }""", cancellationToken: cts.Token);
        await WaitUntil(() => Touch.Downs.Any(), "the finger going down");

        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await WaitUntil(() => !Controller.IsOperating, "the operation having ended");

        await Task.Delay(50);
        Assert.DoesNotContain(ServerLog, line => line.Contains("internal_error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARequestThatArrivesWhileTheServerStopsDoesNotStartAnOperation()
    {
        var running = PostAsync("/v1/touch/long-press", """{ "x": 0.5, "y": 0.5, "holdMs": 5000 }""");
        await WaitUntil(() => Touch.Downs.Any(), "the finger going down");

        var stopping = Server.StopAsync();
        await stopping;

        Assert.False(Controller.IsOperating);
        try
        {
            await running;
        }
        catch (HttpRequestException)
        {
        }

        Assert.Single(Touch.Downs);
    }
}
