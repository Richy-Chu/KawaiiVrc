using System;
using System.IO;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "kawaii-session-" + Guid.NewGuid().ToString("n"));

    private SessionStore Store => new(_directory);

    [Fact]
    public void SavedSessionLoadsBack()
    {
        const string cookie = "auth=abcdef0123456789abcdef0123456789";

        Store.Save(cookie);

        Assert.True(Store.Exists);
        Assert.Equal(cookie, Store.Load());
    }

    [Fact]
    public void AnEmptyCookieNeverReplacesTheSavedSession()
    {
        const string cookie = "auth=keep-me-0123456789";

        Store.Save(cookie);
        Store.Save(string.Empty);
        Store.Save("   ");

        Assert.Equal(cookie, Store.Load());
    }

    [Fact]
    public void SavingAgainReplacesTheSessionAndLeavesNoStagingFile()
    {
        Store.Save("auth=first0123456789");
        Store.Save("auth=second0123456789");

        Assert.Equal("auth=second0123456789", Store.Load());
        Assert.False(File.Exists(Path.Combine(_directory, "session.dat.tmp")));
    }

    [Fact]
    public void AnUnreadableFileIsKeptInsteadOfDeleted()
    {
        Store.Save("auth=something0123456789");
        var path = Path.Combine(_directory, "session.dat");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        Assert.Null(Store.Load());
        // Deleting it here is how an account ends up signed out for good.
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ALeftoverStagingFileDoesNotBreakTheNextSave()
    {
        Store.Save("auth=first0123456789");
        File.WriteAllText(Path.Combine(_directory, "session.dat.tmp"), "junk");

        Store.Save("auth=second0123456789");

        Assert.Equal("auth=second0123456789", Store.Load());
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
