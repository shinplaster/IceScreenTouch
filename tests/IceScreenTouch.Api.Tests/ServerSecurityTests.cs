// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;

namespace IceScreenTouch.Api.Tests;

/// <summary>Origin, Host and token checks, routing errors, and what the log never contains.</summary>
public class ServerSecurityTests : ServerTestBase
{
    private static readonly string[] OtherMethods = ["POST", "PUT", "OPTIONS", "GET"];

    // ---- Token -------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMissingTokenIs401WithAChallenge()
    {
        var response = await SendAsync(HttpMethod.Get, "/v1/status", token: null);

        await AssertProblem(response, ApiErrorCodes.Unauthorized);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcd")] // one character short
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdA0")] // one character long
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_ABCDA")] // same length, other characters
    public async Task AWrongTokenIs401(string token)
    {
        var response = await SendAsync(HttpMethod.Get, "/v1/status", token: token);

        await AssertProblem(response, ApiErrorCodes.Unauthorized);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("Basic dGVzdDp0ZXN0")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdA")] // no scheme
    [InlineData("Token AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdA")]
    public async Task AnotherSchemeIs401(string header)
    {
        var response = await SendAsync(HttpMethod.Get, "/v1/status", token: null, tweak: r => r.Headers.TryAddWithoutValidation("Authorization", header));

        await AssertProblem(response, ApiErrorCodes.Unauthorized);
    }

    [Fact]
    public async Task TheRightTokenPassesAndTheSchemeIsCaseInsensitive()
    {
        Assert.Equal(200, (int)(await GetAsync("/v1/status")).StatusCode);
        var lower = await SendAsync(HttpMethod.Get, "/v1/status", token: null, tweak: r => r.Headers.TryAddWithoutValidation("Authorization", $"bearer {TestToken}"));
        Assert.Equal(200, (int)lower.StatusCode);
    }

    [Fact]
    public async Task ARejectedRequestNeverReachesTheController()
    {
        await SendAsync(HttpMethod.Post, "/v1/home", token: null);
        await SendAsync(HttpMethod.Post, "/v1/home", token: "wrong");
        await SendAsync(HttpMethod.Post, "/v1/home", tweak: r => r.Headers.Host = "evil.com");
        await SendAsync(HttpMethod.Post, "/v1/home", tweak: r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.com"));

        Assert.Equal(0, Host.HomeSink.Presses);
        Assert.Empty(Host.Journal);
    }

    [Fact]
    public async Task ChangingTheTokenTakesEffectAtOnce()
    {
        Assert.Equal(200, (int)(await GetAsync("/v1/status")).StatusCode);
        var fresh = ApiToken.Generate();

        await Server.SetTokenAsync(fresh);

        await AssertProblem(await GetAsync("/v1/status"), ApiErrorCodes.Unauthorized);
        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Get, "/v1/status", token: fresh)).StatusCode);
        Assert.Equal(fresh, Server.Token);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("hunter2hunter2hunter2hunter2hunter2hunter2hunter2")] // long enough but not Base64url of 32 bytes
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdB")] // trailing bits set: not what Generate makes
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789+/abcdA")] // standard Base64, not Base64url
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdA=")] // padded
    public async Task OnlyTheGeneratedTokenFormatIsAccepted(string weak)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Server.SetTokenAsync(weak));
        await Assert.ThrowsAsync<ArgumentException>(() => Server.SetTokenAsync(null!));
        Assert.Throws<ArgumentException>(() => new ApiServer(Controller, weak));
        Assert.Equal(TestToken, Server.Token);
        Assert.True(ApiToken.IsValid(ApiToken.Generate()));
        Assert.True(ApiToken.IsValid(TestToken));
        Assert.False(ApiToken.IsValid(weak));
    }

    // ---- Origin ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://evil.com")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1")]
    public async Task AnyOriginHeaderIs403EvenWithTheToken(string origin)
    {
        var response = await SendAsync(HttpMethod.Get, "/v1/status", tweak: r => r.Headers.TryAddWithoutValidation("Origin", origin));

        await AssertProblem(response, ApiErrorCodes.ForbiddenOrigin);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task APreflightIs403AndGetsNoCorsHeaders()
    {
        var response = await SendAsync(
            HttpMethod.Options,
            "/v1/touch/tap",
            token: null,
            tweak: r =>
            {
                r.Headers.TryAddWithoutValidation("Origin", "http://evil.com");
                r.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
                r.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization, content-type");
            });

        await AssertProblem(response, ApiErrorCodes.ForbiddenOrigin);
        Assert.DoesNotContain(response.Headers, h => h.Key.StartsWith("Access-Control", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OriginIsRefusedOnTheOpenApiRouteAndBeforeTheHostAndTheToken()
    {
        var openapi = await SendAsync(HttpMethod.Get, "/v1/openapi.json", token: null, tweak: r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.com"));
        await AssertProblem(openapi, ApiErrorCodes.ForbiddenOrigin);

        var all = await SendAsync(HttpMethod.Get, "/v1/status", token: "wrong", tweak: r =>
        {
            r.Headers.Host = "evil.com";
            r.Headers.TryAddWithoutValidation("Origin", "http://evil.com");
        });
        await AssertProblem(all, ApiErrorCodes.ForbiddenOrigin);
    }

    // ---- Host --------------------------------------------------------------------------------------------

    [Fact]
    public async Task BothAllowedHostsWithTheBoundPortPass()
    {
        foreach (var host in new[] { $"127.0.0.1:{Port}", $"localhost:{Port}", $"LOCALHOST:{Port}", $"LocalHost:{Port}" })
        {
            var response = await SendAsync(HttpMethod.Get, "/v1/status", tweak: r => r.Headers.Host = host);
            Assert.Equal(200, (int)response.StatusCode);
        }
    }

    [Theory]
    [InlineData("evil.com")]
    [InlineData("evil.com:{port}")]
    [InlineData("127.0.0.1")] // no port: refused (the rule is exact)
    [InlineData("localhost")]
    [InlineData("127.0.0.1:1")]
    [InlineData("localhost:1")]
    [InlineData("127.0.0.1:0{port}")]
    [InlineData("[::1]:{port}")]
    [InlineData("127.0.0.2:{port}")]
    [InlineData("0.0.0.0:{port}")]
    [InlineData("127.0.0.1.evil.com:{port}")]
    [InlineData("localhost.evil.com:{port}")]
    [InlineData("localhost.:{port}")]
    public async Task AnyOtherHostIs403(string host)
    {
        var value = host.Replace("{port}", Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

        var response = await SendAsync(HttpMethod.Get, "/v1/status", tweak: r => r.Headers.Host = value);

        await AssertProblem(response, ApiErrorCodes.ForbiddenOrigin);
    }

    [Fact]
    public async Task TheHostCheckAppliesToOpenApiAndComesBeforeTheToken()
    {
        await AssertProblem(await SendAsync(HttpMethod.Get, "/v1/openapi.json", token: null, tweak: r => r.Headers.Host = "evil.com"), ApiErrorCodes.ForbiddenOrigin);
        await AssertProblem(await SendAsync(HttpMethod.Get, "/v1/status", token: null, tweak: r => r.Headers.Host = "evil.com"), ApiErrorCodes.ForbiddenOrigin);
        await AssertProblem(await SendAsync(HttpMethod.Get, "/v1/nothing", token: null, tweak: r => r.Headers.Host = "evil.com"), ApiErrorCodes.ForbiddenOrigin);
    }

    // ---- Routing -----------------------------------------------------------------------------------------

    [Fact]
    public async Task AnUnknownPathIs404ButOnlyForAnAuthorizedCaller()
    {
        await AssertProblem(await SendAsync(HttpMethod.Get, "/v1/nothing", token: null), ApiErrorCodes.Unauthorized);
        await AssertProblem(await GetAsync("/v1/nothing"), ApiErrorCodes.NotFound);
        await AssertProblem(await GetAsync("/"), ApiErrorCodes.NotFound);
        await AssertProblem(await GetAsync("/v1/status/"), ApiErrorCodes.NotFound);
        await AssertProblem(await GetAsync("/V1/status"), ApiErrorCodes.NotFound);
        await AssertProblem(await GetAsync("/v1/screenshot/"), ApiErrorCodes.NotFound);
    }

    [Fact]
    public async Task AWrongMethodIs405WithAnAllowHeader()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1/status", "{}");

        await AssertProblem(response, ApiErrorCodes.MethodNotAllowed);
        Assert.Equal(["GET"], response.Content.Headers.Allow.ToArray());

        var rotation = await SendAsync(HttpMethod.Delete, "/v1/rotation");
        await AssertProblem(rotation, ApiErrorCodes.MethodNotAllowed);
        Assert.Equal(["GET", "PUT"], rotation.Content.Headers.Allow.ToArray());

        await AssertProblem(await SendAsync(HttpMethod.Options, "/v1/touch/tap"), ApiErrorCodes.MethodNotAllowed);
        Assert.Equal(405, (int)(await SendAsync(HttpMethod.Head, "/v1/status")).StatusCode);
    }

    [Fact]
    public async Task AWrongMethodNeedsTheTokenToo()
    {
        foreach (var method in OtherMethods.Where(m => m != "GET"))
        {
            await AssertProblem(await SendAsync(new HttpMethod(method), "/v1/openapi.json", token: null), ApiErrorCodes.Unauthorized);
        }
    }

    // ---- Log ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoLogLineHasTheTokenAHeaderOrAnAddress()
    {
        await SendAsync(HttpMethod.Get, "/v1/status", token: "attacker-guess-of-the-token");
        await SendAsync(HttpMethod.Get, "/v1/status", token: null);
        await SendAsync(HttpMethod.Get, "/v1/status", tweak: r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.example"));
        await SendAsync(HttpMethod.Get, "/v1/status", tweak: r => r.Headers.Host = "evil.example");
        await PostDriven("/v1/touch/tap", """{ "x": 0.31337, "y": 0.5, "holdMs": 70 }""");
        await PostDriven("/v1/keys/type", """{ "text": "hunter2", "intervalMs": 5 }""");
        await Server.StopAsync();

        var all = ServerLog.Concat(Log).ToList();
        Assert.NotEmpty(all);
        foreach (var line in all)
        {
            Assert.DoesNotContain(TestToken, line, StringComparison.Ordinal);
            Assert.DoesNotContain("attacker-guess", line, StringComparison.Ordinal);
            Assert.DoesNotContain("evil.example", line, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer", line, StringComparison.Ordinal);
            Assert.DoesNotContain("0.31337", line, StringComparison.Ordinal);
            Assert.DoesNotContain("hunter2", line, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RejectedRequestsAreLoggedByCategoryAndRateLimited()
    {
        for (var i = 0; i < 100; i++)
        {
            await SendAsync(HttpMethod.Get, "/v1/status", token: "wrong");
        }

        // One line for the first, the rest held back.
        Assert.Single(ServerLog, line => line.Contains("unauthorized", StringComparison.Ordinal));
        Assert.Contains("[api] request rejected: unauthorized", ServerLog);

        await SendAsync(HttpMethod.Get, "/v1/status", tweak: r => r.Headers.TryAddWithoutValidation("Origin", "http://evil.example"));
        Assert.Contains("[api] request rejected: forbidden_origin", ServerLog); // another category logs at once

        // 10 seconds later the next one reports the count (99 held back + this one).
        Time.AdvanceInline(TimeSpan.FromSeconds(11));
        await SendAsync(HttpMethod.Get, "/v1/status", token: "wrong");
        Assert.Contains("[api] request rejected: unauthorized (x100)", ServerLog);
        Assert.Equal(2, ServerLog.Count(line => line.Contains("unauthorized", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task HeldBackCountsAreReportedWhenTheServerStops()
    {
        for (var i = 0; i < 5; i++)
        {
            await SendAsync(HttpMethod.Get, "/v1/status", token: "wrong");
        }

        await Server.StopAsync();

        Assert.Contains("[api] request rejected: unauthorized (x4)", ServerLog);
        Assert.Equal("[api] server stopped", ServerLog[^1]);
    }
}
