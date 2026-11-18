using System.Collections.Concurrent;
using System.Diagnostics;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Shared cache for VRChat profile decorations. The index is global (not tied
/// to one account), and every surface that shows a player can resolve the same
/// iconFrame/profileEffect/nameplateEffect ids without repeating the request.
/// </summary>
public static class CosmeticCatalog
{
    public const string IconFrame = "iconFrame";
    public const string ProfileEffect = "profileEffect";
    public const string NameplateEffect = "nameplateEffect";

    private static readonly string[] Types = { IconFrame, ProfileEffect, NameplateEffect };
    private static readonly Dictionary<string, ConcurrentDictionary<string, VrcCosmetic>> Entries =
        new(StringComparer.Ordinal)
        {
            [IconFrame] = new(StringComparer.Ordinal),
            [ProfileEffect] = new(StringComparer.Ordinal),
            [NameplateEffect] = new(StringComparer.Ordinal)
        };

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly ConcurrentDictionary<string, bool> LoadedTypes = new(StringComparer.Ordinal);
    private static long _retryAfterTicks;

    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(30);

    internal static Func<DateTimeOffset> UtcNow { get; set; } = static () => DateTimeOffset.UtcNow;

    public static bool IsLoaded => Types.All(LoadedTypes.ContainsKey);

    /// <summary>Raised after any decoration index becomes available.</summary>
    internal static event Action? Changed;

    public static async Task EnsureLoadedAsync(
        VrcApiClient api,
        CancellationToken cancellationToken = default)
    {
        if (IsLoaded || UtcNow().UtcDateTime.Ticks < Volatile.Read(ref _retryAfterTicks))
        {
            return;
        }

        var loadedAny = false;
        var failed = false;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (IsLoaded || UtcNow().UtcDateTime.Ticks < Volatile.Read(ref _retryAfterTicks))
            {
                return;
            }

            foreach (var type in Types)
            {
                if (LoadedTypes.ContainsKey(type))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var items = await api.GetCosmeticsAsync(type, cancellationToken);
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrWhiteSpace(item.Id))
                        {
                            Entries[type][item.Id] = item;
                        }
                    }
                    LoadedTypes[type] = true;
                    loadedAny = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (VrcApiException ex) when (ex.StatusCode == 404)
                {
                    // VRChat has no index for a decoration type it no longer
                    // publishes. Treating that as retryable would poll forever.
                    LoadedTypes[type] = true;
                    loadedAny = true;
                }
                catch (Exception ex)
                {
                    // A decoration index is optional decoration data. Never let
                    // it break the friend list or profile popup.
                    Debug.WriteLine($"Could not load VRChat {type} cosmetics: {ex.Message}");
                    failed = true;
                }
            }

            // Retry only the failed indexes, and only after a short cool-down so
            // a transient outage cannot turn every friend refresh into requests.
            Volatile.Write(
                ref _retryAfterTicks,
                failed ? UtcNow().Add(RetryDelay).UtcDateTime.Ticks : 0);
        }
        finally
        {
            Gate.Release();
        }

        if (loadedAny)
        {
            Changed?.Invoke();
        }
    }

    public static VrcCosmetic? Find(string itemType, string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !Entries.TryGetValue(itemType, out var catalog))
        {
            return null;
        }

        return catalog.TryGetValue(id, out var cosmetic) ? cosmetic : null;
    }

    /// <summary>
    /// The primary artwork for a decoration. Prefer the animated asset, then
    /// fall back to base/gallery artwork when a decoration has no animation.
    /// </summary>
    public static string? DisplayUrl(string itemType, string? id)
    {
        var cosmetic = Find(itemType, id);
        if (cosmetic is null)
        {
            return null;
        }

        return AssetUrl(cosmetic, "mainAnimation")
               ?? AssetUrl(cosmetic, "introAnimation")
               ?? AssetUrl(cosmetic, "base")
               ?? cosmetic.Metadata?.ImageUrl
               ?? cosmetic.ImageUrl;
    }

    /// <summary>The one-shot animation VRCX plays before the looping artwork.</summary>
    public static string? IntroUrl(string itemType, string? id)
    {
        var cosmetic = Find(itemType, id);
        return cosmetic is null ? null : AssetUrl(cosmetic, "introAnimation");
    }

    private static string? AssetUrl(VrcCosmetic cosmetic, string assetType)
    {
        return cosmetic.Metadata?.Assets
            .FirstOrDefault(asset => string.Equals(asset.Type, assetType, StringComparison.OrdinalIgnoreCase))
            ?.Url;
    }

    internal static void ResetForTests()
    {
        foreach (var catalog in Entries.Values)
        {
            catalog.Clear();
        }
        LoadedTypes.Clear();
        RetryDelay = TimeSpan.FromSeconds(30);
        UtcNow = static () => DateTimeOffset.UtcNow;
        Volatile.Write(ref _retryAfterTicks, 0);
    }
}
