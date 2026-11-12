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
    /// Attaches an avatar view model to the shell without restoring a session. The
    /// shell keeps its setter private, so this goes through it by reflection and
    /// stays the only test-side way in.
    /// </summary>
    public static void AttachAvatarViewModel(MainViewModel shell, AvatarViewModel avatar)
    {
        var setter = typeof(MainViewModel)
            .GetProperty(nameof(MainViewModel.AvatarViewModel))
            ?.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("MainViewModel.AvatarViewModel setter not found.");

        setter.Invoke(shell, new object?[] { avatar });
    }

    /// <summary>
    /// Clock behind the browse backoff. Tests move it forward instead of waiting,
    /// and must restore it with <see cref="ResetClock"/>.
    /// </summary>
    public static Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    public static void ResetClock() => UtcNow = () => DateTime.UtcNow;

    /// <summary>Observes launch URIs instead of handing them to Windows; null restores.</summary>
    public static void UseUriLauncher(Action<string>? launcher)
        => LaunchService.UriLauncher = launcher ?? LaunchService.DefaultUriLauncher;

    /// <summary>Runs one cooldown tick; the dispatcher timer does not tick under xUnit.</summary>
    public static void PumpBrowseBackoff(AvatarViewModel viewModel)
        => viewModel.OnBrowseBackoffTick();
}
