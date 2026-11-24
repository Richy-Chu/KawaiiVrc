using System;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The API caches used to keep every response for the process lifetime, so a session that
/// browsed friends, worlds and groups grew all day. The cap is the rule, and the order it evicts
/// by is a read: these cases decide both without an API client.
/// </summary>
public sealed class BoundedCacheTests
{
    [Fact]
    public void TheLeastRecentlyReadEntryGoesAtTheCap()
    {
        var midnight = new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
        var cache = new BoundedCache<string, int>(limit: 2);
        cache["old"] = new CacheEntry<int>(1, midnight.AddMinutes(-30));
        cache["newer"] = new CacheEntry<int>(2, midnight.AddMinutes(-10));

        // Reading the older entry is what makes the other one the least recently used.
        Assert.True(cache.TryGetValue("old", out _));

        cache["latest"] = new CacheEntry<int>(3, midnight);

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGetValue("old", out var kept));
        Assert.Equal(1, kept.Value);
        Assert.True(cache.TryGetValue("latest", out _));
        Assert.False(
            cache.TryGetValue("newer", out _),
            "The entry nobody read any more is the one the cap takes.");
    }

    [Fact]
    public void RemoveAndClearForgetEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var cache = new BoundedCache<string, int>(limit: 4);
        cache["a"] = new CacheEntry<int>(1, now);
        cache["b"] = new CacheEntry<int>(2, now);

        Assert.True(cache.TryRemove("a", out var removed));
        Assert.Equal(1, removed.Value);
        Assert.False(cache.TryGetValue("a", out _));

        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGetValue("b", out _));
    }

    [Fact]
    public void AMissingKeyIsAMiss()
    {
        var cache = new BoundedCache<string, int>(limit: 2);

        Assert.False(cache.TryGetValue("nobody", out _));
        Assert.Equal(0, cache.Count);
    }
}
