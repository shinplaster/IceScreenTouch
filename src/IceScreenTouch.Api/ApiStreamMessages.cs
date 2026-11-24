// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace IceScreenTouch.Api;

/// <summary>One finger of a <c>down</c> or <c>move</c> message of the WebSocket stream.</summary>
internal sealed record StreamContact
{
    /// <summary>The contact id: 0 or 1 (the digitizer has two points).</summary>
    public required int Id { get; init; }

    /// <summary>Horizontal position in the space of the last <c>down</c>.</summary>
    public required double X { get; init; }

    /// <summary>Vertical position in the space of the last <c>down</c>.</summary>
    public required double Y { get; init; }
}

/// <summary>
/// A message from the client (docs/API-DESIGN.md, chapter 8). One flat record for every <c>op</c>: the members each op
/// accepts are checked by the stream (a member another op owns is <c>invalid_request</c>), because the strict JSON
/// context already refuses unknown members.
/// </summary>
internal sealed record StreamClientMessage
{
    /// <summary><c>auth</c>, <c>down</c>, <c>move</c>, <c>up</c>, <c>key</c> or <c>ping</c>.</summary>
    public required string Op { get; init; }

    /// <summary>The bearer token (<c>auth</c> only).</summary>
    public string? Token { get; init; }

    /// <summary>The number the answer (<c>ack</c> or <c>error</c>) carries (<c>down</c>, <c>move</c>, <c>up</c>, <c>key</c>).</summary>
    public int? Seq { get; init; }

    /// <summary><c>screen</c> (default), <c>pixel</c> or <c>device</c> (<c>down</c> only).</summary>
    public string? Space { get; init; }

    /// <summary>The screenshot size for <c>pixel</c> (<c>down</c> only).</summary>
    public PixelRef? Ref { get; init; }

    /// <summary>One or two fingers (<c>down</c>, <c>move</c>).</summary>
    public IReadOnlyList<StreamContact>? Contacts { get; init; }

    /// <summary>The key name (<c>key</c>).</summary>
    public string? Key { get; init; }

    /// <summary><c>cmd</c>, <c>shift</c>, <c>option</c>, <c>ctrl</c> (<c>key</c>).</summary>
    public IReadOnlyList<string>? Modifiers { get; init; }

    /// <summary>Pressed (true) or released (<c>key</c>).</summary>
    public bool? Down { get; init; }
}

/// <summary>A message to the client: <c>ready</c>, <c>ack</c>, <c>error</c>, <c>interrupted</c> or <c>pong</c>.</summary>
internal sealed record StreamEvent
{
    /// <summary>The kind.</summary>
    public required string Ev { get; init; }

    /// <summary>The API version (<c>ready</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Api { get; init; }

    /// <summary>The <c>seq</c> of the message answered (<c>ack</c>, <c>error</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seq { get; init; }

    /// <summary>The error code of <see cref="ApiErrorCodes"/> (<c>error</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Code { get; init; }

    /// <summary>A sentence for the person reading it, never user data (<c>error</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Detail { get; init; }

    /// <summary><c>user</c>, <c>cancelled</c>, <c>session_ended</c> or <c>idle</c> (<c>interrupted</c>); the error's reason, such as <c>trial_limit</c> (<c>error</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }

    /// <summary>The limit that was reached (<c>error</c> with the reason <c>trial_limit</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Limit { get; init; }

    /// <summary><c>operations</c> or <c>stream_seconds</c> (<c>error</c> with the reason <c>trial_limit</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Counter { get; init; }

    public static StreamEvent Ready() => new() { Ev = "ready", Api = 1 };

    public static StreamEvent Ack(int seq) => new() { Ev = "ack", Seq = seq };

    public static StreamEvent Pong() => new() { Ev = "pong" };

    // Only the trial quota's members travel on the stream (the OCR members belong to REST answers).
    public static StreamEvent Error(int? seq, ApiError error) => error.Extras is { Reason: ApiErrorReasons.TrialLimit } trial
        ? new() { Ev = "error", Seq = seq, Code = error.Code, Detail = error.Detail, Reason = trial.Reason, Limit = trial.Limit, Counter = trial.Counter }
        : new() { Ev = "error", Seq = seq, Code = error.Code, Detail = error.Detail };

    public static StreamEvent Interrupted(string reason) => new() { Ev = "interrupted", Reason = reason };
}
