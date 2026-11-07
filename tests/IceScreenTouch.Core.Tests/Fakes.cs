using IceScreenTouch.Core;
using Microsoft.Extensions.Time.Testing;

namespace IceScreenTouch.Core.Tests;

/// <summary>One report as the sink received it.</summary>
internal sealed record Frame(TimeSpan At, TouchContact[] Contacts)
{
    public bool IsEmpty => Contacts.Length == 0;

    public bool IsDown => Contacts.Length > 0 && Contacts.All(c => c.Tip);

    public bool IsLift => Contacts.Length > 0 && Contacts.All(c => !c.Tip);

    public AxisPoint First => new(Contacts[0].X, Contacts[0].Y);
}

/// <summary>Records every report with the fake time at which the send started.</summary>
internal sealed class RecordingSink(FakeTimeProvider time) : ITouchSink
{
    private readonly DateTimeOffset _start = time.GetUtcNow();

    public bool IsSubscribed { get; set; } = true;

    /// <summary>Simulates a slow BLE link: each send blocks the caller this long.</summary>
    public TimeSpan SendDelay { get; set; }

    public List<Frame> Frames { get; } = [];

    public IEnumerable<Frame> Downs => Frames.Where(f => f.IsDown);

    public async ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
    {
        Frames.Add(new Frame(time.GetUtcNow() - _start, contacts.ToArray()));
        if (SendDelay > TimeSpan.Zero)
        {
            await Task.Delay(SendDelay, time, cancellationToken).ConfigureAwait(false);
        }
    }
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

    public static void Wait(this FakeTimeProvider time, double milliseconds) => AdvanceInline(time, TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>
    /// Advances the clock with no SynchronizationContext. xUnit installs one on the test thread, and
    /// continuations completed under a non-default context are queued to the thread pool instead of
    /// running inline, which would let the fake clock run ahead of the code under test.
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
            if (i > 100_000)
            {
                throw new TimeoutException("operation did not finish within 100 s of fake time");
            }

            AdvanceInline(time, Step);
        }

        await task;
    }
}
