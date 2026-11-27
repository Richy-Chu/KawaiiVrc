using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Controls;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The instance activity box is drawn in three places from one feed. These pin the log
/// lines it accepts, the window it keeps, the fold that stops one world's Udon loop
/// filling it, and that the box updates without the page re-reading VRChat.
/// </summary>
public class InstanceActivityTests
{
    private const string WorldId = "wrld_11111111-1111-1111-1111-111111111111";
    private const string Location = WorldId + ":12345~region(us)";
    private const string OtherLocation = WorldId + ":99999~region(us)";
    private const string SomeoneId = "usr_22222222-2222-2222-2222-222222222222";

    [Fact]
    public void TheLogRecordsJoinsLeavesAndUdonEventsWithTheirOwnTime()
    {
        using var log = new LogFile(
            $"2026.11.27 12:00:00 Log        -  [Behaviour] Joining {Location}",
            $"2026.11.27 12:00:05 Log        -  [Behaviour] OnPlayerJoined Someone ({SomeoneId})",
            $"2026.11.27 12:00:10 Log        -  [Behaviour] OnPlayerLeft Someone ({SomeoneId})",
            "2026.11.27 12:00:12 Error      -  [UdonBehaviour] An exception occurred during Udon execution, this UdonBehaviour will be halted.",
            "2026.11.27 12:00:20 Log        -  [Behaviour] Configuring Udon");

        var service = new VrChatLogPlayerService(() => log.Path);
        var snapshot = service.Poll();

        Assert.NotNull(snapshot);
        var activity = snapshot!.Activity;
        Assert.NotNull(activity);
        Assert.Collection(
            activity!,
            entry =>
            {
                Assert.Equal(VrChatLogActivityKind.Joined, entry.Kind);
                Assert.Equal("Someone", entry.DisplayName);
                Assert.Equal(SomeoneId, entry.UserId);
                // The row shows the log line's time, not the moment the app read it.
                Assert.Equal(new DateTime(2026, 11, 27, 12, 0, 5), entry.At.LocalDateTime);
                Assert.Equal(1, entry.RepeatCount);
            },
            entry =>
            {
                Assert.Equal(VrChatLogActivityKind.Left, entry.Kind);
                Assert.Equal("Someone", entry.DisplayName);
                Assert.Equal(new DateTime(2026, 11, 27, 12, 0, 10), entry.At.LocalDateTime);
            },
            entry =>
            {
                Assert.Equal(VrChatLogActivityKind.Udon, entry.Kind);
                Assert.Contains("Udon execution", entry.Detail, StringComparison.Ordinal);
            },
            entry =>
            {
                Assert.Equal(VrChatLogActivityKind.Udon, entry.Kind);
                Assert.Equal("Udon configured for this world", entry.Detail);
            });
    }

    [Fact]
    public void ActivityBelongsToTheInstanceItHappenedIn()
    {
        using var log = new LogFile(
            $"2026.11.27 12:00:00 Log        -  [Behaviour] Joining {Location}",
            $"2026.11.27 12:00:05 Log        -  [Behaviour] OnPlayerJoined Someone ({SomeoneId})");

        var service = new VrChatLogPlayerService(() => log.Path);
        var first = service.Poll();
        Assert.Single(first!.Activity!);

        log.Append(
            $"2026.11.27 12:05:00 Log        -  [Behaviour] Joining {OtherLocation}",
            $"2026.11.27 12:05:05 Log        -  [Behaviour] OnPlayerJoined Other ({SomeoneId})");

        var second = service.Poll();
        Assert.Equal(OtherLocation, second!.Location);
        // The previous room's joins do not carry into this one.
        var only = Assert.Single(second.Activity!);
        Assert.Equal("Other", only.DisplayName);
    }

