using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Every store stages its snapshot in a .tmp file before renaming it, so a test
/// that clears its data directory while a save is in flight must still be able
/// to delete that directory. The save runs on a worker thread, the way the
/// queued writes and background refreshes that call these stores do in the app.
/// </summary>
public sealed class AtomicFileTeardownTests
{
    private static readonly string Name = new('n', 200);
    private static readonly string WorldName = new('w', 120);
    private static readonly string ThumbnailUrl = "https://example.invalid/" + new string('i', 900) + ".png";
    private static readonly string LongText = new('t', 500_000);

    [Theory]
    [InlineData("ContextSnapshotStore")]
    [InlineData("FriendAvatarStore")]
    [InlineData("FriendNotesStore")]
    [InlineData("FriendSnapshotStore")]
    [InlineData("InviteMessageMemoryStore")]
    [InlineData("MediaIndexCache")]
    [InlineData("MediaPhotoCache")]
    [InlineData("NotificationPersistenceStore")]
    [InlineData("SettingsStore")]
    public void ClearingTheDirectoryDuringTheWriteDoesNotFailTheDelete(string store)
    {
        AssertDeleteDuringWriteSucceeds(store, path => Write(store, path));
    }

    /// <summary>
    /// Runs the store's save while polling for its staging file, then deletes the
    /// directory the moment the snapshot is open. Attempts that finish the write
    /// first are discarded; the test fails if none ever overlapped an open file.
    /// </summary>
    private static void AssertDeleteDuringWriteSucceeds(string store, Action<string> write)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "VrcCompanionTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "store.json");
            var temporary = path + ".tmp";

            var writing = Task.Run(() =>
            {
                try
                {
                    write(path);
                }
                catch (IOException)
                {
                    // The delete won the race, so the rename at the end of the
                    // atomic write has nowhere to land. Only the delete itself
                    // is under test here.
                }
                catch (UnauthorizedAccessException)
                {
                }
            });

            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (!File.Exists(temporary) && DateTime.UtcNow < deadline)
                {
                    Thread.SpinWait(100);
                }

                if (File.Exists(temporary) && !writing.IsCompleted)
                {
                    // The store is mid-write with its .tmp handle open. Staging
                    // the file without FileShare.Delete makes this delete throw
                    // "the file is being used by another process".
                    Directory.Delete(directory, recursive: true);
                    writing.Wait(TimeSpan.FromSeconds(30));
                    return;
                }

                writing.Wait(TimeSpan.FromSeconds(30));
            }
            finally
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch
                {
                }
            }
        }

        Assert.Fail(
            $"{store} always finished its write before the .tmp file could be " +
            "observed, so the delete never overlapped an open snapshot.");
    }

    private static void Write(string store, string path)
    {
        switch (store)
        {
            case "ContextSnapshotStore":
                new ContextSnapshotStore(path).Save(new ContextSnapshot
                {
                    Players = Enumerable.Range(0, 2_000)
                        .Select(index => new ContextSnapshotPerson
                        {
                            UserId = "usr_" + index,
                            DisplayName = Name,
                            TrustText = "Trusted User"
                        })
                        .ToList()
                });
                break;

            case "FriendAvatarStore":
                new FriendAvatarStore(path).ObserveAll(
                    "usr_owner",
                    Enumerable.Range(0, 500)
                        .Select(index => new VrcUser
                        {
                            Id = "usr_" + index,
                            CurrentAvatar = "avtr_" + index,
                            CurrentAvatarImageUrl = ThumbnailUrl,
                            CurrentAvatarThumbnailImageUrl = ThumbnailUrl
                        }));
                break;

            case "FriendNotesStore":
                new FriendNotesStore(path).Set("usr_friend", LongText);
                break;

            case "FriendSnapshotStore":
                new FriendSnapshotStore(path).Save(
                    "usr_owner",
                    Enumerable.Range(0, 2_000)
                        .Select(index => new VrcUser
                        {
                            Id = "usr_" + index,
                            DisplayName = Name,
                            Username = "user" + index
                        }));
                break;

            case "InviteMessageMemoryStore":
                new InviteMessageMemoryStore(path).Set("usr_owner", "invite", LongText);
                break;

            case "MediaIndexCache":
                MediaIndexCache.Save(
                    @"C:\Pictures",
                    new[]
                    {
                        new MediaGroup(
                            "October 2026",
                            WorldName,
                            new DateTime(2026, 10, 1),
                            Enumerable.Range(0, 2_000)
                                .Select(index => new MediaItem(
                                    $@"C:\Pictures\VRChat\{index:D6}.png",
                                    WorldName,
                                    new DateTime(2026, 10, 1).AddMinutes(index)))
                                .ToList())
                    },
                    path);
                break;

            case "MediaPhotoCache":
                MediaPhotoCache.Save(
                    Enumerable.Range(0, 2_000)
                        .ToDictionary(
                            index => $@"C:\Pictures\VRChat\{index:D6}.png",
                            index => new MediaPhotoCacheEntry
                            {
                                Path = $@"C:\Pictures\VRChat\{index:D6}.png",
                                Length = 1_000_000 + index,
                                ModifiedTicks = new DateTime(2026, 10, 1).Ticks,
                                WorldName = WorldName,
                                AuthorName = "Author",
                                Players = new List<MediaPhotoCachePlayer>
                                {
                                    new() { UserId = "usr_" + index, DisplayName = "Player" }
                                }
                            }),
                    path);
                break;

            case "NotificationPersistenceStore":
            {
                using var details = JsonDocument.Parse("{}");
                new NotificationPersistenceStore(path).Save(
                    "usr_owner",
                    Enumerable.Range(0, 2_000)
                        .Select(index => new VrcNotification
                        {
                            Id = "notif_" + index,
                            Type = "invite",
                            Message = Name,
                            SenderUserId = "usr_" + index,
                            Details = details.RootElement.Clone()
                        }),
                    Array.Empty<string>());
                break;
            }

            case "SettingsStore":
                var settings = new SettingsStore(path);
                settings.Settings.FavoriteFriends = Enumerable.Range(0, 2_000)
                    .Select(index => "usr_" + index + Name)
                    .ToList();
                settings.Save();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(store), store, "Unknown store.");
        }
    }
}
