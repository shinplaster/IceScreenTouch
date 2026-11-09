// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

namespace IceScreenTouch.Api;

/// <summary>
/// Logs the requests the server rejects (unauthorized, forbidden origin, invalid request) without letting a hostile
/// local loop flood the log: the first rejection of a category is logged at once, further ones within
/// <see cref="Interval"/> are only counted and reported by the next line of that category (or by
/// <see cref="Flush"/>). A line carries the category and a count, never a token, header, body or address.
/// </summary>
internal sealed class RejectionLog(Action<string>? log, TimeProvider time)
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Category> _categories = [];

    public void Record(string category)
    {
        if (log is null)
        {
            return;
        }

        string? line = null;
        lock (_gate)
        {
            var now = time.GetTimestamp();
            if (!_categories.TryGetValue(category, out var state))
            {
                state = new Category();
                _categories[category] = state;
            }

            state.Pending++;
            if (state.LastLogged is null || time.GetElapsedTime(state.LastLogged.Value, now) >= Interval)
            {
                line = Format(category, state.Pending);
                state.Pending = 0;
                state.LastLogged = now;
            }
        }

        if (line is not null)
        {
            log(line);
        }
    }

    /// <summary>Reports the counts that were held back (when the server stops).</summary>
    public void Flush()
    {
        if (log is null)
        {
            return;
        }

        List<string> lines = [];
        lock (_gate)
        {
            foreach (var (category, state) in _categories)
            {
                if (state.Pending > 0)
                {
                    lines.Add(Format(category, state.Pending));
                    state.Pending = 0;
                }
            }
        }

        foreach (var line in lines)
        {
            log(line);
        }
    }

    private static string Format(string category, int count) =>
        count == 1 ? $"[api] {category}" : $"[api] {category} (x{count})";

    private sealed class Category
    {
        public int Pending { get; set; }

        public long? LastLogged { get; set; }
    }
}
