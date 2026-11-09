// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text;
using IceScreenTouch.Api;
using IceScreenTouch.Core;

namespace IceScreenTouch.Api.Tests;

/// <summary>JSON in: names, types, strictness, limits. Nothing malformed reaches the device.</summary>
public class ServerBindingTests : ServerTestBase
{
    [Fact]
    public async Task NamesAreCamelCaseAndSpaceIsALowercaseString()
    {
        // pixel space with a ref (property "ref"), the same names as docs/API-DESIGN.md chapter 7.
        var response = await PostDriven("/v1/touch/tap", """{ "x": 195, "y": 422, "space": "pixel", "ref": { "width": 390, "height": 844 }, "holdMs": 70 }""");

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal(Expected(0.5, 0.5, Rotation.R0), Touch.Downs.Single().First);
    }

    [Theory]
    [InlineData("screen")]
    [InlineData("device")]
    public async Task OtherSpacesAreAccepted(string space)
    {
        var response = await PostDriven("/v1/touch/tap", $$"""{ "x": 0.5, "y": 0.5, "space": "{{space}}", "holdMs": 70 }""");

        Assert.Equal(200, (int)response.StatusCode);
    }

    [Theory]
    [InlineData("""{ "x": 0.5, "y": 0.5, "space": "Pixel" }""")]
    [InlineData("""{ "x": 0.5, "y": 0.5, "space": "nonsense" }""")]
    [InlineData("""{ "x": 0.5, "y": 0.5, "space": 1 }""")]
    [InlineData("""{ "x": 0.5, "y": 0.5, "space": "pixel" }""")]
    public async Task AnUnknownSpaceIsRejected(string body)
    {
        var response = await PostAsync("/v1/touch/tap", body);

        await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
    }

    [Theory]
    [InlineData("""{ "y": 0.5 }""")] // missing required x
    [InlineData("""{ "x": 0.5 }""")]
    [InlineData("""{ "x": "0.5", "y": 0.5 }""")] // a string where a number belongs
    [InlineData("""{ "x": 0.5, "y": 0.5, "holdMs": 1.5 }""")] // a fraction where an integer belongs
    [InlineData("""{ "x": 0.5, "y": 0.5, "holdMs": "80" }""")]
    [InlineData("""{ "x": 0.5, "y": 0.5, "unknown": 1 }""")] // unknown member
    [InlineData("""{ "x": 0.5, "y": 0.5, "X": 0.1 }""")] // names are case sensitive
    [InlineData("""{ "x": null, "y": 0.5 }""")]
    [InlineData("""[ 1, 2 ]""")]
    [InlineData("""{ "x": 0.5, "y": 0.5 } trailing""")]
    [InlineData("""{ "x": 0.5, "y": """)] // truncated
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("null")]
    public async Task AMalformedOrIncompleteBodyIs400(string body)
    {
        var response = await PostAsync("/v1/touch/tap", body);

        await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
        Assert.Empty(Host.Leases);
    }

    [Fact]
    public async Task ABodyIsRequiredWhereTheControllerNeedsOne()
    {
        // No body at all (no Content-Type needed): the controller says a body is required.
        var response = await SendAsync(HttpMethod.Post, "/v1/touch/tap", json: null);

        var problem = await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        Assert.Contains("body", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonNullablePropertyCannotBeNull()
    {
        await AssertProblem(await PostAsync("/v1/keys/press", """{ "key": null }"""), ApiErrorCodes.InvalidRequest);
        await AssertProblem(await PostAsync("/v1/keys/type", """{ "text": null }"""), ApiErrorCodes.InvalidRequest);
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task TheDetailNamesTheMemberButNeverEchoesTheValue()
    {
        var response = await PostAsync("/v1/touch/tap", """{ "x": "secret-value-123", "y": 0.5 }""");

        var problem = await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.DoesNotContain("secret-value-123", detail, StringComparison.Ordinal);
        Assert.Contains("$.x", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABodyOver64KbIsRefused()
    {
        var body = $$"""{ "text": "{{new string('a', 70 * 1024)}}" }""";

        await AssertRefusedOrReset(PostAsync("/v1/keys/type", body));
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task AChunkedBodyOver64KbIsRefusedToo()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url("/v1/keys/type"));
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {TestToken}");
        request.Headers.TransferEncodingChunked = true;
        request.Content = new PushStreamContentLike($$"""{ "text": "{{new string('a', 100 * 1024)}}" }""");
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        await AssertRefusedOrReset(Client.SendAsync(request));
        Assert.Empty(Keys.Reports);
    }

    [Fact]
    public async Task ABodyJustUnderTheLimitIsRead()
    {
        // 1000 characters is the controller's own limit: the server hands it over, the controller refuses it.
        var body = $$"""{ "text": "{{new string('a', 60 * 1024)}}" }""";

        var response = await PostAsync("/v1/keys/type", body);

        var problem = await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        Assert.DoesNotContain("64 KB", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/jsonx")]
    [InlineData("application/json; charset=utf-16")]
    [InlineData("text/json")]
    [InlineData(null)]
    public async Task ABodyMustBeJson(string? contentType)
    {
        var response = await SendAsync(HttpMethod.Post, "/v1/touch/tap", """{ "x": 0.5, "y": 0.5 }""", contentType: contentType);

        await AssertProblem(response, ApiErrorCodes.InvalidRequest);
        Assert.Empty(Touch.Frames);
        Assert.Empty(Host.Leases);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON; charset=UTF-8")]
    [InlineData("application/json;charset=\"utf-8\"")]
    public async Task JsonContentTypesAreAccepted(string contentType)
    {
        var response = await Drive(SendAsync(HttpMethod.Post, "/v1/touch/tap", """{ "x": 0.5, "y": 0.5, "holdMs": 70 }""", contentType: contentType));

        Assert.Equal(200, (int)response.StatusCode);
    }

    [Fact]
    public async Task ABodyOnABodylessRouteStillMustBeJson()
    {
        await AssertProblem(await SendAsync(HttpMethod.Post, "/v1/home", "a=b", contentType: "application/x-www-form-urlencoded"), ApiErrorCodes.InvalidRequest);
        Assert.Equal(0, Host.HomeSink.Presses);
        Assert.Equal(200, (int)(await SendAsync(HttpMethod.Post, "/v1/home", "{}")).StatusCode);
        Assert.Equal(1, Host.HomeSink.Presses);
    }

    [Fact]
    public async Task ServerRejectionsAreNotLoggedWithTheirContent()
    {
        await PostAsync("/v1/touch/tap", """{ "x": "needle-in-body", "y": 0.5 }""");

        Assert.DoesNotContain(ServerLog, line => line.Contains("needle-in-body", StringComparison.Ordinal));
        Assert.Contains(ServerLog, line => line.Contains("invalid_request", StringComparison.Ordinal));
    }

    /// <summary>
    /// The server answers 400 <c>invalid_request</c>; but it may close the connection while the client is still sending
    /// the oversized body, and then the client sees a reset instead of the answer. Either way nothing is carried out.
    /// </summary>
    private static async Task AssertRefusedOrReset(Task<HttpResponseMessage> send)
    {
        try
        {
            await AssertProblem(await send, ApiErrorCodes.InvalidRequest);
        }
        catch (HttpRequestException)
        {
            // RST / connection closed before the response was read.
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A body without a known length (sent chunked).</summary>
    private sealed class PushStreamContentLike(string text) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return stream.WriteAsync(bytes, 0, bytes.Length);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
