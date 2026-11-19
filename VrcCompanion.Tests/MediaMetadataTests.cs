using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class MediaMetadataTests
{
    private const string AccountId = "usr_00000000-0000-0000-0000-000000000000";
    private const string JapanId = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b";
    private const string ClubId = "wrld_11111111-2222-3333-4444-555555555555";

    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    private const string GroupId = "grp_9f1c2b3d-4e5f-4a6b-8c7d-0e1f2a3b4c5d";

    private static InstanceHistoryEntry Session(
        string id,
        string worldId,
        string worldName,
        int startHour,
        int endHour,
        string instance = "12345")
    {
        var session = new InstanceHistoryEntry
        {
            Id = id,
            AccountUserId = AccountId,
            Location = worldId + ":" + instance,
            WorldId = worldId,
            InstanceId = instance,
            WorldName = worldName,
            WorldThumbnailUrl = "https://example.test/" + worldId + ".png",
            WorldAuthorName = "Creator",
            JoinedAt = Local(2025, 6, 2, startHour, 0),
            LeftAt = Local(2025, 6, 2, endHour, 0),
            LastSeenAt = Local(2025, 6, 2, endHour, 0)
        };
        session.GetOrAddPlayer(AccountId, "Me");
        session.GetOrAddPlayer("usr_a", "Alice", "https://example.test/alice.png");
        session.GetOrAddPlayer(string.Empty, "Bob");
        return session;
    }

    [Fact]
    public void ContextBecomesAHistoryRowForTheSessionPopup()
    {
        var session = Session("s1", JapanId, "Japan Street", 10, 12);
        var context = MediaMetadataResolver.FindContext(Picture("row.png", 11, 0).TakenAt, new[] { session }, AccountId)!;

        var entry = context.ToHistoryEntry();

        Assert.Equal(context.Location, entry.Location);
        Assert.Equal(JapanId, entry.WorldId);
        Assert.Equal("12345", entry.InstanceId);
        Assert.Equal(context.JoinedAt, entry.JoinedAt);
        Assert.Equal(new[] { "Me", "Alice", "Bob" }, entry.Players.Select(player => player.DisplayName));
        Assert.True(context.CanOpenSessionView);
    }

    [Fact]
    public void SessionListOnlyKeepsPlayersWhoWereThereForThePicture()
    {
        var session = Session("s1", JapanId, "Japan Street", 20, 23);
        session.Players.Single(player => player.DisplayName == "Alice").Visits.Add(new InstanceHistoryVisit
        {
            JoinedAt = Local(2025, 6, 2, 20, 0),
            LeftAt = Local(2025, 6, 2, 20, 30),
            LastSeenAt = Local(2025, 6, 2, 20, 30)
        });
        session.Players.Single(player => player.DisplayName == "Bob").Visits.Add(new InstanceHistoryVisit
        {
            JoinedAt = Local(2025, 6, 2, 20, 50),
            LastSeenAt = Local(2025, 6, 2, 22, 59)
        });

        var late = MediaMetadataResolver.FindContext(Picture("late.png", 21, 0).TakenAt, new[] { session }, AccountId);
        var early = MediaMetadataResolver.FindContext(Picture("early.png", 20, 10).TakenAt, new[] { session }, AccountId);

        Assert.Equal(new[] { "Me", "Bob" }, late!.People.Select(person => person.DisplayName));
        Assert.Equal(new[] { "Me", "Alice" }, early!.People.Select(person => person.DisplayName));
    }

    private static MediaItem Picture(string name, int hour, int minute)
        => new(
            Path.Combine(Path.GetTempPath(), "VrcMediaMeta", name),
            "VRChat",
            new DateTime(2025, 6, 2, hour, minute, 0));

    [Fact]
    public void PictureInsideASessionGetsItsWorldAndPlayers()
    {
        var sessions = new List<InstanceHistoryEntry>
        {
            Session("s1", JapanId, "Japan Street", 10, 12),
            Session("s2", ClubId, "Night Club", 14, 16)
        };
        var inside = Picture("a.png", 11, 30);
        var outside = Picture("b.png", 13, 30);
        var later = Picture("c.png", 15, 0);

        var contexts = MediaMetadataResolver.Build(new[] { inside, outside, later }, sessions, AccountId);

        Assert.True(contexts.TryGetValue(inside.Path, out var first));
        Assert.Equal("Japan Street", first!.WorldName);
        Assert.Equal(JapanId, first.WorldId);
        Assert.True(first.HasSession);
        Assert.Contains(first.People, person => person is { UserId: "usr_a", DisplayName: "Alice", HasAvatar: true });
        Assert.Contains(first.People, person => person.DisplayName == "Bob" && !person.HasUserId);
        Assert.Contains(first.People, person => person.IsSelf && person.UserId == AccountId);

        Assert.False(contexts.ContainsKey(outside.Path));
        Assert.Equal("Night Club", contexts[later.Path].WorldName);
    }

    [Fact]
    public void GroupInstanceNamesTheGroupAndItsAccess()
    {
        var session = Session(
            "s1",
            JapanId,
            "Japan Street",
            10,
            12,
            $"12345~group({GroupId})~groupAccessType(public)");

        var context = MediaMetadataResolver.Describe(
            session,
            AccountId,
            groupId => groupId == GroupId ? ("Kawaii Club", "https://example.test/group.png") : default);

        Assert.Equal("Group public", context.AccessLabel);
        Assert.True(context.HasGroup);
        Assert.True(context.CanOpenGroup);
        Assert.Equal(GroupId, context.GroupId);
        Assert.Equal("Kawaii Club", context.GroupDisplayName);
        Assert.Equal("https://example.test/group.png", context.GroupIconUrl);
    }

    [Fact]
    public void GroupPlusInstanceIsStillAGroup()
    {
        var session = Session(
            "s1",
            JapanId,
            "Japan Street",
            10,
            12,
            $"12345~group({GroupId})~groupAccessType(plus)");

        var context = MediaMetadataResolver.Describe(session, AccountId);

        Assert.Equal("Group+", context.AccessLabel);
        Assert.True(context.CanOpenGroup);
        // Without a lookup the group row still exists, just without its name yet.
        Assert.Equal("Group instance", context.GroupDisplayName);

        context.ApplyGroupInfo("Kawaii Club", "https://example.test/group.png");
        Assert.Equal("Kawaii Club", context.GroupDisplayName);
    }

    [Fact]
    public void PublicInstanceStaysPublic()
    {
        var context = MediaMetadataResolver.Describe(
            Session("s1", JapanId, "Japan Street", 10, 12),
            AccountId);

        Assert.Equal("Public", context.AccessLabel);
        Assert.False(context.HasGroup);
        Assert.False(context.CanOpenGroup);
        Assert.Equal(string.Empty, context.GroupDisplayName);
    }

    [Fact]
    public void ContextTextDescribesTheSession()
    {
        var context = MediaMetadataResolver.Describe(
            Session("s1", JapanId, "Japan Street", 10, 12),
            AccountId);

        Assert.Equal("Japan Street", context.WorldNameText);
        Assert.Equal("by Creator", context.WorldAuthorText);
        Assert.Equal("2h", context.DurationText);
        Assert.StartsWith("Session ·", context.SessionText);
        Assert.Equal("3 players recorded", context.PeopleCountText);
    }

    [Fact]
    public async Task MediaFiltersListWorldsAndPeopleAndNarrowTheGrid()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N") + ".json");
        var inside = Picture("a.png", 10, 30);
        var insideToo = Picture("b.png", 11, 15);
        var outside = Picture("c.png", 18, 0);
        var groups = new[]
        {
            new MediaGroup("VRChat · June 2025", "VRChat", new DateTime(2025, 6, 1), new[] { inside, insideToo, outside })
        };
        var sessions = new[] { Session("s1", JapanId, "Japan Street", 10, 12) };

        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => groups,
            cachePath: cachePath)
        {
            HistoryProvider = () => sessions
        };

        await viewModel.LoadAsync(force: true);

        Assert.Equal(3, viewModel.PictureCount);
        var world = Assert.Single(viewModel.WorldFilters);
        Assert.Equal("Japan Street", world.Label);
        Assert.Equal(2, world.Count);
        Assert.Contains(viewModel.PeopleFilters, option => option.Label == "Alice");
        Assert.Contains(viewModel.PeopleFilters, option => option.Label == "Bob");

        viewModel.ToggleWorldFilterCommand.Execute(world);
        Assert.True(viewModel.HasActiveFilter);
        Assert.Equal(2, viewModel.PictureCount);
        Assert.True(world.IsSelected);

        viewModel.ToggleWorldFilterCommand.Execute(world);
        Assert.False(viewModel.HasActiveFilter);
        Assert.Equal(3, viewModel.PictureCount);

        var alice = viewModel.PeopleFilters.Single(option => option.Label == "Alice");
        viewModel.TogglePersonFilterCommand.Execute(alice);
        Assert.Equal(2, viewModel.PictureCount);

        viewModel.SearchText = "nothing matches this";
        Assert.Equal(0, viewModel.PictureCount);
        Assert.True(viewModel.IsFilteredEmpty);

        viewModel.ClearFiltersCommand.Execute(null);
        Assert.Equal(3, viewModel.PictureCount);
        Assert.False(viewModel.HasActiveFilter);
        Assert.Equal(string.Empty, viewModel.SearchText);
    }

    [Fact]
    public async Task DropdownsFilterByWorldAndPerson()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N") + ".json");
        var inside = Picture("a.png", 10, 30);
        var insideToo = Picture("b.png", 11, 15);
        var outside = Picture("c.png", 18, 0);
        var groups = new[]
        {
            new MediaGroup("VRChat · June 2025", "VRChat", new DateTime(2025, 6, 1), new[] { inside, insideToo, outside })
        };
        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => groups,
            cachePath: cachePath)
        {
            HistoryProvider = () => new[] { Session("s1", JapanId, "Japan Street", 10, 12) }
        };

        await viewModel.LoadAsync(force: true);

        Assert.Equal("All worlds", viewModel.WorldFilterOptions[0].Label);
        Assert.Equal("All people", viewModel.PeopleFilterOptions[0].Label);
        Assert.Same(viewModel.WorldFilterOptions[0], viewModel.SelectedWorldFilter);
        Assert.Same(viewModel.PeopleFilterOptions[0], viewModel.SelectedPeopleFilter);

        var japan = viewModel.WorldFilterOptions.Single(option => option.Label == "Japan Street");
        viewModel.SelectedWorldFilter = japan;
        Assert.True(japan.IsSelected);
        Assert.Equal(2, viewModel.PictureCount);

        // A person pick replaces the world pick, like the gallery filters.
        var alice = viewModel.PeopleFilterOptions.Single(option => option.Label == "Alice");
        viewModel.SelectedPeopleFilter = alice;
        Assert.Same(viewModel.WorldFilterOptions[0], viewModel.SelectedWorldFilter);
        Assert.False(japan.IsSelected);
        Assert.True(alice.IsSelected);
        Assert.Equal(2, viewModel.PictureCount);

        viewModel.SelectedPeopleFilter = viewModel.PeopleFilterOptions[0];
        Assert.Equal(3, viewModel.PictureCount);
        Assert.False(alice.IsSelected);

        viewModel.SelectedWorldFilter = japan;
        viewModel.SelectedWorldFilter = viewModel.WorldFilterOptions[0];
        Assert.Equal(3, viewModel.PictureCount);
        Assert.False(japan.IsSelected);
    }

    [Fact]
    public async Task ViewerShowsWorldSessionAndPeopleForAPicture()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N") + ".json");
        var picture = Picture("a.png", 10, 30);
        var groups = new[]
        {
            new MediaGroup("VRChat · June 2025", "VRChat", new DateTime(2025, 6, 1), new[] { picture })
        };
        var sessions = new[] { Session("s1", JapanId, "Japan Street", 10, 12) };
        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => groups,
            cachePath: cachePath)
        {
            HistoryProvider = () => sessions
        };

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(viewModel.Groups[0].Items[0]);

        Assert.True(viewModel.HasViewerContext);
        Assert.False(viewModel.ViewerShowsNoContext);
        Assert.Equal("Japan Street", viewModel.ViewerWorldName);
        Assert.Equal("by Creator", viewModel.ViewerWorldAuthorText);
        Assert.True(viewModel.HasViewerWorldThumbnail);
        Assert.True(viewModel.CanOpenViewerWorld);
        Assert.True(viewModel.CanOpenViewerSession);
        Assert.True(viewModel.HasViewerPeople);
        Assert.False(viewModel.ViewerShowsNoPlayers);
        Assert.StartsWith("Session ·", viewModel.ViewerSessionText);
    }

    [Fact]
    public async Task ViewerFillsTheMissingWorldAndPlayerIconsFromTheApi()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N") + ".json");
        var picture = Picture("a.png", 10, 30);
        var groups = new[]
        {
            new MediaGroup("VRChat · June 2025", "VRChat", new DateTime(2025, 6, 1), new[] { picture })
        };

        // The log only stores the world id and player names, like a real row.
        var session = Session("s1", JapanId, string.Empty, 10, 12);
        session.WorldThumbnailUrl = string.Empty;
        session.WorldAuthorName = string.Empty;
        foreach (var player in session.Players)
        {
            player.AvatarUrl = string.Empty;
        }

        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => groups,
            cachePath: cachePath)
        {
            HistoryProvider = () => new[] { session },
            WorldFetcher = worldId => Task.FromResult<MediaWorldInfo?>(
                worldId == JapanId
                    ? new MediaWorldInfo("Japan Street", "https://example.test/world.png", "Creator")
                    : null),
            PersonFetcher = userId => Task.FromResult<MediaPersonInfo?>(
                userId == "usr_a" ? new MediaPersonInfo("Alice", "https://example.test/alice.png") : null)
        };

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(viewModel.Groups[0].Items[0]);

        await viewModel.ViewerHydration;

        Assert.Equal("Japan Street", viewModel.ViewerWorldName);
        Assert.Equal("by Creator", viewModel.ViewerWorldAuthorText);
        Assert.Equal("https://example.test/world.png", viewModel.ViewerWorldThumbnailUrl);
        Assert.True(viewModel.HasViewerWorldThumbnail);

        var alice = viewModel.ViewerPeople.Single(person => person.UserId == "usr_a");
        Assert.True(alice.HasAvatar);
        Assert.Equal("https://example.test/alice.png", alice.AvatarUrl);
    }

    [Fact]
    public async Task ViewerExplainsWhenAPictureHasNoRecordedSession()
    {
        var cachePath = Path.Combine(Path.GetTempPath(), "VrcMediaMeta", Guid.NewGuid().ToString("N") + ".json");
        var picture = Picture("old.png", 9, 0);
        var groups = new[]
        {
            new MediaGroup("VRChat · June 2025", "VRChat", new DateTime(2025, 6, 1), new[] { picture })
        };
        var viewModel = new MediaLibraryViewModel(
            "C:\\pictures",
            (_, _) => groups,
            cachePath: cachePath)
        {
            HistoryProvider = () => Array.Empty<InstanceHistoryEntry>()
        };

        await viewModel.LoadAsync(force: true);
        viewModel.OpenViewer(viewModel.Groups[0].Items[0]);

        Assert.False(viewModel.HasViewerContext);
        Assert.True(viewModel.ViewerShowsNoContext);
        Assert.False(viewModel.CanOpenViewerWorld);
        Assert.False(viewModel.CanOpenViewerSession);
        Assert.False(viewModel.HasViewerPeople);
        Assert.False(viewModel.ViewerShowsNoPlayers);
        Assert.Contains("no recorded session", viewModel.ViewerContextNote);
    }
}
