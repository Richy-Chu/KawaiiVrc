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
