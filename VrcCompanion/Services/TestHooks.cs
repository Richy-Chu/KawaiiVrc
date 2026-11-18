using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Controls;
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

    public static void UseLoggedAvatar(
        string userId,
        string displayName,
        string avatarName,
        string? authorName = null)
        => VrChatLogAvatarService.SetForTests(userId, displayName, avatarName, authorName);

    public static void ClearLoggedAvatars()
        => VrChatLogAvatarService.ClearForTests();

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
        => AttachViewModel(shell, nameof(MainViewModel.AvatarViewModel), avatar);

    /// <summary>Attaches the Activity feed so self-avatar updates can be asserted.</summary>
    public static void AttachActivityFeed(MainViewModel shell, ActivityFeedViewModel feed)
        => AttachViewModel(shell, nameof(MainViewModel.ActivityFeedViewModel), feed);

    /// <summary>Raises the event a successful avatar wear publishes.</summary>
    public static void PublishAvatarWorn(AvatarViewModel viewModel, Avatar avatar)
    {
        var field = typeof(AvatarViewModel).GetField(
            "AvatarWorn",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        (field?.GetValue(viewModel) as Action<Avatar>)?.Invoke(avatar);
    }

    /// <summary>Seeds the self baseline used by the VRChat-log avatar observer.</summary>
    public static void SeedLoggedAvatarBaseline(
        MainViewModel shell,
        string userId,
        string avatarName)
    {
        var field = typeof(MainViewModel).GetField(
            "_lastLoggedAvatars",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel._lastLoggedAvatars not found.");
        var map = (Dictionary<string, string>)field.GetValue(shell)!;
        map[userId] = avatarName;
    }

    /// <summary>Feeds a parsed log snapshot through the self-avatar observer.</summary>
    public static void ApplyLoggedAvatarChanges(MainViewModel shell, VrChatLogSnapshot snapshot)
    {
        var method = typeof(MainViewModel).GetMethod(
            "ApplyLoggedAvatarChanges",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel.ApplyLoggedAvatarChanges not found.");
        method.Invoke(shell, new object[] { snapshot });
    }

    /// <summary>
    /// Attaches a group view model to the shell through its private setter so the
    /// shell subscribes to membership changes exactly as it does after sign-in.
    /// </summary>
    public static void AttachGroupViewModel(MainViewModel shell, GroupViewModel groups)
        => AttachViewModel(shell, nameof(MainViewModel.GroupViewModel), groups);

    /// <summary>
    /// Attaches a history view model to the shell without restoring a session.
    /// The shell keeps its setter private, so this goes through it by reflection.
    /// </summary>
    public static void AttachInstanceHistoryViewModel(
        MainViewModel shell,
        InstanceHistoryViewModel history)
        => AttachViewModel(shell, nameof(MainViewModel.InstanceHistoryViewModel), history);

    private static void AttachViewModel(MainViewModel shell, string propertyName, object? value)
    {
        var setter = typeof(MainViewModel)
            .GetProperty(propertyName)
            ?.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException($"MainViewModel.{propertyName} setter not found.");

        setter.Invoke(shell, new[] { value });
    }

    /// <summary>Sets the signed-in user on an API client for request-level tests.</summary>
    public static void SetCurrentUser(VrcApiClient api, VrcUser user)
    {
        var setter = typeof(VrcApiClient)
            .GetProperty(nameof(VrcApiClient.CurrentUser))
            ?.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("VrcApiClient.CurrentUser setter not found.");

        setter.Invoke(api, new object?[] { user });
    }

    /// <summary>Sets the signed-in user on the shell for command-level tests.</summary>
    public static void SetCurrentUser(MainViewModel shell, VrcUser user)
        => AttachViewModel(shell, nameof(MainViewModel.CurrentUser), user);

    /// <summary>Marks the shell as signed in so command guards run in tests.</summary>
    public static void SetLoggedIn(MainViewModel shell)
    {
        var setter = typeof(MainViewModel)
            .GetProperty(nameof(MainViewModel.IsLoggedIn))
            ?.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("MainViewModel.IsLoggedIn setter not found.");

        setter.Invoke(shell, new object?[] { true });
    }

    /// <summary>Supplies an isolated invite-message memory file for shell-level tests.</summary>
    public static void UseInviteMessageMemory(MainViewModel shell, InviteMessageMemoryStore store)
    {
        var field = typeof(MainViewModel).GetField(
            "_inviteMessageMemory",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel._inviteMessageMemory not found.");
        field.SetValue(shell, store);
    }

    /// <summary>Supplies a deterministic VRChat log reader for shell-level tests.</summary>
    public static void UseLogPlayer(MainViewModel shell, VrChatLogPlayerService logPlayer)
    {
        var field = typeof(MainViewModel).GetField(
            "_logPlayerService",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel._logPlayerService not found.");
        field.SetValue(shell, logPlayer);
    }

    /// <summary>Runs the profile avatar loader without going through the full profile request.</summary>
    public static Task LoadAvatarDetails(FriendDetailViewModel profile)
    {
        var method = typeof(FriendDetailViewModel)
            .GetMethod(
                "LoadAvatarDetailsAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("FriendDetailViewModel.LoadAvatarDetailsAsync not found.");
        return (Task)method.Invoke(profile, new object[] { CancellationToken.None })!;
    }

    /// <summary>Attaches an open friend profile to the shell through its private setter.</summary>
    public static void AttachFriendDetail(MainViewModel shell, FriendDetailViewModel profile)
    {
        var setter = typeof(MainViewModel)
            .GetProperty(nameof(MainViewModel.SelectedFriendDetail))
            ?.GetSetMethod(nonPublic: true)
            ?? throw new InvalidOperationException("MainViewModel.SelectedFriendDetail setter not found.");

        setter.Invoke(shell, new object?[] { profile });
    }

    /// <summary>Attaches a notification detail without triggering its network load.</summary>
    public static void AttachNotificationDetail(
        MainViewModel shell,
        NotificationDetailViewModel detail)
        => AttachViewModel(shell, nameof(MainViewModel.NotificationDetail), detail);

    /// <summary>Feeds the notification refresh path without a network request.</summary>
    public static void UpdateNotifications(
        MainViewModel shell,
        IReadOnlyList<VrcNotification> notifications)
    {
        var method = typeof(MainViewModel)
            .GetMethod("UpdateNotifications", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel.UpdateNotifications not found.");
        method.Invoke(shell, new object[] { notifications });
    }

    /// <summary>Exercises the same removal path used after accepting a friend request.</summary>
    public static void ResolveFriendRequest(MainViewModel shell, NotificationItem item)
    {
        var method = typeof(MainViewModel)
            .GetMethod("ResolveFriendRequest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MainViewModel.ResolveFriendRequest not found.");
        method.Invoke(shell, new object[] { item });
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

    /// <summary>Supplies the registered handler path without touching the registry; null restores.</summary>
    public static void UseHandlerPathProvider(Func<string?>? provider)
        => LaunchService.HandlerPathProvider = provider ?? LaunchService.FindHandlerPath;

    /// <summary>Observes direct handler launches instead of starting VRChat; null restores.</summary>
    public static void UseHandlerLauncher(Action<string, string>? launcher)
        => LaunchService.HandlerLauncher = launcher ?? LaunchService.DefaultHandlerLauncher;

    /// <summary>Tests pin whether VRChat is already running; null restores process detection.</summary>
    public static void UseGameRunningProbe(Func<bool>? probe)
        => LaunchService.GameRunningProbe = probe ?? LaunchService.IsGameRunning;

    /// <summary>Tests observe the running-game attach path instead of opening VRChat's pipe.</summary>
    public static void UseRunningGameLauncher(Func<string, CancellationToken, Task<bool>>? launcher)
        => LaunchService.RunningGameLauncher = launcher ?? LaunchService.SendToRunningGameAsync;

    /// <summary>Observes Explorer targets instead of opening them; null restores.</summary>
    public static void UseFolderLauncher(Action<string>? launcher)
        => LaunchService.FolderLauncher = launcher ?? LaunchService.DefaultFolderLauncher;

    /// <summary>Supplies download paths instead of showing the Save As dialog; null restores.</summary>
    public static void UseDownloadPathProvider(Func<string, string?>? provider)
        => EmbeddedBrowser.DownloadPathProvider = provider;

    /// <summary>Runs one cooldown tick; the dispatcher timer does not tick under xUnit.</summary>
    public static void PumpBrowseBackoff(AvatarViewModel viewModel)
        => viewModel.OnBrowseBackoffTick();
}
