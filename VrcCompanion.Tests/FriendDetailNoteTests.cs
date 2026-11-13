using System;
using System.IO;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class FriendDetailNoteTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vrccompanion-detail-note-" + Guid.NewGuid().ToString("N"));

    private string NotesPath => Path.Combine(_directory, "friend-notes.json");

    public FriendDetailNoteTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private FriendDetailViewModel CreateProfile(FriendNotesStore store) => new(
        new FriendItem(new VrcUser
        {
            Id = "usr_1",
            DisplayName = "Yuki",
            Username = "yuki"
        }),
        new VrcApiClient(),
        ImageLoader.Shared,
        store);

    [Fact]
    public void JoinAndLeaveUpdateMyGroupsWithoutReopeningTheProfile()
    {
        var profile = CreateProfile(new FriendNotesStore(NotesPath));
        profile.GroupsTab = FriendDetailViewModel.ProfileGroupsTab.Mine;
        var group = new Group { Id = "grp_after_dark", Name = "After Dark" };

        Assert.Empty(profile.MyGroups);
        Assert.Empty(profile.VisibleGroups);

        profile.ApplyGroupMembership(group, isMember: true);
        var joined = Assert.Single(profile.MyGroups);
        Assert.Same(joined, Assert.Single(profile.VisibleGroups));
        Assert.Equal("My groups (1)", profile.MyGroupsTabText);
        Assert.Equal("grp_after_dark", joined.Id);
        Assert.Equal("After Dark", joined.Name);
        Assert.Equal("Joined", joined.MembershipLabel);

        profile.ApplyGroupMembership(group, isMember: false);
        Assert.Empty(profile.MyGroups);
        Assert.Empty(profile.VisibleGroups);
    }

    [Fact]
    public void ProfileShowsTheRepresentedGroupOrNone()
    {
        var profile = CreateProfile(new FriendNotesStore(NotesPath));
        Assert.True(profile.HasNoRepresentedGroup);
        Assert.False(profile.HasRepresentedGroup);

        profile.Profile = new VrcProfile
        {
            RepresentedGroup = new VrcProfileGroup { Id = "grp_represented", Name = "Represented" }
        };

        Assert.True(profile.HasRepresentedGroup);
        Assert.False(profile.HasNoRepresentedGroup);
        Assert.Equal("Represented", profile.RepresentedGroup?.Name);
    }

    [Fact]
    public void RepresentedGroupIsIncludedOnce()
    {
        var profile = new VrcProfile
        {
            RepresentedGroup = new VrcProfileGroup { Id = "grp_represented", Name = "Represented" },
            Groups = new VrcProfileGroups
            {
                Count = 1,
                List = { new VrcProfileGroup { Id = "grp_other", Name = "Other" } }
            }
        };

        Assert.True(FriendDetailViewModel.IncludeRepresentedGroup(profile));
        Assert.Equal(2, profile.Groups.List.Count);
        Assert.Equal("grp_represented", profile.Groups.List[0].Id);
        Assert.Equal(2, profile.Groups.Count);

        Assert.False(FriendDetailViewModel.IncludeRepresentedGroup(profile));
        Assert.Equal(2, profile.Groups.List.Count);
    }

    [Fact]
    public void FullGroupListIsOnlyNeededWhenThePayloadIsShort()
    {
        var shortPayload = new VrcProfile
        {
            Groups = new VrcProfileGroups
            {
                Count = 66,
                List =
                {
                    new VrcProfileGroup { Id = "grp_1", Name = "One" },
                    new VrcProfileGroup { Id = "grp_2", Name = "Two" }
                }
            }
        };
        var completePayload = new VrcProfile
        {
            Groups = new VrcProfileGroups
            {
                Count = 2,
                List =
                {
                    new VrcProfileGroup { Id = "grp_1", Name = "One" },
                    new VrcProfileGroup { Id = "grp_2", Name = "Two" }
                }
            }
        };

        Assert.True(FriendDetailViewModel.NeedsFullGroupList(shortPayload));
        Assert.False(FriendDetailViewModel.NeedsFullGroupList(completePayload));
        Assert.False(FriendDetailViewModel.NeedsFullGroupList(null));
    }

    [Fact]
    public void LeavingTheLastMutualGroupReturnsToTheirGroupsTab()
    {
        var profile = CreateProfile(new FriendNotesStore(NotesPath));
        profile.Profile = new VrcProfile
        {
            Groups = new VrcProfileGroups
            {
                Count = 1,
                List =
                {
                    new VrcProfileGroup { Id = "grp_age_gated", Name = "Age Gated", IsMine = true, IsMutual = true }
                }
            }
        };

        profile.GroupsTab = FriendDetailViewModel.ProfileGroupsTab.Mutual;
        Assert.Single(profile.VisibleGroups);

        profile.ApplyGroupMembership(new Group { Id = "grp_age_gated", Name = "Age Gated" }, isMember: false);

        Assert.Empty(profile.MyGroups);
        Assert.Equal(FriendDetailViewModel.ProfileGroupsTab.Profile, profile.GroupsTab);
        Assert.Single(profile.VisibleGroups);
    }

    [Fact]
    public void OwnProfile_IsMarkedAsSelfAndKeepsTheNoteEditorOutOfThePopup()
    {
        var store = new FriendNotesStore(NotesPath);
        var profile = new FriendDetailViewModel(
            new FriendItem(new VrcUser { Id = "usr_me", DisplayName = "Me", Username = "me" }),
            new VrcApiClient(),
            ImageLoader.Shared,
            store,
            isSelf: true);

        Assert.True(profile.IsSelf);
        Assert.Equal("usr_me", profile.Id);
    }

    [Fact]
    public void OpenGroupsCommand_ShowsTheProfileGroupListUntilClosed()
    {
        var profile = CreateProfile(new FriendNotesStore(NotesPath));

        profile.OpenGroupsCommand.Execute(null);
        Assert.True(profile.IsGroupsOpen);

        profile.CloseGroupsCommand.Execute(null);
        Assert.False(profile.IsGroupsOpen);
    }

    [Fact]
    public void SavePendingNote_PersistsTypedTextWhenTheProfileCloses()
    {
        var store = new FriendNotesStore(NotesPath);
        var profile = CreateProfile(store);

        profile.Note = "met in the midnight rooftop world";
        Assert.True(profile.HasUnsavedNote);

        profile.SavePendingNote();

        Assert.False(profile.HasUnsavedNote);
        Assert.Equal(
            "met in the midnight rooftop world",
            new FriendNotesStore(NotesPath).Get("usr_1")!.Text);
    }

    [Fact]
    public void SavePendingNote_DoesNotWriteWhenNothingWasTyped()
    {
        var store = new FriendNotesStore(NotesPath);
        var profile = CreateProfile(store);

        profile.Note = "   ";
        Assert.False(profile.HasUnsavedNote);

        profile.SavePendingNote();

        Assert.Null(store.Get("usr_1"));
        Assert.False(File.Exists(NotesPath));
    }

    [Fact]
    public void ReopeningTakesTheSavedDraftInsteadOfTheOldNote()
    {
        var store = new FriendNotesStore(NotesPath);
        var profile = CreateProfile(store);
        profile.Note = "first draft";
        profile.SavePendingNote();

        var reopened = CreateProfile(new FriendNotesStore(NotesPath));
        reopened.Note = "edited while open";
        reopened.SavePendingNote();

        Assert.Equal("edited while open", new FriendNotesStore(NotesPath).Get("usr_1")!.Text);
    }
}
