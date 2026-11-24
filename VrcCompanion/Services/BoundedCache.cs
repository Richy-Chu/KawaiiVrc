using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace VrcCompanion.Services;

/// <summary>
/// One cached API response. Freshness is the caller's rule - <see cref="FetchedAt"/> is the
/// moment it arrived - and <see cref="UsedAt"/> is what the cache's cap evicts by.
/// </summary>
internal sealed record CacheEntry<T>(T Value, DateTimeOffset FetchedAt, DateTimeOffset UsedAt)
{
    internal CacheEntry(T value, DateTimeOffset fetchedAt)
        : this(value, fetchedAt, fetchedAt)
    {
    }
}

/// <summary>
/// A read cache with a ceiling on how many responses it keeps. A session browses friends, worlds
/// and groups for hours, and every one of those responses used to stay for the process lifetime;
/// here a read refreshes the entry's place in the order and the least recently read entry goes
/// when the cap is reached, so memory has a bound instead of a direction.
/// </summary>
internal sealed class BoundedCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry<TValue>> _entries;
    private readonly int _limit;

    internal BoundedCache(int limit, IEqualityComparer<TKey>? comparer = null)
    {
        _limit = Math.Max(1, limit);
        _entries = new ConcurrentDictionary<TKey, CacheEntry<TValue>>(
            comparer ?? EqualityComparer<TKey>.Default);
    }

    internal int Count => _entries.Count;

    /// <summary>Reads an entry and marks it as the most recently used one.</summary>
    internal bool TryGetValue(TKey key, out CacheEntry<TValue> entry)
    {
        if (!_entries.TryGetValue(key, out var cached))
        {
            entry = null!;
            return false;
        }

        // Losing this update to another reader is harmless: both of them just read the entry, and
        // either timestamp keeps it ahead of what has not been read.
        _entries.TryUpdate(key, cached with { UsedAt = DateTimeOffset.UtcNow }, cached);
        entry = cached;
        return true;
    }

    internal CacheEntry<TValue> this[TKey key]
    {
        set
        {
            _entries[key] = value;
            Trim();
        }
    }

    internal bool TryRemove(TKey key, out CacheEntry<TValue> entry)
    {
        var removed = _entries.TryRemove(key, out var cached);
        entry = cached!;
        return removed;
    }

    internal void Clear() => _entries.Clear();

    /// <summary>Drops the least recently read entry for as long as the cap is exceeded.</summary>
    private void Trim()
    {
        while (_entries.Count > _limit)
        {
            var found = false;
            var oldestKey = default(TKey)!;
            var oldestUsed = DateTimeOffset.MaxValue;
            foreach (var pair in _entries)
            {
                if (found && pair.Value.UsedAt >= oldestUsed)
                {
                    continue;
                }

                oldestKey = pair.Key;
                oldestUsed = pair.Value.UsedAt;
                found = true;
            }

            if (!found || !_entries.TryRemove(oldestKey, out _))
            {
                return;
            }
        }
    }
}
