// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Api;
using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Api.Tests;

/// <summary>One touch report as the sink received it.</summary>
internal sealed record Frame(TimeSpan At, TouchContact[] Contacts)
{
    public bool IsEmpty => Contacts.Length == 0;

    public bool IsDown => Contacts.Length > 0 && Contacts.All(c => c.Tip);

    public bool IsLift => Contacts.Length > 0 && Contacts.All(c => !c.Tip);

    public AxisPoint First => new(Contacts[0].X, Contacts[0].Y);
}

/// <summary>One keyboard report as the sink received it.</summary>
internal sealed record KeyReport(TimeSpan At, byte Modifiers, byte[] Keys)
{
    public bool IsRelease => Modifiers == 0 && Keys.Length == 0;
}

/// <summary>Records every report with the fake time at which it was sent; refuses cancelled sends like a real link.</summary>
internal sealed class FakeTouchSink(FakeTimeProvider time, SyncList<string> journal) : ITouchSink
{
    private readonly DateTimeOffset _start = time.GetUtcNow();

    public bool IsSubscribed { get; set; } = true;

    public SyncList<Frame> Frames { get; } = [];

    /// <summary>The managed thread id each frame was sent on (same order as <see cref="Frames"/>).</summary>
    public SyncList<int> ThreadIds { get; } = [];

    public IEnumerable<Frame> Downs => Frames.Where(f => f.IsDown);

