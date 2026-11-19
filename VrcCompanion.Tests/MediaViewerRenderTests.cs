using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VrcCompanion.Controls;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Opens a picture in the real Media page and checks the world, session and
/// player metadata is on screen and wired to the right buttons.
/// </summary>
public class MediaViewerRenderTests
{
    private const string JapanId = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b";
    private const string GroupId = "grp_9f1c2b3d-4e5f-4a6b-8c7d-0e1f2a3b4c5d";

    [Fact]
    public void ViewerShowsTheWorldSessionAndPlayers()
    {
        WpfTestHost.Run(() =>
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "vrccompanion-media-viewer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Window? window = null;

            try
            {
                var picturePath = Path.Combine(root, "VRChat_2025-06-02_11-04-00.png");
                CreatePng(picturePath);
                var picture = new MediaItem(
                    picturePath,
                    "Japan Street",
                    new DateTime(2025, 6, 2, 11, 4, 0));
                var group = new MediaGroup(
                    "Japan Street · June 2025",
                    "Japan Street",
                    new DateTime(2025, 6, 1),
                    new[] { picture });

                var media = new MediaLibraryViewModel(
                    root,
                    (_, _) => new[] { group },
                    cachePath: Path.Combine(root, "index.json"));
                var shell = new MainViewModel(media);
                TestHooks.SetLoggedIn(shell);
                // The shell owns the real history wiring; the test supplies it
                // before the Media section loads.
                media.HistoryProvider = () => new[] { Session() };
                media.GroupLookup = groupId => groupId == GroupId
                    ? ("Kawaii Club", "https://example.test/group.png")
                    : default;
                media.GroupFetcher = _ => Task.FromResult<(string?, string?)>(default);
                media.WorldFetcher = _ => Task.FromResult<MediaWorldInfo?>(
                    new MediaWorldInfo("Japan Street", "https://example.test/world.png", "Creator"));
                media.PersonFetcher = userId => Task.FromResult<MediaPersonInfo?>(
                    userId == "usr_a" ? new MediaPersonInfo("Alice", "https://example.test/alice.png") : null);

                window = new MainWindow(shell, initializeOnLoad: false);
                window.Show();

                shell.SelectedSection = shell.Sections.Single(section => section.Key == "media");
                WaitUntil(() => !media.IsBusy && media.PictureCount == 1, "Media page did not load.");

                Assert.Single(media.Groups);

                // The filters are dropdowns now, and both list the world/person.
                var filters = FindChildren<System.Windows.Controls.ComboBox>(window).ToList();
                Assert.True(filters.Count >= 2, $"expected two filter dropdowns, found {filters.Count}");
                var labels = string.Join(" | ", filters.Select(box =>
                    box.Items.Count + ":" + string.Join(",", box.Items.Cast<MediaFilterOption>().Select(option => option.Label))));
                Assert.True(
                    filters.Any(box => box.Items.Count >= 2 &&
                        box.Items.Cast<MediaFilterOption>().Any(option => option.Label == "Japan Street")),
                    "world filter missing (" + labels + ")");
                Assert.True(
                    filters.Any(box => box.Items.Count >= 2 &&
                        box.Items.Cast<MediaFilterOption>().Any(option => option.Label == "Alice")),
                    "person filter missing (" + labels + ")");

                media.OpenViewer(media.Groups[0].Items[0]);
                Assert.True(media.IsViewerOpen, "The viewer did not open.");
                WaitUntil(
                    () => media.ViewerHydration.IsCompleted,
                    "world/player metadata did not resolve");
                Assert.True(
                    media.HasViewerContext,
                    $"The viewer had no session metadata (groups={media.Groups.Count}, pictures={media.PictureCount}).");

                // The metadata panel lives inside a ScrollViewer, so it only joins
                // the visual tree after a layout pass.
                window.UpdateLayout();
                WaitUntil(
                    () => FindChildren<TextBlock>(window).Any(block => block.Text == "TAKEN IN"),
                    "metadata panel did not layout");

                var texts = FindChildren<TextBlock>(window)
                    .Select(block => block.Text)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                Assert.True(
                    texts.Any(text => text.Contains("Japan Street", StringComparison.Ordinal)),
                    "world name missing. Texts: " + string.Join(" | ", texts));
                Assert.True(
                    texts.Any(text => text.Contains("Creator", StringComparison.Ordinal)),
                    "world author missing. Texts: " + string.Join(" | ", texts));
                Assert.True(
                    texts.Any(text => text.Contains("Alice", StringComparison.Ordinal)),
                    "player missing. Texts: " + string.Join(" | ", texts));
                Assert.True(
                    texts.Any(text => text.Contains("Group public", StringComparison.Ordinal)),
                    "group access missing. Texts: " + string.Join(" | ", texts));
                Assert.True(
                    texts.Any(text => text.Contains("Kawaii Club", StringComparison.Ordinal)),
                    "group name missing. Texts: " + string.Join(" | ", texts));
                Assert.Contains(
                    FindChildren<Button>(window),
                    button => button.ToolTip as string == "Open this group");
                Assert.Contains(texts, text => text.Contains("Session ·", StringComparison.Ordinal));

                var buttons = FindChildren<Button>(window)
                    .SelectMany(button => button.Content is string content
                        ? new[] { content }
                        : FindChildren<TextBlock>(button).Select(block => block.Text))
                    .ToList();
                Assert.Contains("Only pictures from this world", buttons);
                Assert.Contains("View this session", buttons);

                // The avatar keeps its URL even though the download cannot finish
                // offline, and the initial fallback sits behind it.
                var avatars = FindChildren<AsyncImage>(window)
                    .Where(image => image.SourceUrl == "https://example.test/alice.png")
                    .ToList();
                Assert.NotEmpty(avatars);
                Assert.Contains(
                    FindChildren<AsyncImage>(window),
                    image => image.SourceUrl == "https://example.test/world.png");
            }
            finally
            {
                window?.Close();
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        });
    }

    private static InstanceHistoryEntry Session()
    {
        var session = new InstanceHistoryEntry
        {
            Id = "session-1",
            AccountUserId = "usr_00000000-0000-0000-0000-000000000000",
            Location = $"{JapanId}:12345~group({GroupId})~groupAccessType(public)",
            WorldId = JapanId,
            InstanceId = $"12345~group({GroupId})~groupAccessType(public)",
            // A real log row: the world id is stored, the name is not.
            WorldName = string.Empty,
            WorldThumbnailUrl = string.Empty,
            WorldAuthorName = string.Empty,
            JoinedAt = new DateTimeOffset(new DateTime(2025, 6, 2, 10, 0, 0, DateTimeKind.Local)),
            LeftAt = new DateTimeOffset(new DateTime(2025, 6, 2, 12, 0, 0, DateTimeKind.Local)),
            LastSeenAt = new DateTimeOffset(new DateTime(2025, 6, 2, 12, 0, 0, DateTimeKind.Local))
        };
        session.GetOrAddPlayer("usr_00000000-0000-0000-0000-000000000000", "Me");
        session.GetOrAddPlayer("usr_a", "Alice");
        return session;
    }

    private static void CreatePng(string path)
    {
        const int size = 32;
        var pixels = new byte[size * size * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 120;
            pixels[offset + 1] = 90;
            pixels[offset + 2] = 60;
            pixels[offset + 3] = 255;
        }

        var bitmap = BitmapSource.Create(
            size,
            size,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            size * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static IEnumerable<T> FindChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void WaitUntil(Func<bool> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.True(condition(), message);
                return;
            }

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                frame.Continue = false;
            };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}
