// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;

namespace IceScreenTouch.Api;

/// <summary>The body of a successful operation that has nothing to return: <c>{}</c>.</summary>
public sealed record EmptyResponse;

/// <summary>The response of <c>POST /v1/cancel</c>.</summary>
/// <param name="Cancelled">True when an operation or session was running and was told to stop; false when nothing ran.</param>
public sealed record CancelResponse(bool Cancelled);

/// <summary>
/// An error response, RFC 9457 <c>application/problem+json</c>. <see cref="Type"/> is always <c>about:blank</c> (the
/// meaning is in <see cref="Code"/>, the machine-readable extension member), <see cref="Title"/> is the HTTP status
/// text, <see cref="Detail"/> a sentence for the reader (never user data, never an exception message).
/// </summary>
/// <param name="Type">Always <c>about:blank</c>.</param>
/// <param name="Title">The HTTP status text.</param>
/// <param name="Status">The HTTP status.</param>
/// <param name="Detail">What went wrong.</param>
/// <param name="Code">One of <see cref="ApiErrorCodes"/>.</param>
public sealed record ApiProblem(string Type, string Title, int Status, string Detail, string Code)
{
    /// <summary>See <see cref="ApiErrorExtras.Reason"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>See <see cref="ApiErrorExtras.Engine"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Engine { get; init; }

    /// <summary>See <see cref="ApiErrorExtras.Available"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Available { get; init; }

    /// <summary>See <see cref="ApiErrorExtras.Engines"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Engines { get; init; }

    /// <summary>See <see cref="ApiErrorExtras.Matches"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<TextMatchResult>? Matches { get; init; }

    /// <summary>See <see cref="ApiErrorExtras.MatchCount"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MatchCount { get; init; }
}

/// <summary>The content of <c>api.json</c> (see <see cref="ApiConnectionFile"/>).</summary>
/// <param name="Port">The port of the API on 127.0.0.1.</param>
/// <param name="Token">The bearer token.</param>
/// <param name="Pid">The id of the process that listens (a client can check that the owner of the port is this process).</param>
public sealed record ApiConnectionInfo(int Port, string Token, int Pid);

internal static class ApiResponses
{
    public const string JsonContentType = "application/json; charset=utf-8";
    public const string ProblemContentType = "application/problem+json; charset=utf-8";

    public static Task WriteJsonAsync<T>(HttpContext context, int status, T value, JsonTypeInfo<T> typeInfo)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
        return WriteBytesAsync(context, status, JsonContentType, bytes);
    }

    public static Task WriteProblemAsync(HttpContext context, ApiError error)
    {
        var extras = error.Extras;
        var problem = new ApiProblem("about:blank", TitleOf(error.Status), error.Status, error.Detail, error.Code)
        {
            Reason = extras?.Reason,
            Engine = extras?.Engine,
            Available = extras?.Available,
            Engines = extras?.Engines,
            Matches = extras?.Matches,
            MatchCount = extras?.MatchCount,
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(problem, ApiJson.Default.ApiProblem);
        if (error.Code == ApiErrorCodes.RateLimited)
        {
            // The rate windows (screenshots, text readings) are one second.
            context.Response.Headers.RetryAfter = "1";
        }

        return WriteBytesAsync(context, error.Status, ProblemContentType, bytes);
    }

    public static Task WriteResultAsync(HttpContext context, ApiResult result) =>
        result.Error is { } error
            ? WriteProblemAsync(context, error)
            : WriteJsonAsync(context, StatusCodes.Status200OK, new EmptyResponse(), ApiJson.Default.EmptyResponse);

    public static Task WriteResultAsync<T>(HttpContext context, ApiResult<T> result, JsonTypeInfo<T> typeInfo) =>
        result.Error is { } error
            ? WriteProblemAsync(context, error)
            : WriteJsonAsync(context, StatusCodes.Status200OK, result.Value!, typeInfo);

    public static Task WriteBytesAsync(HttpContext context, int status, string contentType, byte[] bytes)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength = bytes.Length;
        return response.Body.WriteAsync(bytes, context.RequestAborted).AsTask();
    }

    private static string TitleOf(int status) => status switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        429 => "Too Many Requests",
        499 => "Client Closed Request",
        500 => "Internal Server Error",
        _ => "Error",
    };
}
