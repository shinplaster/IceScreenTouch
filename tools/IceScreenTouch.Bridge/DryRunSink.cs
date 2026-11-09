// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Globalization;
using IceScreenTouch.Core;

namespace IceScreenTouch.Bridge;

/// <summary>Stands in for the BLE peripheral: always "subscribed", counts reports, optionally prints them.</summary>
internal sealed class DryRunSink(Action<string>? print) : ITouchSink
{
    private long _sent;

    public bool IsSubscribed => true;

    public long Sent => Interlocked.Read(ref _sent);

    public ValueTask SendAsync(ReadOnlyMemory<TouchContact> contacts, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _sent);
        print?.Invoke("[dry ] " + Describe(contacts.Span));
        return ValueTask.CompletedTask;
    }

    internal static string Describe(ReadOnlySpan<TouchContact> contacts)
    {
        if (contacts.IsEmpty)
        {
            return "(no contacts)";
        }

        var parts = new string[contacts.Length];
        for (var i = 0; i < contacts.Length; i++)
        {
            var c = contacts[i];
            parts[i] = string.Create(CultureInfo.InvariantCulture, $"#{c.Id} {(c.Tip ? "down" : "up  ")} ({c.X,5},{c.Y,5})");
        }

        return string.Join("  ", parts);
    }
}
