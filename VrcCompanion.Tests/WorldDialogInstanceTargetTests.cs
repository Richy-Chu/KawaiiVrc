using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The world dialog opens for a location, not just for a world. The instance an invite
/// names, the one a friend is in, or the one a profile row points at is pinned first -
/// even when VRChat does not list it - and the create card can host the instance for a
/// group whose role allows it.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class WorldDialogInstanceTargetTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";
    private const string InvitedInstance = "99999~private(usr_friend)~region(us)";
    private const string PublicInstance = "111~region(us)";

    [Fact]
    public async Task TheInstanceTheDialogWasOpenedForIsPinnedFirst()
    {
        using var handler = new WorldHandler();
        using var api = new VrcApiClient(null, handler);
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared,
            $"{WorldId}:{InvitedInstance}",
            "You were invited to this instance.");

        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        await (dialog.GroupHydration ?? Task.CompletedTask);

        Assert.NotNull(dialog.FocusInstance);
        Assert.Same(dialog.FocusInstance, dialog.Instances[0]);
        Assert.Equal(InvitedInstance, dialog.Instances[0].InstanceId);
        Assert.True(dialog.Instances[0].IsFocus);
        Assert.Equal("You were invited to this instance.", dialog.Instances[0].FocusNote);
        Assert.Contains("Invite", dialog.Instances[0].DetailText, StringComparison.Ordinal);
        Assert.Contains("US West", dialog.Instances[0].DetailText, StringComparison.Ordinal);

        // VRChat does not list a private invite, so the pinned row is the only copy.
        Assert.Equal(1, dialog.Instances.Count(item => item.InstanceId == InvitedInstance));
        Assert.Contains(dialog.Instances, item => item.InstanceId == PublicInstance);
    }

    [Fact]
    public async Task AnInstanceVrchatAlreadyListsIsMarkedInsteadOfDuplicated()
    {
        using var handler = new WorldHandler();
        using var api = new VrcApiClient(null, handler);
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared,
            $"{WorldId}:{PublicInstance}",
            "Your friend is in this instance.");

        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);

        var focus = Assert.Single(dialog.Instances, item => item.InstanceId == PublicInstance);
        Assert.True(focus.IsFocus);
        Assert.Same(focus, dialog.FocusInstance);
        Assert.Equal(focus, dialog.Instances[0]);

        // A refresh rebuilds the rows; the pinned one stays pinned and stays single.
        await dialog.LoadAsync(forceRefresh: true, CancellationToken.None);
        Assert.Single(dialog.Instances, item => item.InstanceId == PublicInstance);
        Assert.True(dialog.Instances[0].IsFocus);
    }

    [Fact]
    public async Task OnlyGroupsWhoseRoleAllowsInstancesAreOffered()
    {
        using var handler = new WorldHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);

        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        Settle.Until(
            () => !dialog.IsLoadingOwners && dialog.Owners.Count > 1,
            "the group owners never arrived");

        Assert.Equal("Just you", dialog.Owners[0].Label);
        Assert.Contains(dialog.Owners, owner => owner.Label == "Test Group");
        Assert.Contains(dialog.Owners, owner => owner.Label == "Unknown Permissions Group");
        Assert.DoesNotContain(dialog.Owners, owner => owner.Label == "No Create Permission");
        Assert.DoesNotContain(dialog.Owners, owner => owner.Label == "Empty Permissions Group");
    }

    [Fact]
    public async Task CreatingForAGroupSendsTheGroupAndTheChosenGroupAccess()
    {
        using var handler = new WorldHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);

        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        Settle.Until(
            () => !dialog.IsLoadingOwners && dialog.Owners.Any(owner => owner.IsGroup),
            "the group owners never arrived");

        dialog.SelectedOwner = dialog.Owners.Single(owner => owner.Label == "Test Group");

        // The group role may create Group+ and members-only instances, not public ones.
        Assert.Equal(new[] { "Group+", "Group members" }, dialog.InstanceTypes.Select(type => type.Label));
        Assert.Equal("members", dialog.SelectedType.Value);
        Assert.Equal("Create for Test Group", dialog.CreateButtonText);

        dialog.SelectedType = dialog.InstanceTypes.Single(type => type.Value == "plus");

        var instance = await dialog.CreateInstanceAsync(CancellationToken.None);

        Assert.NotNull(instance);
        Assert.Equal("group", handler.CreatedType);
        Assert.Equal("grp_abc", handler.CreatedOwnerId);
        Assert.Equal("plus", handler.CreatedGroupAccessType);
        Assert.False(handler.CreatedCanRequestInvite);
        Assert.Contains("Group+", dialog.CreatedTitle, StringComparison.Ordinal);
        Assert.Contains("grp_abc", dialog.CreatedInstance!.InstanceId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APersonalInstanceKeepsTheSignedInUserAsOwner()
    {
        using var handler = new WorldHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
        var dialog = new WorldDialogViewModel(
            new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
            api,
            ImageLoader.Shared);

        await dialog.LoadAsync(forceRefresh: false, CancellationToken.None);
        dialog.SelectedType = dialog.InstanceTypes.Single(type => type.Value == "friends");

        await dialog.CreateInstanceAsync(CancellationToken.None);

        Assert.Equal("friends", handler.CreatedType);
        Assert.Equal("usr_me", handler.CreatedOwnerId);
        Assert.Null(handler.CreatedGroupAccessType);
    }

    [Fact]
    public void OpeningAProfileRowLoadsThatWorldAndPinsItsInstance()
    {
        using var handler = new WorldHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.SetLoggedIn(shell);

        var location = $"{WorldId}:{InvitedInstance}";
        Assert.True(shell.OpenWorldDetailsForLocationCommand.CanExecute(location));

        shell.OpenWorldDetailsForLocationCommand.Execute(location);

        Settle.Until(
            () => shell.WorldDialog?.FocusInstance is { } focus &&
                  string.Equals(focus.InstanceId, InvitedInstance, StringComparison.Ordinal),
            "the profile location never opened the world dialog on its instance");

        Assert.Equal(WorldId, shell.WorldDialog!.WorldId);
        Assert.Equal("Popcorn Palace", shell.WorldDialog.Name);
    }

    [Fact]
    public void OpeningWorldDetailsClosesTheProfileItCameFrom()
    {
        using var handler = new WorldHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.SetLoggedIn(shell);

        var location = $"{WorldId}:{InvitedInstance}";
        shell.SelectedFriend = new FriendItem(new VrcUser
        {
            Id = "usr_friend",
            DisplayName = "Tree Bear",
            Location = location
        });

        Assert.True(shell.HasSelectedFriend);

        shell.OpenWorldDetailsForLocationCommand.Execute(location);

        Settle.Until(
            () => shell.WorldDialog?.FocusInstance is not null,
            "the world dialog never opened on the profile's instance");

        // The profile overlay is above the world dialog, so it has to close or the
        // dialog it asked for would be hidden underneath it.
        Assert.False(shell.HasSelectedFriend);
    }

    [Fact]
    public void ALocationWithoutAWorldIsNotOffered()
    {
        using var api = new VrcApiClient(null, new WorldHandler());
        using var shell = new MainViewModel(api);
        TestHooks.SetLoggedIn(shell);

        Assert.False(shell.OpenWorldDetailsForLocationCommand.CanExecute("private"));
        Assert.False(shell.OpenWorldDetailsForLocationCommand.CanExecute(""));
        Assert.False(shell.OpenWorldDetailsForLocationCommand.CanExecute(null));
    }

    private sealed class WorldHandler : HttpMessageHandler
    {
        public string? CreatedType { get; private set; }
        public string? CreatedOwnerId { get; private set; }
        public string? CreatedGroupAccessType { get; private set; }
        public bool CreatedCanRequestInvite { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var method = request.Method;

            if (method == HttpMethod.Post && path.EndsWith("/instances", StringComparison.Ordinal))
            {
                var json = request.Content is null
                    ? "{}"
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                CreatedType = root.GetProperty("type").GetString();
                CreatedOwnerId = root.TryGetProperty("ownerId", out var owner) ? owner.GetString() : null;
                CreatedGroupAccessType = root.TryGetProperty("groupAccessType", out var access)
                    ? access.GetString()
                    : null;
                CreatedCanRequestInvite = root.TryGetProperty("canRequestInvite", out var canRequest) &&
                                          canRequest.GetBoolean();

                var instanceId = string.Equals(CreatedType, "group", StringComparison.Ordinal)
                    ? $"88888~group({CreatedOwnerId})~groupAccessType({CreatedGroupAccessType})~region(us)"
                    : CreatedType == "public"
                        ? "88888~region(us)"
                        : $"88888~friends({CreatedOwnerId})~region(us)";
                return await Json($$"""
                    {
                      "instanceId": "{{instanceId}}",
                      "location": "{{WorldId}}:{{instanceId}}",
                      "worldId": "{{WorldId}}"
                    }
                    """);
            }

            if (method == HttpMethod.Get &&
                path.Contains("/users/", StringComparison.Ordinal) &&
                path.EndsWith("/groups", StringComparison.Ordinal))
            {
                return await Json("""
                    [
                      {
                        "id": "grp_abc",
                        "name": "Test Group",
                        "membershipStatus": "member",
                        "myMember": {
                          "permissions": ["group-instance-plus-create", "group-instance-restricted-create"]
                        }
                      },
                      {
                        "id": "grp_none",
                        "name": "No Create Permission",
                        "membershipStatus": "member",
                        "myMember": { "permissions": ["group-instance-join"] }
                      },
                      {
                        "id": "grp_empty",
                        "name": "Empty Permissions Group",
                        "membershipStatus": "member",
                        "myMember": { "permissions": [] }
                      },
                      {
                        "id": "grp_unknown",
                        "name": "Unknown Permissions Group",
                        "membershipStatus": "member"
                      }
                    ]
                    """);
            }

            if (method == HttpMethod.Get && path.EndsWith($"/worlds/{WorldId}", StringComparison.Ordinal))
            {
                return await Json($$"""
                    {
                      "id": "{{WorldId}}",
                      "name": "Popcorn Palace",
                      "authorName": "spacebaar",
                      "imageUrl": "https://example.test/popcorn.jpg",
                      "slimInstances": [
                        {
                          "instanceId": "{{PublicInstance}}",
                          "location": "{{WorldId}}:{{PublicInstance}}",
                          "userCount": 28,
                          "capacity": 80,
                          "region": "us"
                        }
                      ]
                    }
                    """);
            }

            return await Json("[]");
        }

        private static Task<HttpResponseMessage> Json(string json)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
