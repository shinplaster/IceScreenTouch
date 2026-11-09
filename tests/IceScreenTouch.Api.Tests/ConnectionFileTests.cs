// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Buffers.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

public sealed class ConnectionFileTests : ApiTestBase, IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "icescreentouch-api-tests-" + Guid.NewGuid().ToString("N"));

    private static (int Port, string Token) Read(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(3, doc.RootElement.EnumerateObject().Count());
        Assert.Equal(Environment.ProcessId, doc.RootElement.GetProperty("pid").GetInt32());
        return (doc.RootElement.GetProperty("port").GetInt32(), doc.RootElement.GetProperty("token").GetString()!);
    }

    private string[] Files() => Directory.Exists(_dir) ? [.. Directory.GetFiles(_dir).Select(Path.GetFileName).Order()!] : [];

    [Fact]
    public void TheTokenIs256BitsOfBase64UrlWithoutPadding()
    {
        var token = ApiToken.Generate();

        Assert.Equal(43, token.Length);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        Assert.Equal(32, Base64Url.DecodeFromChars(token).Length);
        Assert.NotEqual(token, ApiToken.Generate());
    }

    [Fact]
    public void WriteCreatesTheDirectoryAndTheJson()
    {
        var file = new ApiConnectionFile(_dir);

        file.Write(47821, "abc");

        Assert.Equal(Path.Combine(_dir, "api.json"), file.Path);
        Assert.Equal((47821, "abc"), Read(file.Path));
        Assert.Equal(["api.json"], Files());
    }

    [Fact]
    public void WriteReplacesTheOldFileAndLeavesNoTemporaryFile()
    {
        var file = new ApiConnectionFile(_dir);
        file.Write(1000, "old");

        file.Write(2000, "new");

        Assert.Equal((2000, "new"), Read(file.Path));
        Assert.Equal(["api.json"], Files());
    }

    [Fact]
    public void AFailedWriteKeepsTheOldFileAndLeavesNothingBehind()
    {
        new ApiConnectionFile(_dir).Write(1000, "old");
        var failing = new ApiConnectionFile(_dir, _ => throw new IOException("no"));

        Assert.Throws<IOException>(() => failing.Write(2000, "new"));

        Assert.Equal((1000, "old"), Read(Path.Combine(_dir, "api.json")));
        Assert.Equal(["api.json"], Files());
    }

    [Fact]
    public void TheSecureHookRunsOnTheTemporaryFileBeforeTheTokenIsWritten()
    {
        var calls = new List<(string Name, long Length)>();
        var file = new ApiConnectionFile(_dir, path => calls.Add((Path.GetFileName(path), new FileInfo(path).Length)));

        file.Write(1, "secret-token");

        var call = Assert.Single(calls);
        Assert.NotEqual("api.json", call.Name);
        Assert.EndsWith(".tmp", call.Name, StringComparison.Ordinal);
        Assert.Equal(0, call.Length);
        Assert.Equal((1, "secret-token"), Read(file.Path));
    }

    [Fact]
    public void DeleteRemovesTheFileAndToleratesNone()
    {
        var file = new ApiConnectionFile(_dir);
        Assert.True(file.Delete());
        file.Write(1, "t");

        Assert.True(file.Delete());

        Assert.False(File.Exists(file.Path));
    }

    [Fact]
    public void OnWindowsTheFileIsForTheCurrentUserOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = new ApiConnectionFile(_dir);
        file.Write(1, "t");

        var security = new FileInfo(file.Path).GetAccessControl();

        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var rule = Assert.Single(rules);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
    }

    // ---- Together with the server ------------------------------------------------------------------------

    [Fact]
    public async Task TheFileIsWrittenAfterTheBindAndDeletedOnStop()
    {
        var file = new ApiConnectionFile(_dir);
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, time: Time);
        Assert.False(File.Exists(file.Path));

        var started = await server.StartAsync();

        Assert.True(started.IsSuccess, started.Error);
        Assert.Equal((server.Port, TestTokens.One), Read(file.Path));

        await server.StopAsync();

        Assert.False(File.Exists(file.Path));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task NothingIsWrittenWhenTheBindFails()
    {
        using var occupied = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((System.Net.IPEndPoint)occupied.LocalEndpoint).Port;
        var file = new ApiConnectionFile(_dir);
        await using var server = new ApiServer(Controller, TestTokens.One, port, file, time: Time);

        var result = await server.StartAsync();

        Assert.False(result.IsSuccess);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task AFileThatCannotBeWrittenFailsTheStartAndClosesThePort()
    {
        var file = new ApiConnectionFile(_dir, _ => throw new UnauthorizedAccessException("denied"));
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, time: Time);

        var result = await server.StartAsync();

        Assert.False(result.IsSuccess);
        Assert.False(result.PortInUse);
        Assert.Contains("api.json", result.Error, StringComparison.Ordinal);
        Assert.False(server.IsRunning);
        Assert.Equal(0, server.Port);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task ChangingTheTokenRewritesTheFile()
    {
        var file = new ApiConnectionFile(_dir);
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, time: Time);
        await server.StartAsync();

        await server.SetTokenAsync(TestTokens.Two);

        Assert.Equal((server.Port, TestTokens.Two), Read(file.Path));
    }

    [Fact]
    public async Task ChangingTheTokenOfAStoppedServerWritesNothing()
    {
        var file = new ApiConnectionFile(_dir);
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, time: Time);

        await server.SetTokenAsync(TestTokens.Two);

        Assert.False(File.Exists(file.Path));
        await server.StartAsync();
        Assert.Equal((server.Port, TestTokens.Two), Read(file.Path));
    }


    // ---- Stale files, retries, races (review round 1) ----------------------------------------------------

    [Fact]
    public async Task AStaleFileAndLeftoverTemporaryFilesAreRemovedBeforeTheBind()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "api.json"), """{ "port": 1, "token": "stale", "pid": 1 }""");
        File.WriteAllText(Path.Combine(_dir, "api.json.0123456789abcdef.tmp"), "half");
        File.WriteAllText(Path.Combine(_dir, "other.txt"), "keep");
        var file = new ApiConnectionFile(_dir);
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, time: Time);

        Assert.True((await server.StartAsync()).IsSuccess);

        Assert.Equal(["api.json", "other.txt"], Files());
        Assert.Equal((server.Port, TestTokens.One), Read(file.Path));
    }

    [Fact]
    public async Task TheStaleFileStaysAbsentWhenTheBindFails()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "api.json"), """{ "port": 1, "token": "stale", "pid": 1 }""");
        File.WriteAllText(Path.Combine(_dir, "api.json.0123456789abcdef.tmp"), "half");
        using var occupied = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        occupied.Start();
        var port = ((System.Net.IPEndPoint)occupied.LocalEndpoint).Port;
        await using var server = new ApiServer(Controller, TestTokens.One, port, new ApiConnectionFile(_dir), time: Time);

        var result = await server.StartAsync();

        Assert.False(result.IsSuccess);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task AStaleFileThatCannotBeRemovedFailsTheStart()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = new ApiConnectionFile(_dir);
        file.Write(1, "stale");
        var log = new List<string>();
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, log.Add, Time);

        using (new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = await server.StartAsync();

            Assert.False(result.IsSuccess);
            Assert.False(server.IsRunning);
        }

        Assert.Contains("[api] connection file could not be deleted", log);
    }

    [Fact]
    public async Task ADeleteWaitsForAFileHeldForAMoment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = new ApiConnectionFile(_dir);
        var log = new List<string>();
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, log.Add, Time);
        await server.StartAsync();
        var hold = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(40);
            await hold.DisposeAsync();
        });

        await server.StopAsync();
        await release;

        Assert.False(File.Exists(file.Path));
        Assert.DoesNotContain("[api] connection file could not be deleted", log);
    }

    [Fact]
    public async Task AFileThatStaysHeldIsReportedOnStop()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = new ApiConnectionFile(_dir);
        var log = new List<string>();
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, log.Add, Time);
        await server.StartAsync();

        using (new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await server.StopAsync();
        }

        Assert.False(server.IsRunning);
        Assert.Contains("[api] connection file could not be deleted", log);
        Assert.Contains(log, line => line == "[api] server stopped");
    }

    [Fact]
    public async Task AWriteWaitsForAFileHeldForAMoment()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = new ApiConnectionFile(_dir);
        file.Write(1000, "old");
        var hold = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(40);
            await hold.DisposeAsync();
        });

        file.Write(2000, "new");
        await release;

        Assert.Equal((2000, "new"), Read(file.Path));
        Assert.Equal(["api.json"], Files());
    }

    [Fact]
    public async Task AWriteThatNeverGetsTheFileFailsAndKeepsTheOldOne()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var file = new ApiConnectionFile(_dir);
        file.Write(1000, "old");

        using (new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var error = Assert.ThrowsAny<Exception>(() => file.Write(2000, "new"));
            Assert.True(error is IOException or UnauthorizedAccessException);
        }

        Assert.Equal((1000, "old"), Read(file.Path));
        Assert.Equal(["api.json"], Files());
        await Task.CompletedTask;
    }

    [Fact]
    public async Task StopRacingWithATokenChangeNeverLeavesAFileBehind()
    {
        var file = new ApiConnectionFile(_dir);
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, time: Time);
        for (var i = 0; i < 15; i++)
        {
            Assert.True((await server.StartAsync()).IsSuccess);

            await Task.WhenAll(
                Task.Run(() => server.SetTokenAsync(i % 2 == 0 ? TestTokens.Two : TestTokens.Main)),
                Task.Run(server.StopAsync),
                Task.Run(() => server.SetTokenAsync(ApiToken.Generate())));

            Assert.False(File.Exists(file.Path));
            Assert.Empty(Files());
        }
    }

    [Fact]
    public async Task AStartRacingWithATokenChangeWritesTheCurrentToken()
    {
        var file = new ApiConnectionFile(_dir);
        await using var server = new ApiServer(Controller, TestTokens.One, 0, file, time: Time);
        for (var i = 0; i < 15; i++)
        {
            var token = ApiToken.Generate();

            await Task.WhenAll(Task.Run(() => server.StartAsync()), Task.Run(() => server.SetTokenAsync(token)));

            Assert.Equal((server.Port, token), Read(file.Path));
            Assert.Equal(token, server.Token);
            await server.StopAsync();
        }
    }

    public new void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
        }

        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