    [Fact]
    public void TheActivityListKeepsItsNewestHundredRowsAndDoesNotRepeatAPoll()
    {
        var lines = new List<string> { $"2026.11.27 11:00:00 Log        -  [Behaviour] Joining {Location}" };
        for (var index = 0; index < 120; index++)
        {
            lines.Add(
                $"2026.11.27 12:00:00 Log        -  [Behaviour] OnPlayerJoined Person {index} " +
                $"(usr_{index:D8}-0000-0000-0000-000000000000)");
        }

        using var log = new LogFile(lines.ToArray());
        var service = new VrChatLogPlayerService(() => log.Path);
        var first = service.Poll();
        Assert.Equal(100, first!.Activity!.Count);
        // The cap drops the oldest rows, not the newest ones.
        Assert.Equal("Person 20", first.Activity[0].DisplayName);
        Assert.Equal("Person 119", first.Activity[^1].DisplayName);
        // The revision counts every event recorded, so the cap never hides a change.
        Assert.Equal(120, first.ActivityRevision);

        // A poll that finds no new line returns the same list, so the list bound to the
        // page is never re-walked and Context's signature does not move.
        var second = service.Poll();
        Assert.Same(first.Activity, second!.Activity);
        Assert.Equal(first.ActivityRevision, second.ActivityRevision);
    }

    [Fact]
    public void RepeatingTheSameUdonLineFoldsIntoOneRow()
    {
        var lines = new List<string> { $"2026.11.27 12:00:00 Log        -  [Behaviour] Joining {Location}" };
        for (var index = 0; index < 150; index++)
        {
            // One world writing the same warning every frame: 150 lines, one event.
            lines.Add("2026.11.27 12:00:01 Error      -  [UdonBehaviour] Script is looping again.");
        }

        lines.Add($"2026.11.27 12:00:02 Log        -  [Behaviour] OnPlayerJoined Late ({SomeoneId})");

        using var log = new LogFile(lines.ToArray());
        var service = new VrChatLogPlayerService(() => log.Path);
        var activity = service.Poll()!.Activity!;

        // The run of identical lines is one Udon row with the count, not 150 rows that
        // push every other event out of the 100-row window.
        Assert.Equal(2, activity.Count);
        Assert.Equal(VrChatLogActivityKind.Udon, activity[0].Kind);
        Assert.Equal("Script is looping again.", activity[0].Detail);
        Assert.Equal(150, activity[0].RepeatCount);
        // The folded row keeps the time of the last time it happened.
        Assert.Equal(new DateTime(2026, 11, 27, 12, 0, 1), activity[0].At.LocalDateTime);
        Assert.Equal(VrChatLogActivityKind.Joined, activity[1].Kind);
        Assert.Equal("Late", activity[1].DisplayName);
    }

