// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Buffers.Text;
using System.Security.Cryptography;

namespace IceScreenTouch.Api;

/// <summary>The bearer token of the API (docs/API-DESIGN.md, chapter 4).</summary>
public static class ApiToken
{
    /// <summary>Bytes of randomness in a token (256 bits).</summary>
    public const int Bytes = 32;

    /// <summary>Characters in a token: 32 bytes as Base64url without padding.</summary>
    public const int Length = 43;

    /// <summary>A new token: 256 random bits as Base64url without padding (43 characters).</summary>
    public static string Generate() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Bytes));

    /// <summary>True when <paramref name="token"/> is exactly what <see cref="Generate"/> can produce (the server accepts no other, so no weak token is ever in use).</summary>
    public static bool IsValid(string? token)
    {
        if (token is not { Length: Length })
        {
            return false;
        }

        // Decode and encode again: only the canonical text (URL-safe alphabet, no stray trailing bits) comes back equal.
        var bytes = new byte[Bytes];
        try
        {
            return Base64Url.TryDecodeFromChars(token, bytes, out var written)
                && written == Bytes
                && string.Equals(Base64Url.EncodeToString(bytes), token, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
