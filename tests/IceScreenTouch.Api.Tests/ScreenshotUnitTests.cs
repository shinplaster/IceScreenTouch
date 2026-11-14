// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Api.Tests;

/// <summary>The parts of the screenshot that need no server: the query, the shrinking, the frame and the rate limiter.</summary>
public class ScreenshotUnitTests
{
    private static ApiResult<ScreenshotOptions> Parse(params string[] pairs)
    {
        var query = new List<KeyValuePair<string, string>>();
        for (var i = 0; i < pairs.Length; i += 2)
        {
            query.Add(new KeyValuePair<string, string>(pairs[i], pairs[i + 1]));
        }

        return ScreenshotOptions.Parse(query);
    }

    [Fact]
    public void NoQueryIsAPngAtTheOriginalSize()
    {
        var options = Parse().Value!;

        Assert.Equal(new ScreenshotOptions(ScreenshotFormat.Png, null, 90), options);
    }

    [Fact]
    public void TheValuesAreTakenAsGiven()
    {
        Assert.Equal(new ScreenshotOptions(ScreenshotFormat.Jpeg, 16, 1), Parse("format", "jpeg", "maxSize", "16", "quality", "1").Value);
        Assert.Equal(new ScreenshotOptions(ScreenshotFormat.Jpeg, 8192, 100), Parse("quality", "100", "maxSize", "8192", "format", "jpeg").Value);
        Assert.Equal(new ScreenshotOptions(ScreenshotFormat.Jpeg, null, 90), Parse("format", "jpeg").Value);
    }

    [Theory]
    [InlineData("format", "JPEG")]
    [InlineData("format", "jpg")]
    [InlineData("format", " png")]
    [InlineData("maxSize", "٣٠٠")] // Arabic-Indic digits
    public void OnlyPlainAsciiDigitsAndLowerCaseNamesAreAccepted(string name, string value)
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest, Parse(name, value).Error?.Code);
    }

    [Fact]
    public void LeadingZerosAreStillPlainDigits()
    {
        Assert.Equal(16, Parse("maxSize", "0016").Value!.MaxSize);
    }

    [Fact]
    public void TheFirstProblemIsTheError()
    {
        Assert.Equal(ApiErrorCodes.InvalidRequest, Parse("width", "1", "format", "gif").Error?.Code);
        Assert.Contains("unknown", Parse("width", "1").Error!.Detail, StringComparison.Ordinal);
        Assert.Contains("only applies to format=jpeg", Parse("quality", "5").Error!.Detail, StringComparison.Ordinal);
        Assert.Contains("more than once", Parse("format", "png", "format", "png").Error!.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(100, 200, null, 100, 200)]
    [InlineData(100, 200, 200, 100, 200)]
    [InlineData(100, 200, 100, 50, 100)]
    [InlineData(200, 100, 100, 100, 50)]
    [InlineData(100, 100, 50, 50, 50)]
    [InlineData(1179, 2556, 1024, 472, 1024)]
    [InlineData(1000, 3, 100, 100, 1)] // never 0
    [InlineData(3, 1000, 100, 1, 100)]
    public void ShrunkSizeKeepsTheAspectAndNeverEnlarges(int w, int h, int? max, int expectedW, int expectedH)
    {
        Assert.Equal((expectedW, expectedH), ScreenshotEncoder.ShrunkSize(w, h, max));
    }

    [Fact]
    public void AFrameChecksItsBuffer()
    {
        Assert.Throws<ArgumentException>(() => new ApiPictureFrame(new byte[10], 4, 4, 16, false, Rotation.R0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiPictureFrame(new byte[100], 0, 4, 16, false, Rotation.R0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiPictureFrame(new byte[100], 4, 4, 15, false, Rotation.R0)); // stride below 4 x width
        Assert.Throws<ArgumentNullException>(() => new ApiPictureFrame(null!, 4, 4, 16, false, Rotation.R0));
        _ = new ApiPictureFrame(new byte[(16 * 3) + 16], 4, 4, 16, true, Rotation.R90); // the last row needs no padding
    }

    [Fact]
    public void AFrameRefusesAnAbsurdSizeWithoutOverflowing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiPictureFrame(new byte[10], PictureLimits.MaxSide + 1, 1, (PictureLimits.MaxSide + 1) * 4, false, Rotation.R0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiPictureFrame(new byte[10], 8_192, 8_192, 8_192 * 4, false, Rotation.R0)); // 67 M pixels: over the pixel limit
        Assert.Throws<ArgumentOutOfRangeException>(() => new ApiPictureFrame(new byte[10], int.MaxValue, 1, int.MaxValue, false, Rotation.R0)); // 4 x width wraps in int
        Assert.True(PictureLimits.Allows(7_680, 4_320));
        Assert.False(PictureLimits.Allows(0, 10));
        Assert.False(PictureLimits.Allows(int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void TheRateLimiterAdmitsTenInAnyRollingSecond()
    {
        var time = new FakeTimeProvider();
        var limiter = new ScreenshotRateLimiter(time);

        for (var i = 0; i < 10; i++)
        {
            Assert.True(limiter.TryAcquire());
            time.Advance(TimeSpan.FromMilliseconds(50));
        }

        Assert.False(limiter.TryAcquire()); // 500 ms since the first
        time.Advance(TimeSpan.FromMilliseconds(499));
        Assert.False(limiter.TryAcquire()); // 999 ms since the first
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(limiter.TryAcquire()); // the first one is exactly a second old
        Assert.False(limiter.TryAcquire()); // the second is 950 ms old
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.True(limiter.TryAcquire());
    }
}
