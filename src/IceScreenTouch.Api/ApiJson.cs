// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Text.Json.Serialization;

namespace IceScreenTouch.Api;

/// <summary>
/// The JSON of the API, source generated (the app is trimmed, so no reflection). Property names are camelCase;
/// unknown members, missing required members and nulls for non-nullable members are errors (the server answers 400
/// <c>invalid_request</c>); numbers must be JSON numbers.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(TargetRequest))]
[JsonSerializable(typeof(TapRequest))]
[JsonSerializable(typeof(LongPressRequest))]
[JsonSerializable(typeof(SwipeRequest))]
[JsonSerializable(typeof(PathRequest))]
[JsonSerializable(typeof(PinchRequest))]
[JsonSerializable(typeof(KeyPressRequest))]
[JsonSerializable(typeof(TypeTextRequest))]
[JsonSerializable(typeof(RotationRequest))]
[JsonSerializable(typeof(TypeTextResponse))]
[JsonSerializable(typeof(RotationInfo))]
[JsonSerializable(typeof(ApiStatus))]
[JsonSerializable(typeof(EmptyResponse))]
[JsonSerializable(typeof(CancelResponse))]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(ApiConnectionInfo))]
[JsonSerializable(typeof(StreamClientMessage))]
[JsonSerializable(typeof(StreamEvent))]
internal sealed partial class ApiJson : JsonSerializerContext;
