using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A bio check runs every few seconds and used to rewrite the whole feed file,
/// which is tens of megabytes once the timeline has history. The baselines live
/// in their own file so that check stays cheap.
/// </summary>
public sealed class ActivityStoreBiosTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public ActivityStoreBiosTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "VrcCompanion.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "activity.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task ABioCheckDoesNotRewriteTheFeedFile()
    {
        var store = new ActivityStore(_path);
        store.Save(
            "usr_owner",
            Enumerable.Range(0, 500)
                .Select(index => new ActivityEntry
                {
                    Id = "friend-added-usr_" + index,
                    Type = ActivityType.FriendAdded,
                    UserId = "usr_" + index,
                    Details = new string('x', 400)
                })
                .ToList());
        await store.FlushAsync();

        var feed = new FileInfo(_path);
        Assert.True(feed.Exists);
        var feedLength = feed.Length;
        var feedWritten = feed.LastWriteTimeUtc;

        for (var check = 0; check < 5; check++)
        {
            store.SaveBioBaselines(
                "usr_owner",
                new Dictionary<string, BioBaseline>(StringComparer.Ordinal)
                {
                    ["usr_0"] = new BioBaseline
                    {
                        Bio = "a bio that was just read",
                        CheckedAt = DateTimeOffset.UtcNow.AddSeconds(check)
                    }
                });
        }

        await store.FlushAsync();

        feed.Refresh();
        Assert.Equal(feedLength, feed.Length);
        Assert.Equal(feedWritten, feed.LastWriteTimeUtc);

        var bios = new FileInfo(store.BiosFilePath);
        Assert.True(bios.Exists, "The bio baselines were never written to their own file.");
        Assert.True(
            bios.Length < feedLength / 10,
            $"The bio file is {bios.Length:N0} bytes against a {feedLength:N0} byte feed.");
    }

    [Fact]
    public async Task BioBaselinesSurviveARestart()
    {
        var store = new ActivityStore(_path);
        store.SaveBioBaselines(
            "usr_owner",
            new Dictionary<string, BioBaseline>(StringComparer.Ordinal)
            {
                ["usr_a"] = new BioBaseline { Bio = "hello world", CheckedAt = DateTimeOffset.UtcNow }
            });
        await store.FlushAsync();

        var reopened = new ActivityStore(_path);
        var baseline = Assert.Single(reopened.LoadBioBaselines("usr_owner"));
        Assert.Equal("usr_a", baseline.Key);
        Assert.Equal("hello world", baseline.Value.Bio);
    }

    [Fact]
    public async Task BioBaselinesSavedBeforeTheSplitAreStillRead()
    {
        // Reports from older versions carried the baselines inside the feed.
        File.WriteAllText(
            _path,
            """
            {"accounts":{"usr_owner":{"entries":[],"bios":{"usr_legacy":{"bio":"old bio","checkedAt":"2026-01-01T00:00:00+00:00"}}}}}
            """);

        var store = new ActivityStore(_path);
        var baseline = Assert.Single(store.LoadBioBaselines("usr_owner"));
        Assert.Equal("usr_legacy", baseline.Key);
        Assert.Equal("old bio", baseline.Value.Bio);

        // The next check moves them into their own file, and the following feed
        // snapshot stops carrying a second copy.
        store.SaveBioBaselines("usr_owner", store.LoadBioBaselines("usr_owner"));
        await store.FlushAsync();

        var reopened = new ActivityStore(_path);
        Assert.Equal("old bio", Assert.Single(reopened.LoadBioBaselines("usr_owner")).Value.Bio);

        reopened.Save("usr_owner", Array.Empty<ActivityEntry>());
        await reopened.FlushAsync();
        Assert.DoesNotContain("old bio", File.ReadAllText(_path));
        Assert.Equal("old bio", Assert.Single(new ActivityStore(_path).LoadBioBaselines("usr_owner")).Value.Bio);
        Assert.Equal("old bio", Assert.Single(reopened.LoadBioBaselines("usr_owner")).Value.Bio);
    }
}
