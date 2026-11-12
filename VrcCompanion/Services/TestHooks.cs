using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Services;

/// <summary>
/// The one place tests reach into production seams: the fake community HTTP
/// handler, the browse fetch (offset in, rows out) and the search cache. Nothing
/// here is used at runtime; every member keeps the production default when unset.
/// </summary>
internal static class TestHooks
{
    public static void UseCommunityHandler(HttpMessageHandler? handler)
        => VrcApiClient.SetCommunityHandler(handler);

    public static void UseBrowseFetch(
        AvatarViewModel viewModel,
        Func<int, CancellationToken, Task<List<Avatar>>>? fetch)
        => viewModel.BrowseFetch = fetch;

    public static void SeedSearchCache(AvatarViewModel viewModel, AvatarViewModel.CachedSearch cached)
        => viewModel.SeedSearchCache(cached);

    /// <summary>
    /// Clock behind the browse backoff. Tests move it forward instead of waiting,
    /// and must restore it with <see cref="ResetClock"/>.
    /// </summary>
    public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public static void ResetClock() => UtcNow = () => DateTime.UtcNow;

    /// <summary>Runs one cooldown tick; the dispatcher timer does not tick under xUnit.</summary>
    public static void PumpBrowseBackoff(AvatarViewModel viewModel)
        => viewModel.OnBrowseBackoffTick();
}
