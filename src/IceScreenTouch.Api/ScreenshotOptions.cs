// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;

namespace IceScreenTouch.Api;

/// <summary>The image format of a screenshot.</summary>
public enum ScreenshotFormat
{
    /// <summary>PNG (lossless; the default).</summary>
    Png,

    /// <summary>JPEG (see <see cref="ScreenshotOptions.Quality"/>).</summary>
    Jpeg,
}

/// <summary>
/// The query of <c>GET /v1/screenshot</c>: <c>format=png|jpeg</c> (lower case, default png), <c>maxSize=16..8192</c> (the longest
/// side in pixels; only shrinks), <c>quality=1..100</c> (default 90; only with <c>format=jpeg</c>). Parameter names are case-sensitive,
/// an unknown or repeated parameter, a value that is not plain digits (no sign, no space) and anything out of range is refused.
/// </summary>
/// <param name="Format">The image format.</param>
/// <param name="MaxSize">The longest side in pixels the image may have, or null for the original size.</param>
/// <param name="Quality">The JPEG quality (also set, to the default, for PNG; it is not used there).</param>
public sealed record ScreenshotOptions(ScreenshotFormat Format, int? MaxSize, int Quality)
{
    /// <summary>The smallest <c>maxSize</c>.</summary>
    public const int MinMaxSize = 16;

    /// <summary>The largest <c>maxSize</c>.</summary>
    public const int MaxMaxSize = 8192;

    /// <summary>The JPEG quality when none is given.</summary>
    public const int DefaultQuality = 90;

    /// <summary>Parses the query pairs (name, value) as they came in the URL; the first problem is the error (<c>invalid_request</c>).</summary>
    public static ApiResult<ScreenshotOptions> Parse(IReadOnlyList<KeyValuePair<string, string>> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        Dictionary<string, string> given = new(StringComparer.Ordinal);
        foreach (var (name, value) in query)
        {
            if (name is not ("format" or "maxSize" or "quality"))
            {
                return Invalid("unknown query parameter (the parameters are format, maxSize and quality)");
            }

            if (!given.TryAdd(name, value))
            {
                return Invalid($"the query parameter {name} is given more than once");
            }
        }

        given.TryGetValue("format", out var format);
        given.TryGetValue("maxSize", out var maxSize);
        given.TryGetValue("quality", out var quality);

        var kind = format switch
        {
            null or "png" => ScreenshotFormat.Png,
            "jpeg" => ScreenshotFormat.Jpeg,
            _ => (ScreenshotFormat?)null,
        };
        if (kind is null)
        {
            return Invalid("format must be png or jpeg");
        }

        int? size = null;
        if (maxSize is not null)
        {
            if (!TryInteger(maxSize, MinMaxSize, MaxMaxSize, out var parsedSize))
            {
                return Invalid($"maxSize must be a whole number from {MinMaxSize} to {MaxMaxSize}");
            }

            size = parsedSize;
        }

        var level = DefaultQuality;
        if (quality is not null)
        {
            if (kind != ScreenshotFormat.Jpeg)
            {
                return Invalid("quality only applies to format=jpeg");
            }

            if (!TryInteger(quality, 1, 100, out level))
            {
                return Invalid("quality must be a whole number from 1 to 100");
            }
        }

        return ApiResult.Ok(new ScreenshotOptions(kind.Value, size, level));
    }

    private static ApiResult<ScreenshotOptions> Invalid(string detail) => ApiResult.Fail<ScreenshotOptions>(ApiError.InvalidRequest(detail));

    private static bool TryInteger(string text, int min, int max, out int value) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= min && value <= max;
}
