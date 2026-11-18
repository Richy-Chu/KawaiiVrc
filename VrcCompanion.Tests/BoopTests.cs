using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class BoopTests
{
    [Fact]
    public async Task PlainBoopPostsAnEmptyObjectToTheUsersBoopEndpoint()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);

        await api.SendBoopAsync("usr_target");

        Assert.Equal("/api/1/users/usr_target/boop", handler.Path);
        Assert.Equal("{}", handler.Body);
    }

    [Fact]
    public async Task EmojiBoopSendsOnlyTheEmojiId()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);

        await api.SendBoopAsync("usr_target", emojiId: "default_wave");

        Assert.Equal("/api/1/users/usr_target/boop", handler.Path);
        Assert.Equal("""{"emojiId":"default_wave"}""", handler.Body);
    }

    [Fact]
    public async Task InventoryBoopSendsOnlyTheInventoryItemId()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);

        await api.SendBoopAsync("usr_target", inventoryItemId: "inv_miku");

        Assert.Equal("/api/1/users/usr_target/boop", handler.Path);
        Assert.Equal("""{"inventoryItemId":"inv_miku"}""", handler.Body);
    }

    [Fact]
    public async Task EmojiAndInventoryCannotBeSentTogether()
    {
        using var api = new VrcApiClient(null, new CaptureHandler());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            api.SendBoopAsync("usr_target", "default_wave", "inv_miku"));
    }

    [Fact]
    public async Task V2BoopsAreMergedIntoTheNotificationFeed()
    {
        var handler = new NotificationFeedHandler
        {
            LegacyJson = """
                [
                  {
                    "id": "not_legacy",
                    "type": "friendRequest",
                    "senderUserId": "usr_legacy",
                    "senderUsername": "LegacySender",
                    "details": {},
                    "seen": false,
                    "created_at": "2026-11-17T10:00:00Z"
                  }
                ]
                """,
            V2Json = """
                [
                  {
                    "id": "not_boop",
                    "type": "boop",
                    "senderUserId": "usr_hellkitty",
                    "senderUsername": "hellkittypantle",
                    "details": {
                      "emojiId": "default_frog",
                      "inventoryItemId": "inv_frog"
                    },
                    "seen": false,
                    "createdAt": "2026-11-17T10:15:30Z",
                    "version": 2
                  },
                  {
                    "id": "not_legacy",
                    "type": "friendRequest",
                    "senderUserId": "usr_legacy",
                    "senderUsername": "LegacySender",
                    "seen": false,
                    "createdAt": "2026-11-17T10:00:00Z",
                    "version": 2
                  }
                ]
                """
        };
        using var api = new VrcApiClient(null, handler);

        var notifications = await api.GetNotificationsAsync();

        Assert.Contains(handler.Paths, path =>
            path.StartsWith("/api/1/notifications", StringComparison.Ordinal));
        Assert.Equal(2, notifications.Count);
        var boop = Assert.Single(notifications, notification => notification.Type == "boop");
        Assert.True(boop.IsV2);
        Assert.Equal("hellkittypantle", boop.SenderName);
        Assert.Equal("default_frog", boop.Details.GetProperty("emojiId").GetString());
        Assert.Equal(
            DateTimeOffset.Parse("2026-11-17T10:15:30Z"),
            boop.Timestamp);

        var item = new NotificationItem(boop);
        Assert.Equal("BOOP", item.TypeLabel);
        Assert.Equal("other", item.Category);
        Assert.Equal("Booped you with an emoji.", item.MessageText);
        Assert.True(item.HasBoopAttachment);
        Assert.Equal("hellkittypantle", item.SenderName);
        Assert.True(item.IsUnseen);

        var legacy = Assert.Single(notifications, notification => notification.Id == "not_legacy");
        Assert.False(legacy.IsV2);
    }

    [Fact]
    public async Task V2FeedFallsBackWhenTheDocumentedRouteIsUnavailable()
    {
        var handler = new NotificationFeedHandler
        {
            V2Status = HttpStatusCode.NotFound,
            V2Json = """
                [
                  {
                    "id": "not_boop",
                    "type": "boop",
                    "senderUserId": "usr_hellkitty",
                    "senderUsername": "hellkittypantle",
                    "seen": false,
                    "createdAt": "2026-11-17T10:15:30Z",
                    "version": 2
                  }
                ]
                """
        };
        using var api = new VrcApiClient(null, handler);

        var notifications = await api.GetNotificationsAsync();

        Assert.Contains(handler.Paths, path =>
            path.StartsWith("/api/1/auth/user/notificationsV2", StringComparison.Ordinal));
        Assert.Single(notifications, notification => notification.Type == "boop");
    }

    [Fact]
    public void BuiltInBoopEmojiIdsResolveToTheirArtwork()
    {
        var blush = BoopEmojiOption.FindById("default_blushing");
        Assert.NotNull(blush);
        Assert.Equal("Blushing", blush!.Name);
        Assert.Contains("Blush.webp", blush.ImageUrl, StringComparison.Ordinal);

        var handwave = BoopEmojiOption.FindById("default_handwave");
        Assert.NotNull(handwave);
        Assert.Equal("Hand Wave", handwave!.Name);

        var thumbsUp = BoopEmojiOption.FindById("default_like");
        Assert.NotNull(thumbsUp);
        Assert.Equal("Thumbs Up", thumbsUp!.Name);
    }

    [Fact]
    public async Task InventoryBoopArtworkComesFromTheSendersItem()
    {
        var handler = new JsonHandler("""
            {
              "id": "inv_frog",
              "name": "Frog",
              "imageUrl": "https://example.test/frog.webp",
              "thumbnailUrl": "https://example.test/frog-thumb.webp",
              "itemType": "emoji"
            }
            """);
        using var api = new VrcApiClient(null, handler);

        var item = await api.GetUserInventoryItemAsync("usr_sender", "inv_frog");

        Assert.NotNull(item);
        Assert.Equal("Frog", item!.FriendlyName);
        Assert.Equal("https://example.test/frog.webp", item.DisplayImageUrl);
        Assert.Equal("/api/1/user/usr_sender/inventory/inv_frog", handler.Path);
    }

    [Fact]
    public async Task CustomBoopEmojiFileResolvesToItsImage()
    {
        var handler = new JsonHandler("""
            {
              "id": "file_emoji",
              "name": "Party",
              "displayName": "Party emoji",
              "tags": ["emoji"],
              "versions": [
                { "file": { "url": "https://example.test/party.webp" } }
              ]
            }
            """);
        using var api = new VrcApiClient(null, handler);

        var file = await api.GetFileAsync("file_emoji");

        Assert.NotNull(file);
        Assert.Equal("Party emoji", file!.FriendlyName);
        Assert.Equal("https://example.test/party.webp", file.ImageUrl);
        Assert.Equal("/api/1/file/file_emoji", handler.Path);
    }

    [Fact]
    public async Task InventoryBoopHydratesItsNameAndImageOntoTheCard()
    {
        var handler = new JsonHandler("""
            {
              "id": "inv_frog",
              "name": "Frog",
              "imageUrl": "https://example.test/frog.webp",
              "itemType": "emoji"
            }
            """);
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        using var details = JsonDocument.Parse(
            """{"inventoryItemId":"inv_frog"}""");
        var notification = new VrcNotification
        {
            Id = "not_boop_hydrate",
            Type = "boop",
            SenderUserId = "usr_sender",
            SenderUsername = "Sender",
            Details = details.RootElement.Clone()
        };

        TestHooks.UpdateNotifications(shell, new[] { notification });

        var item = Assert.Single(shell.Notifications);
        for (var attempt = 0; attempt < 100 && !item.HasBoopImage; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(item.HasBoopImage, "The inventory artwork should be resolved.");
        Assert.Equal("Frog", item.BoopAttachmentTitle);
        Assert.Equal("Booped you with Frog.", item.MessageText);
        Assert.Equal("Inventory emoji · Frog", item.BoopKindText);
        Assert.Equal(
            "/api/1/user/usr_sender/inventory/inv_frog",
            handler.Path);
    }

    [Fact]
    public async Task V2BoopSeenStateUsesTheV2Endpoint()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);

        await api.MarkNotificationSeenAsync("not_boop", isV2: true);

        Assert.Equal("POST", handler.Method);
        Assert.Equal("/api/1/notifications/not_boop/see", handler.Path);
    }

    [Fact]
    public void FriendProfileRendersTheCopyActionsAndBoopPicker()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            using var api = new VrcApiClient();
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = "usr_friend",
                    DisplayName = "Friend",
                    Username = "friend",
                    IsFriend = true
                }),
                api,
                ImageLoader.Shared);
            TestHooks.AttachFriendDetail(shell, profile);
            var window = new MainWindow(shell, initializeOnLoad: false)
            {
                Width = 1100,
                Height = 720,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 100
            };

            try
            {
                window.Show();
                window.UpdateLayout();

                var boop = FindVisualChildren<Button>(window)
                    .Single(button => button.ToolTip is string tooltip &&
                        tooltip.StartsWith("Send a boop to this friend", StringComparison.Ordinal));
                Assert.True(boop.IsVisible);
                Assert.True(boop.IsEnabled);
                Assert.Contains(
                    FindVisualChildren<Button>(window),
                    button => button.IsVisible && Equals(button.ToolTip, "Copy user ID"));
                Assert.Contains(
                    FindVisualChildren<Button>(window),
                    button => button.IsVisible && Equals(button.ToolTip, "Copy profile link"));

                boop.Command.Execute(boop.CommandParameter);
                window.UpdateLayout();

                Assert.True(profile.IsBoopPickerOpen);
                var popup = FindVisualChildren<Popup>(window)
                    .Single(candidate => candidate.IsOpen);
                var child = Assert.IsType<Border>(popup.Child);
                Assert.Contains(
                    FindVisualChildren<TextBlock>(child),
                    text => text.Text == "SEND A BOOP");
                Assert.Contains(
                    FindVisualChildren<Button>(child),
                    button => button.CommandParameter is BoopEmojiOption);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void BoopActionIsOnlyAvailableOnAnotherUsersFriendProfile()
    {
        var friend = new FriendDetailViewModel(
            new FriendItem(new VrcUser
            {
                Id = "usr_friend",
                DisplayName = "Friend",
                IsFriend = true
            }),
            new VrcApiClient(),
            ImageLoader.Shared);

        var self = new FriendDetailViewModel(
            new FriendItem(new VrcUser
            {
                Id = "usr_me",
                DisplayName = "Me",
                IsFriend = true
            }),
            new VrcApiClient(),
            ImageLoader.Shared,
            isSelf: true);

        var stranger = new FriendDetailViewModel(
            new FriendItem(new VrcUser
            {
                Id = "usr_stranger",
                DisplayName = "Stranger"
            }),
            new VrcApiClient(),
            ImageLoader.Shared);

        Assert.True(friend.CanBoop);
        Assert.False(self.CanBoop);
        Assert.False(stranger.CanBoop);
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
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Method { get; private set; }

        public string? Path { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }

    private sealed class JsonHandler : HttpMessageHandler
    {
        private readonly string _json;

        public JsonHandler(string json) => _json = json;

        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class NotificationFeedHandler : HttpMessageHandler
    {
        public string LegacyJson { get; set; } = "[]";

        public string V2Json { get; set; } = "[]";

        public HttpStatusCode V2Status { get; set; } = HttpStatusCode.OK;

        public List<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            Paths.Add(path);

            if (path.StartsWith("/api/1/notifications", StringComparison.Ordinal) &&
                V2Status != HttpStatusCode.OK)
            {
                return Task.FromResult(new HttpResponseMessage(V2Status));
            }

            var body = path.StartsWith("/api/1/auth/user/notificationsV2", StringComparison.Ordinal)
                ? V2Json
                : path.StartsWith("/api/1/auth/user/notifications", StringComparison.Ordinal)
                    ? LegacyJson
                    : path.StartsWith("/api/1/notifications", StringComparison.Ordinal)
                        ? V2Json
                        : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
