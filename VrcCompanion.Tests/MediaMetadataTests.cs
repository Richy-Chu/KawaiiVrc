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

    private static InstanceHistoryEntry Session(
        string id,
        string worldId,
        string worldName,
        int startHour,
        int endHour)
    {
        var session = new InstanceHistoryEntry
        {
            Id = id,
            AccountUserId = AccountId,
            Location = worldId + ":12345",
            WorldId = worldId,
            InstanceId = "12345",
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
