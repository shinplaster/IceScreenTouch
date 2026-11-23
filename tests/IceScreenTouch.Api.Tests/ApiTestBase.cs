// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Api.Tests;

public abstract class ApiTestBase : IDisposable
{
    protected ApiTestBase()
    {
        Time = new FakeTimeProvider();
        Host = new FakeHost(Time);
        Log = [];
        Controller = new ApiController(Host, () => Allowed, Time, Log.Add);

        // The fake engine answers synchronously: inline, the OCR pipeline runs on the fake clock like the touch operations.
        Controller.Ocr.CallInline = true;
    }

    internal FakeTimeProvider Time { get; }

    internal FakeHost Host { get; }

    internal ApiController Controller { get; }

    internal SyncList<string> Log { get; }

    protected bool Allowed { get; set; } = true;

    internal FakeTouchSink Touch => Host.TouchSink;

    internal FakeKeyboardSink Keys => Host.KeyboardSink;

    /// <summary>The 0..1 position as the digitizer gets it for the given rotation.</summary>
    protected static AxisPoint Expected(double u, double v, Rotation rotation) =>
        Geometry.UnitToAxis(Geometry.Rotate(new PointD(u, v), rotation));

    protected static void AssertError(ApiResult result, string code)
    {
        Assert.NotNull(result.Error);
        Assert.Equal(code, result.Error.Code);
        Assert.Equal(ApiErrorCodes.StatusOf(code), result.Error.Status);
    }

    protected static void AssertError<T>(ApiResult<T> result, string code) => AssertError(result.WithoutValue(), code);

    protected static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Waits (real time, short) for something that happens on a thread-pool thread, such as a queued cancellation.</summary>
    protected static async Task Eventually(Func<bool> condition, string what = "the condition")
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {what}");
            await Task.Delay(1);
        }
    }

    public void Dispose()
    {
        Controller.Dispose();
        GC.SuppressFinalize(this);
    }
}
