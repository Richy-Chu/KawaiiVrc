using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The Context page's activity box is built from VRChat's own log lines. These pin the
/// lines it accepts, the window it keeps, and the filter it applies, because a wrong
/// regex here shows made-up activity rather than an empty box.
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
        // page is never re-walked and Context's log signature does not move.
        var second = service.Poll();
        Assert.Same(first.Activity, second!.Activity);
        Assert.Equal(first.ActivityRevision, second.ActivityRevision);
    }

    [Fact]
    public void TheActivityBoxFiltersJoinsLeavesAndUdonEvents()
    {
        var context = new ContextViewModel();
        context.ApplyActivity(new[]
        {
            Entry(VrChatLogActivityKind.Joined, "A", 1),
            Entry(VrChatLogActivityKind.Left, "B", 2),
            Entry(VrChatLogActivityKind.Udon, string.Empty, 3)
        });

        Assert.Equal(3, context.Activity.Count);
        // Newest first, so the Udon line written last is at the top.
        Assert.Equal("Udon event", context.Activity[0].Title);
        Assert.True(context.HasActivity);
        Assert.Equal("3 event(s)", context.ActivityText);

        context.SelectedActivityFilter = context.ActivityFilters.Single(filter => filter.Key == "joined");
        var joined = Assert.Single(context.Activity);
        Assert.Equal(VrChatLogActivityKind.Joined, joined.Kind);
        Assert.Single(context.ActivityPage.Items);
        Assert.Equal("1 of 3 event(s)", context.ActivityText);

        context.SelectedActivityFilter = context.ActivityFilters.Single(filter => filter.Key == "left");
        Assert.Equal(VrChatLogActivityKind.Left, Assert.Single(context.Activity).Kind);

        context.SelectedActivityFilter = context.ActivityFilters.Single(filter => filter.Key == "udon");
        var udon = Assert.Single(context.Activity);
        Assert.Equal("UDON", udon.KindLabel);
        Assert.Equal("Udon", udon.DisplayName);

        // Back to everything: the filter keeps the cached rows instead of rebuilding them.
        var before = context.Activity.ToArray();
        context.SelectedActivityFilter = context.ActivityFilters[0];
        Assert.Equal(3, context.Activity.Count);
        Assert.Contains(context.Activity, row => before.Contains(row));
    }

    [Fact]
    public void AnEmptyActivityListShowsItsOwnTextAndNoRows()
    {
        var context = new ContextViewModel();
        context.ApplyActivity(Array.Empty<VrChatLogActivity>());

        Assert.False(context.HasActivity);
        Assert.Empty(context.ActivityPage.Items);
        Assert.Equal("Nothing yet in this instance.", context.ActivityText);
    }

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
