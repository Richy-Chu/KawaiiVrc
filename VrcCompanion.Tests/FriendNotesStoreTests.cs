using System;
using System.IO;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class FriendNotesStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vrccompanion-notes-" + Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_directory, name);

    public FriendNotesStoreTests() => Directory.CreateDirectory(_directory);

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

    [Fact]
    public void Set_ThenGet_ReturnsTrimmedNote()
    {
        var store = new FriendNotesStore(PathFor("notes.json"));

        var saved = store.Set("usr_1", "  met at the VRChat New Year party  ");

        Assert.NotNull(saved);
        Assert.Equal("met at the VRChat New Year party", saved!.Text);
        Assert.Equal("usr_1", store.Get("usr_1")!.UserId);
        Assert.Null(store.Get("usr_2"));
        Assert.True(File.Exists(PathFor("notes.json")));
    }

    [Fact]
    public void Notes_SurviveARestart()
    {
        var path = PathFor("notes.json");
        new FriendNotesStore(path).Set("usr_1", "speaks Japanese; likes world hopping");

        var reopened = new FriendNotesStore(path);

        Assert.Equal("speaks Japanese; likes world hopping", reopened.Get("usr_1")!.Text);
    }

    [Fact]
    public void BlankText_RemovesTheNoteAndPersistsTheRemoval()
    {
        var path = PathFor("notes.json");
        var store = new FriendNotesStore(path);
        store.Set("usr_1", "temporary");

        Assert.Null(store.Set("usr_1", "   "));
        Assert.Null(store.Get("usr_1"));
        Assert.Null(new FriendNotesStore(path).Get("usr_1"));
    }

    [Fact]
    public void CorruptFile_LoadsEmptyAndCanBeOverwritten()
    {
        var path = PathFor("notes.json");
        File.WriteAllText(path, "{ not json");

        var store = new FriendNotesStore(path);
        Assert.Null(store.Get("usr_1"));

        store.Set("usr_1", "written after a damaged file");
        Assert.Equal("written after a damaged file", new FriendNotesStore(path).Get("usr_1")!.Text);
    }

    [Fact]
    public void NotesAreKeyedByUserIdAndUpdateInPlace()
    {
        var path = PathFor("notes.json");
        var store = new FriendNotesStore(path);
        var first = store.Set("usr_1", "first");
        store.Set("usr_2", "someone else");
        var second = store.Set("usr_1", "second");

        Assert.True(second!.UpdatedAt >= first!.UpdatedAt);
        Assert.Equal("second", store.Get("usr_1")!.Text);
        Assert.Equal("someone else", store.Get("usr_2")!.Text);

        var reopened = new FriendNotesStore(path);
        Assert.Equal("second", reopened.Get("usr_1")!.Text);
        Assert.Equal("someone else", reopened.Get("usr_2")!.Text);
    }
}
