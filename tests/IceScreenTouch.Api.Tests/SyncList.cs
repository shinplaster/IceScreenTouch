// Copyright (c) 2026 Shin-plaster
// SPDX-License-Identifier: MIT

using System.Collections;

namespace IceScreenTouch.Api.Tests;

/// <summary>
/// A list the server thread can append to while the test thread reads it: every access takes the lock and
/// enumeration walks a snapshot.
/// </summary>
internal sealed class SyncList<T> : IReadOnlyList<T>
{
    private readonly List<T> _items = [];
    private readonly Lock _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public T this[int index]
    {
        get
        {
            lock (_lock)
            {
                return _items[index];
            }
        }
    }

    public void Add(T item)
    {
        lock (_lock)
        {
            _items.Add(item);
        }
    }

    public int IndexOf(T item)
    {
        lock (_lock)
        {
            return _items.IndexOf(item);
        }
    }

    public bool Contains(T item)
    {
        lock (_lock)
        {
            return _items.Contains(item);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _items.Clear();
        }
    }

    public IEnumerator<T> GetEnumerator()
    {
        T[] snapshot;
        lock (_lock)
        {
            snapshot = [.. _items];
        }

        return ((IEnumerable<T>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
