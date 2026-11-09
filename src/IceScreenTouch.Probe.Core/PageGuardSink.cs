// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using IceScreenTouch.Core;

namespace IceScreenTouch.Probe;

/// <summary>
/// A touch sink for the probes that puts a finger down only while the probe page is in front
/// (<see cref="ProbeReadiness.InFront"/>). If the phone locked or switched to another app during a probe, the rest of
/// its taps would land on the lock screen's passcode pad or on that app (security review, 2026-11-07): the probe stops
/// instead (<see cref="ProbePageLeftException"/>). Lifts always go through, so no finger is left down.
/// </summary>
public sealed class PageGuardSink(ITouchSink inner, ProbeServer server, ProbePage page, TextWriter? output = null) : ITouchSink
{
    /// <inheritdoc/>
    public bool IsSubscribed => inner.IsSubscribed;

    /// <inheritdoc/>
    public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
    {
        var press = false;
        foreach (var c in contacts.Span)
        {
            press |= c.Tip;
        }

        if (press && !ProbeReadiness.InFront(page, server, ProbeClock.Now()))
        {
            output?.WriteLine("  -> the probe page is no longer in front: stopped");
            throw new ProbePageLeftException();
        }

        return inner.SendAsync(contacts, cancellationToken);
    }
}
