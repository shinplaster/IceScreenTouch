// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.App.Services;

namespace IceScreenTouch.App.Tests;

/// <summary>The trial quota of the automation API: counted over the whole trial, kept in the data folder, only while the trial runs.</summary>
public sealed class TrialApiQuotaTests : IDisposable
{
    private readonly TempFolder _dir = new();
    private readonly List<string> _log = [];
    private bool _trial = true;

    public void Dispose() => _dir.Dispose();

    private TrialApiQuota Create(int operations = 3, int captures = 2) =>
        new(_dir.Path, () => _trial, _log.Add, operations, captures, TimeSpan.FromMinutes(5));

    private string UsageFile => Path.Combine(_dir.Path, TrialApiQuota.FileName);

    [Fact]
    public void TheDefaultsAreTheOwnersDecision()
    {
        Assert.Equal(300, TrialApiQuota.DefaultOperationLimit);
        Assert.Equal(300, TrialApiQuota.DefaultCaptureLimit);
        Assert.Equal(TimeSpan.FromMinutes(5), TrialApiQuota.DefaultStreamLimit);
        Assert.Equal(new ApiTrialInfo(300, 300, 300, 300, 300), new TrialApiQuota(_dir.Path, () => true).Current);
    }

    [Fact]
    public void EachCounterIsUsedUpOnItsOwnAndARefusalCountsNothing()
    {
        var quota = Create();

        Assert.True(quota.TryTake(ApiQuotaCounter.Captures, out _));
        Assert.True(quota.TryTake(ApiQuotaCounter.Captures, out _));
        Assert.False(quota.TryTake(ApiQuotaCounter.Captures, out var limit));
        Assert.Equal(2, limit);
        Assert.Equal(new ApiTrialInfo(3, 3, 0, 2, 300), quota.Current);

        Assert.True(quota.TryTake(ApiQuotaCounter.Operations, out _)); // the other counter still has some left
        Assert.Equal(2, quota.Current!.OperationsLeft);
    }

    [Fact]
    public async Task ConcurrentRequestsNeverCountPastTheLimitAndTheFileHasTheLastCount()
    {
        var quota = Create(operations: 50, captures: 0);
        var taken = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                if (quota.TryTake(ApiQuotaCounter.Operations, out _))
                {
                    Interlocked.Increment(ref taken);
                }
            }
        })));

        Assert.Equal(50, taken);
        Assert.Equal(0, quota.Current!.OperationsLeft);
        Assert.Equal(new ApiTrialInfo(0, 50, 0, 0, 300), Create(operations: 50, captures: 0).Current); // the last write holds the last count
    }

    [Fact]
    public void AHugeFileIsNotReadAndCountsFromZero()
    {
        File.WriteAllText(UsageFile, "{\"operations\":3" + new string(' ', 10_000) + "}");

        Assert.Equal(new ApiTrialInfo(3, 3, 2, 2, 300), Create().Current);
        Assert.Single(_log, l => l.Contains("not valid", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCountsSurviveARestartAndDoNotResetWithTime()
    {
        var quota = Create();
        Assert.True(quota.TryTake(ApiQuotaCounter.Operations, out _));
        Assert.True(quota.TryTake(ApiQuotaCounter.Captures, out _));

        Assert.Equal("{\"operations\":1,\"captures\":1}\n", File.ReadAllText(UsageFile));
        Assert.Equal(new ApiTrialInfo(2, 3, 1, 2, 300), Create().Current);
        Assert.False(File.Exists(UsageFile + ".tmp"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("{\"operations\":-5,\"captures\":\"x\"}")]
    public void AMissingOrBrokenFileCountsFromZero(string content)
    {
        File.WriteAllText(UsageFile, content);

        Assert.Equal(new ApiTrialInfo(3, 3, 2, 2, 300), Create().Current);
    }

    [Fact]
    public void ACountAboveTheLimitLeavesNothingAndIsKept()
    {
        File.WriteAllText(UsageFile, "{\"operations\":999,\"captures\":1}");
        var quota = Create();

        Assert.Equal(new ApiTrialInfo(0, 3, 1, 2, 300), quota.Current);
        Assert.False(quota.TryTake(ApiQuotaCounter.Operations, out _));
    }

    [Fact]
    public void OutsideTheTrialNothingIsLimitedCountedOrWritten()
    {
        _trial = false;
        var quota = Create(operations: 0, captures: 0);

        Assert.Null(quota.Current);
        Assert.True(quota.TryTake(ApiQuotaCounter.Operations, out _));
        Assert.True(quota.TryTake(ApiQuotaCounter.Captures, out _));
        Assert.False(File.Exists(UsageFile));

        _trial = true; // (a license that turns into the trial is limited from then on)
        Assert.False(quota.TryTake(ApiQuotaCounter.Operations, out _));
    }

    [Fact]
    public void AFileThatCannotBeWrittenKeepsTheCountsInMemoryAndLogsOnce()
    {
        Directory.CreateDirectory(UsageFile); // a folder where the file should be: every write fails
        var quota = Create();

        Assert.True(quota.TryTake(ApiQuotaCounter.Operations, out _));
        Assert.True(quota.TryTake(ApiQuotaCounter.Operations, out _));

        Assert.Equal(1, quota.Current!.OperationsLeft);
        Assert.Single(_log, l => l.Contains("cannot write", StringComparison.Ordinal));
    }
}