    public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = new Frame(time.GetUtcNow() - _start, contacts.ToArray());
        ThreadIds.Add(Environment.CurrentManagedThreadId);
        Frames.Add(frame);
        journal.Add(frame.IsEmpty ? "touch:empty" : frame.IsLift ? "touch:lift" : "touch:down");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeKeyboardSink(FakeTimeProvider time, SyncList<string> journal) : IKeyboardSink
{
    private readonly DateTimeOffset _start = time.GetUtcNow();

    public bool HasKeyboard { get; set; } = true;

    public SyncList<KeyReport> Reports { get; } = [];

    public ValueTask SendKeyboardAsync(byte modifiers, ReadOnlyMemory<byte> keys, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var report = new KeyReport(time.GetUtcNow() - _start, modifiers, keys.ToArray());
        Reports.Add(report);
        journal.Add(report.IsRelease ? "key:release" : "key:down");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeHomeSink(SyncList<string> journal) : IHomeButtonSink
{
    public bool HasHomeButton { get; set; } = true;

    public int Presses { get; private set; }

    public ValueTask PressHomeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Presses++;
        journal.Add("home");
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeLease : IInputLease
{
    private readonly CancellationTokenSource _user = new();
    private readonly SyncList<string> _journal;

    public FakeLease(SyncList<string> journal) => _journal = journal;

    public CancellationToken UserInput => _user.Token;

    public bool Disposed { get; private set; }

    /// <summary>Giving the input back fails (the lease is still counted as disposed).</summary>
    public bool ThrowOnDispose { get; set; }

    public bool SessionEnded { get; private set; }

    /// <summary>The session ends while the lease is held: the arbiter fires the same token as for the user's input.</summary>
    public void SimulateSessionEnd()
    {
        SessionEnded = true;
        _user.Cancel();
    }

    /// <summary>The user touches the mirror.</summary>
    public void SimulateUserInput() => _user.Cancel();

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _journal.Add("lease:disposed");
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("the bridge failed to take the input back");
        }

        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeHost : IApiHost
{
    private readonly FakeTimeProvider _time;

    public FakeHost(FakeTimeProvider time)
    {
        _time = time;
        TouchSink = new FakeTouchSink(time, Journal);
        KeyboardSink = new FakeKeyboardSink(time, Journal);
        HomeSink = new FakeHomeSink(Journal);
    }

    public SyncList<string> Journal { get; } = [];

    public FakeTouchSink TouchSink { get; }

    public FakeKeyboardSink KeyboardSink { get; }

    public FakeHomeSink HomeSink { get; }

    public bool Running { get; set; } = true;

    public bool ProbeSuspended { get; set; }

    public PictureInfo? Picture { get; set; } = new(390, 844);

    public Rotation Landscape { get; set; } = Rotation.R90;

    public Rotation Portrait { get; set; } = Rotation.R0;

    public DateTimeOffset? LastUserInput { get; set; }

    public bool UserInputActive { get; set; }

    public bool LeaseDisposeThrows { get; set; }

    /// <summary>When set, <see cref="AcquireInputAsync"/> refuses with it (the bridge says the user is active).</summary>
    public InputRefusedException? RefuseInput { get; set; }

    /// <summary>When true, <see cref="AcquireInputAsync"/> never answers (the bridge loop is gone) until its token is cancelled.</summary>
    public bool AcquireBlocks { get; set; }

    public SyncList<FakeLease> Leases { get; } = [];

    public FakeLease? Lease => Leases.Count == 0 ? null : Leases[^1];

    public SyncList<(Rotation? Landscape, Rotation? Portrait)> RotationSets { get; } = [];

    public SyncList<string?> TargetChoices { get; } = [];

    public HashSet<string> KnownDevices { get; } = ["iphone-1"];

    public ITouchSink Touch => TouchSink;

    public IKeyboardSink Keyboard => KeyboardSink;

    public IHomeButtonSink HomeButton => HomeSink;

    public ApiHostSnapshot GetSnapshot() =>
        new(Running, ProbeSuspended, TouchSink.IsSubscribed, KeyboardSink.HasKeyboard, HomeSink.HasHomeButton, Picture, Landscape, Portrait, LastUserInput, UserInputActive);

    public ApiStatus GetStatus() =>
        new("0.0.0-test", 1, Running, "connected", [new ApiDevice("iphone-1", "iPhone", "iPhone15,2", TouchSink.IsSubscribed)], "iphone-1", "TestReceiver", Picture, new RotationInfo((int)Landscape, (int)Portrait), false);

    public ValueTask<IInputLease> AcquireInputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (RefuseInput is { } refusal)
        {
            throw refusal;
        }

        if (AcquireBlocks)
        {
            return new ValueTask<IInputLease>(Block(cancellationToken));
        }

        var lease = new FakeLease(Journal) { ThrowOnDispose = LeaseDisposeThrows };
        Leases.Add(lease);
        Journal.Add("lease:acquired");
        return ValueTask.FromResult<IInputLease>(lease);
    }

    private static async Task<IInputLease> Block(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }

    public void SetRotation(Rotation? landscape, Rotation? portrait)
    {
        RotationSets.Add((landscape, portrait));
        Landscape = landscape ?? Landscape;
        Portrait = portrait ?? Portrait;
    }

    public bool ChooseTarget(string? deviceId)
    {
        if (deviceId is not null && !KnownDevices.Contains(deviceId))
        {
            return false;
        }

        TargetChoices.Add(deviceId);
        return true;
    }

    /// <summary>The user used the mirror just now.</summary>
    public void UserJustActed(TimeSpan ago) => LastUserInput = _time.GetUtcNow() - ago;
}

internal static class FakeTimeExtensions
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(1);

    /// <summary>Advances the fake clock in 1 ms steps until the operation completes.</summary>
    public static async Task<T> Drive<T>(this FakeTimeProvider time, ValueTask<T> operation)
    {
        var task = operation.AsTask();
        await DriveUntilDone(time, task);
        return await task;
    }

    /// <inheritdoc cref="Drive{T}(FakeTimeProvider, ValueTask{T})"/>
    public static Task Drive(this FakeTimeProvider time, ValueTask operation) => DriveUntilDone(time, operation.AsTask());

    /// <summary>Advances the clock by <paramref name="milliseconds"/> in 1 ms steps (the code under test reacts at each step).</summary>
    public static void Wait(this FakeTimeProvider time, int milliseconds)
    {
        for (var i = 0; i < milliseconds; i++)
        {
            AdvanceInline(time, Step);
        }
    }

    /// <summary>
    /// Advances the clock with no SynchronizationContext. xUnit installs one on the test thread, and continuations
    /// completed under a non-default context are queued to the thread pool instead of running inline, which would
    /// let the fake clock run ahead of the code under test.
    /// </summary>
    public static void AdvanceInline(this FakeTimeProvider time, TimeSpan delta)
    {
        var saved = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            time.Advance(delta);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(saved);
        }
    }

    private static async Task DriveUntilDone(FakeTimeProvider time, Task task)
    {
        for (var i = 0; !task.IsCompleted; i++)
        {
            if (i > 200_000)
            {
                throw new TimeoutException("operation did not finish within 200 s of fake time");
            }

            AdvanceInline(time, Step);
        }

        await task;
    }
}