    [Fact]
    public void AFoldedUdonRowShowsItsCountInTheBox()
    {
        var feed = new InstanceActivityFeed();
        feed.Apply(new[]
        {
            new VrChatLogActivity(
                VrChatLogActivityKind.Udon,
                string.Empty,
                string.Empty,
                "Script is looping again.",
                new DateTimeOffset(2026, 11, 27, 12, 0, 1, TimeSpan.Zero),
                RepeatCount: 12)
        });

        var row = Assert.Single(feed.Activity);
        Assert.True(row.HasRepeat);
        Assert.Equal("× 12", row.RepeatText);
        Assert.Contains("× 12", row.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowsShowATwelveHourTime()
    {
        // A quarter to one in the afternoon, in this machine's zone, so the stamp is
        // predictable whichever zone the suite runs in.
        var day = new DateTime(2026, 11, 27, 12, 45, 0, DateTimeKind.Local);
        var row = new InstanceActivityItem(new VrChatLogActivity(
            VrChatLogActivityKind.Joined,
            "A",
            SomeoneId,
            "Joined the instance",
            new DateTimeOffset(day)));

        // The three surfaces share this row, so the stamp is fixed once: 12:45:00 PM,
        // never 24-hour "12:45:00" or "00:45".
        Assert.Matches(@"^12:45:00 PM$", row.TimeText);
    }

    [Fact]
    public void TheActivityBoxFiltersJoinsLeavesAndUdonEvents()
    {
        var feed = new InstanceActivityFeed();
        feed.Apply(new[]
        {
            Entry(VrChatLogActivityKind.Joined, "A", 1),
            Entry(VrChatLogActivityKind.Left, "B", 2),
            Entry(VrChatLogActivityKind.Udon, string.Empty, 3)
        });

        Assert.Equal(3, feed.Activity.Count);
        // Newest first, so the Udon line written last is at the top.
        Assert.Equal("Udon event", feed.Activity[0].Title);
        Assert.True(feed.HasActivity);
        Assert.Equal("3 event(s)", feed.ActivityText);

        feed.SelectedActivityFilter = feed.ActivityFilters.Single(filter => filter.Key == "joined");
        var joined = Assert.Single(feed.Activity);
        Assert.Equal(VrChatLogActivityKind.Joined, joined.Kind);
        Assert.Single(feed.ActivityPage.Items);
        Assert.Equal("1 of 3 event(s)", feed.ActivityText);

        feed.SelectedActivityFilter = feed.ActivityFilters.Single(filter => filter.Key == "left");
        Assert.Equal(VrChatLogActivityKind.Left, Assert.Single(feed.Activity).Kind);

        feed.SelectedActivityFilter = feed.ActivityFilters.Single(filter => filter.Key == "udon");
        var udon = Assert.Single(feed.Activity);
        Assert.Equal("UDON", udon.KindLabel);
        Assert.Equal("Udon", udon.DisplayName);

        // Back to everything: the filter keeps the cached rows instead of rebuilding them.
        var before = feed.Activity.ToArray();
        feed.SelectedActivityFilter = feed.ActivityFilters[0];
        Assert.Equal(3, feed.Activity.Count);
        Assert.Contains(feed.Activity, row => before.Contains(row));
    }

    [Fact]
    public void AnEmptyActivityListShowsItsOwnTextAndNoRows()
    {
        var feed = new InstanceActivityFeed();
        feed.Apply(Array.Empty<VrChatLogActivity>());

        Assert.False(feed.HasActivity);
        Assert.Empty(feed.ActivityPage.Items);
        Assert.Equal("Nothing yet in this instance.", feed.ActivityText);
        Assert.Contains("VRChat's log", feed.ActivityEmptyText, StringComparison.Ordinal);
    }

    [Fact]
    public void TheContextActivityBoxUpdatesWithoutReadingVrChat()
    {
        using var handler = new CountingHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        var context = new ContextViewModel();
        TestHooks.AttachContextViewModel(shell, context);

        var snapshot = Snapshot(
            7,
            Entry(VrChatLogActivityKind.Joined, "A", 1),
            Entry(VrChatLogActivityKind.Udon, string.Empty, 2));

        // A world that writes Udon lines calls exactly this: the rows move, the instance
        // is not read again, so the box cannot turn a log line into an API call.
        Assert.True(shell.UpdateContextActivity(snapshot));
        Assert.Equal(0, handler.Requests);
        Assert.Equal(2, context.ActivityFeed.Activity.Count);

        // Leaving the room empties it through the same path.
        Assert.True(shell.UpdateContextActivity(null));
        Assert.Equal(0, handler.Requests);
        Assert.False(context.ActivityFeed.HasActivity);
    }

    [Fact]
    public void TheInstanceTabFeedTakesItsRowsFromTheLogSnapshot()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new CountingHandler();
            using var api = new VrcApiClient(null, handler);
            var instance = new InstanceViewModel(api, ImageLoader.Shared);

            instance.ApplyLogActivity(Snapshot(
                4,
                Entry(VrChatLogActivityKind.Joined, "A", 1),
                Entry(VrChatLogActivityKind.Left, "B", 2)));

            Assert.Equal(0, handler.Requests);
            Assert.Equal(2, instance.ActivityFeed.Activity.Count);
            Assert.True(instance.ActivityFeed.HasActivity);
        });
    }

    [Fact]
    public void TheWorldDialogPlayersPanelUsesTheInstanceTabsFeed()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new CountingHandler();
            using var api = new VrcApiClient(null, handler);
            var instance = new InstanceViewModel(api, ImageLoader.Shared);
            instance.ApplyLogActivity(Snapshot(2, Entry(VrChatLogActivityKind.Joined, "A", 1)));

            // The dialog's Players panel shows the Instance tab's own feed, so all three
            // surfaces read one list and one filter.
            var dialog = new WorldDialogViewModel(
                new WorldItem(new VrcWorld { Id = WorldId, Name = "Popcorn Palace" }),
                api,
                ImageLoader.Shared,
                activityFeed: instance.ActivityFeed);

            Assert.Same(instance.ActivityFeed, dialog.ActivityFeed);
            Assert.Single(dialog.ActivityFeed!.Activity);

            dialog.ActivityFeed.SelectedActivityFilter =
                dialog.ActivityFeed.ActivityFilters.Single(filter => filter.Key == "udon");
            Assert.Empty(dialog.ActivityFeed.Activity);
            Assert.Same(instance.ActivityFeed, dialog.ActivityFeed);
        });
    }

    [Fact]
    public void TheActivityViewDrawsItsRowsAndFollowsItsFilter()
    {
        WpfTestHost.Run(() =>
        {
            var feed = new InstanceActivityFeed();
            feed.Apply(new[]
            {
                Entry(VrChatLogActivityKind.Joined, "Someone", 1),
                Entry(VrChatLogActivityKind.Udon, string.Empty, 2)
            });

            var window = new Window
            {
                Width = 520,
                Height = 640,
                Content = new InstanceActivityView { DataContext = feed }
            };
            window.Show();
            try
            {
                Pump(window);

                Assert.True(
                    FindVisualChildren<TextBlock>(window).Any(text => text.Text == "Someone" && text.IsVisible),
                    "The joined row never reached the window.");
                Assert.True(
                    FindVisualChildren<TextBlock>(window).Any(text => text.Text == "Udon event" && text.IsVisible),
                    "The Udon row never reached the window.");

                // The filter is the feed's own selected pill, and the list follows it.
                feed.SelectedActivityFilter =
                    feed.ActivityFilters.Single(filter => filter.Key == "joined");
                Pump(window);

                Assert.Contains(
                    FindVisualChildren<TextBlock>(window),
                    text => text.Text == "Someone" && text.IsVisible);
                Assert.DoesNotContain(
                    FindVisualChildren<TextBlock>(window),
                    text => text.Text == "Udon event" && text.IsVisible);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void AnAvatarSwitchIsRecordedWithThePlayerTheAvatarAndItsAuthor()
    {
        using var log = new LogFile(
            $"2026.11.27 12:00:00 Log        -  [Behaviour] Joining {Location}",
            $"2026.11.27 12:00:05 Log        -  [Behaviour] OnPlayerJoined Someone ({SomeoneId})",
            "2026.11.27 12:00:07 Log        -  [AssetBundleDownloadManager] [1] Unpacking Avatar (Kitten by Maker)",
            "2026.11.27 12:00:09 Log        -  [Behaviour] Switching Someone to avatar Kitten");

        var service = new VrChatLogPlayerService(() => log.Path);
        var activity = service.Poll()!.Activity!;

        var row = Assert.Single(activity, entry => entry.Kind == VrChatLogActivityKind.AvatarChanged);
        Assert.Equal("Someone", row.DisplayName);
        // The switch line names the avatar only; the author comes from the unpack line.
        Assert.Equal(SomeoneId, row.UserId);
        Assert.Equal("Kitten", row.AvatarName);
        Assert.Equal("Maker", row.AvatarAuthor);
        // Joins are still recorded beside it.
        Assert.Contains(activity, entry => entry.Kind == VrChatLogActivityKind.Joined);
    }

    [Fact]
    public void TheAvatarFilterKeepsOnlyAvatarChanges()
    {
        var feed = new InstanceActivityFeed();
        feed.Apply(new[]
        {
            Entry(VrChatLogActivityKind.Joined, "A", 1),
            AvatarRow("Someone", "Kitten", "Maker", 2),
            Entry(VrChatLogActivityKind.Udon, string.Empty, 3)
        });

        var avatarFilter = feed.ActivityFilters.Single(filter => filter.Key == "avatar");
        Assert.Equal("Avatar", avatarFilter.Label);
        feed.SelectedActivityFilter = avatarFilter;

        var row = Assert.Single(feed.Activity);
        Assert.Equal("AVATAR", row.KindLabel);
        Assert.Equal("Someone", row.Title);
        Assert.Equal("now wearing Kitten by Maker", row.DetailText);
    }

    [Fact]
    public void AnAvatarRowOpensTheAvatarAndTheProfileLikeTheActivityPage()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new CountingHandler();
            using var api = new VrcApiClient(null, handler);
            using var shell = new MainViewModel(api);
            var avatars = new AvatarViewModel(api);
            TestHooks.AttachAvatarViewModel(shell, avatars);

            var feed = new InstanceActivityFeed();
            feed.Apply(new[] { AvatarRow("Someone", "Kitten", "Maker", 2) });
            var row = Assert.Single(feed.Activity);

            // The box hands the Activity page the same row model that page's avatar rows
            // use, so a name-only switch resolves to the public avatar there too.
            var entry = Assert.IsType<ActivityEntry>(row.AvatarEntry);
            Assert.Equal(ActivityType.AvatarChanged, entry.Type);
            Assert.Equal("Kitten", entry.EffectiveAvatarName);
            Assert.Equal("Maker", entry.AvatarAuthor);
            Assert.Equal(SomeoneId, entry.UserId);
            Assert.True(row.CanOpenAvatar);
            Assert.True(row.CanOpenProfile);

            Assert.True(shell.OpenActivityAvatarCommand.CanExecute(entry));
            shell.OpenActivityAvatarCommand.Execute(entry);

            // The popup opens on the name alone, before any lookup answers.
            Assert.True(avatars.IsAvatarDetailsOpen);
            Assert.Contains("Kitten", avatars.SelectedAvatar!.Name, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void AnAvatarRowOffersItsTwoActionsInTheBox()
    {
        WpfTestHost.Run(() =>
        {
            var feed = new InstanceActivityFeed();
            feed.Apply(new[]
            {
                AvatarRow("Someone", "Kitten", "Maker", 1),
                Entry(VrChatLogActivityKind.Joined, "Another", 2)
            });

            var window = new Window
            {
                Width = 520,
                Height = 640,
                Content = new InstanceActivityView { DataContext = feed }
            };
            window.Show();
            try
            {
                Pump(window);

                var buttons = FindVisualChildren<Button>(window).ToList();
                var avatarActions = buttons.Where(button => Equals(button.Content, "View avatar")).ToList();
                var profileActions = buttons.Where(button => Equals(button.Content, "Profile")).ToList();
                // The list can keep a container from the publish before it, so the check is
                // that the row the reader sees carries both actions, not that only one exists.
                Assert.Contains(avatarActions, button => button.IsVisible);
                Assert.Contains(profileActions, button => button.IsVisible);
                Assert.Equal("now wearing Kitten by Maker", feed.Activity.Single(
                    item => item.IsAvatarChange).DetailText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static VrChatLogActivity AvatarRow(string name, string avatar, string author, int minute)
        => new(
            VrChatLogActivityKind.AvatarChanged,
            name,
            SomeoneId,
            avatar,
            new DateTimeOffset(2026, 11, 27, 12, minute, 0, TimeSpan.Zero),
            RepeatCount: 1,
            AvatarName: avatar,
            AvatarAuthor: author);

    private static VrChatLogSnapshot Snapshot(int revision, params VrChatLogActivity[] activity)
        => new(
            Location,
            Array.Empty<VrChatLogPlayer>(),
            Activity: activity,
            ActivityRevision: revision);

    private static VrChatLogActivity Entry(VrChatLogActivityKind kind, string name, int minute)
        => new(
            kind,
            name,
            kind == VrChatLogActivityKind.Udon ? string.Empty : SomeoneId,
            kind switch
            {
                VrChatLogActivityKind.Joined => "Joined the instance",
                VrChatLogActivityKind.Left => "Left the instance",
                _ => "Udon configured for this world"
            },
            new DateTimeOffset(2026, 11, 27, 12, minute, 0, TimeSpan.Zero));

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var nested in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return nested;
            }
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>A real log file the parser reads from the start, removed with the test.</summary>
    private sealed class LogFile : IDisposable
    {
        public LogFile(params string[] lines)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "vrccompanion-activity-" + Guid.NewGuid().ToString("N") + ".log");
            File.WriteAllText(Path, string.Join('\n', lines));
        }

        public string Path { get; }

        public void Append(params string[] lines)
            => File.AppendAllText(Path, "\n" + string.Join('\n', lines));

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // A leftover temp file is the harness's to sweep, not the test's to fail on.
            }
        }
    }
}
