// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using IceScreenTouch.Api;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IceScreenTouch.Api.Tests;

/// <summary>
/// A real Kestrel listener on 127.0.0.1 (an ephemeral port) in front of the A1 controller and fakes, plus an HttpClient.
/// The controller's clock is the fake one: <see cref="Drive"/> advances it while a request is in flight.
/// </summary>
public abstract class ServerTestBase : ApiTestBase, IAsyncLifetime
{
    protected const string TestToken = TestTokens.Main;

    private readonly HttpClientHandler _handler = new() { UseProxy = false };
    private readonly ConcurrentQueue<string> _serverLog = new();

    protected ServerTestBase()
    {
        Client = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        Server = new ApiServer(Controller, TestToken, port: 0, log: _serverLog.Enqueue, time: Time);
    }

    internal ApiServer Server { get; }

    protected HttpClient Client { get; }

    protected int Port => Server.Port;

    /// <summary>The server's own log lines.</summary>
    protected IReadOnlyList<string> ServerLog => [.. _serverLog];

    public virtual async Task InitializeAsync()
    {
        var started = await Server.StartAsync();
        Assert.True(started.IsSuccess, started.Error);
    }

    public virtual async Task DisposeAsync()
    {
        await Server.DisposeAsync();
        Client.Dispose();
    }

    protected string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    /// <summary>Builds and sends a request. The token is added unless <paramref name="token"/> is null.</summary>
    protected Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? json = null,
        string? token = TestToken,
        string? contentType = "application/json",
        Action<HttpRequestMessage>? tweak = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, Url(path));
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8);
            request.Content.Headers.Remove("Content-Type");
            if (contentType is not null)
            {
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
            }
        }

        if (token is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        }

        tweak?.Invoke(request);
        return Client.SendAsync(request, cancellationToken);
    }

    protected Task<HttpResponseMessage> PostAsync(string path, string? json = null) => SendAsync(HttpMethod.Post, path, json);

    protected Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path);

    /// <summary>Waits for the response while advancing the fake clock (the operation's waits are on it).</summary>
    protected async Task<HttpResponseMessage> Drive(Task<HttpResponseMessage> request)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (!request.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "the request did not complete");
            Time.AdvanceInline(TimeSpan.FromMilliseconds(25));
            await Task.Delay(1);
        }

        return await request;
    }

    /// <summary>Sends a request that takes (fake) time and returns its response.</summary>
    protected async Task<HttpResponseMessage> PostDriven(string path, string json) => await Drive(PostAsync(path, json));

    protected static async Task<JsonDocument> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    /// <summary>Asserts a problem+json response with this code (and the status that belongs to it).</summary>
    protected static async Task<JsonElement> AssertProblem(HttpResponseMessage response, string code)
    {
        Assert.Equal(ApiErrorCodes.StatusOf(code), (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var doc = await ReadJson(response);
        var root = doc.RootElement.Clone();
        Assert.Equal("about:blank", root.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("title").GetString()));
        Assert.Equal((int)response.StatusCode, root.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("detail").GetString()));
        Assert.Equal(code, root.GetProperty("code").GetString());

        // The five members; only the OCR errors add their documented extension members.
        var extensions = root.EnumerateObject().Select(p => p.Name).Except(["type", "title", "status", "detail", "code"]).ToList();
        string[] allowed = code switch
        {
            ApiErrorCodes.OcrUnavailable => ["reason", "engine", "available", "engines"],
            ApiErrorCodes.TextNotFound or ApiErrorCodes.AmbiguousText or ApiErrorCodes.TapOutsideScreen => ["matches", "matchCount"],
            _ => [],
        };
        Assert.All(extensions, name => Assert.Contains(name, allowed));
        return root;
    }

    protected static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(2);
        }
    }

    protected bool JournalHas(string entry) => Host.Journal.Contains(entry);

    protected static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    protected static readonly HttpMethod Put = HttpMethod.Put;
}

/// <summary>Tokens in the format of <see cref="ApiToken.Generate"/> (43 Base64url characters).</summary>
internal static class TestTokens
{
    public const string Main = "AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdA";
    public const string One = "BbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdA";
    public const string Two = "CbCdEfGhIjKlMnOpQrStUvWxYz0123456789-_abcdA";
}
