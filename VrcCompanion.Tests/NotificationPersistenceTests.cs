using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class NotificationPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vrccompanion-notifications-" + Guid.NewGuid().ToString("N"));

    private string PathFor(string name) => Path.Combine(_directory, name);

    public NotificationPersistenceTests() => Directory.CreateDirectory(_directory);

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
    public void WorldInvitesAndInviteRequestsSurviveARestart()
    {
        var path = PathFor("notifications.json");
        new NotificationPersistenceStore(path).Save(
            "usr_me",
            new[]
            {
                Notification("not_invite", "invite"),
                Notification("not_request", "requestInvite"),
                Notification("not_friend", "friendRequest"),
                Notification("not_boop", "boop")
            },
            Array.Empty<string>());

        var reopened = new NotificationPersistenceStore(path).Load("usr_me");

        Assert.Equal(
            new[] { "not_invite", "not_request", "not_boop" },
            reopened.Notifications.Select(notification => notification.Id));
        Assert.Equal("The Black Cat", new NotificationItem(reopened.Notifications[0]).InviteWorldName);
    }

    [Fact]
    public void GroupInvitesSurviveARestart()
    {
        var path = PathFor("notifications.json");
        using var details = JsonDocument.Parse(
            "{\"groupId\":\"grp_77777777-7777-7777-7777-777777777777\",\"groupName\":\"HeartBendersMC\"}");
        new NotificationPersistenceStore(path).Save(
            "usr_me",
            new[]
            {
                new VrcNotification
                {
                    Id = "not_group",
                    Type = "group.invite",
                    SenderUserId = "usr_inviter",
                    SenderUsername = "Willow0rBunny",
                    Details = details.RootElement.Clone()
                }
            },
            Array.Empty<string>());

        var item = new NotificationItem(
            Assert.Single(new NotificationPersistenceStore(path).Load("usr_me").Notifications));

        Assert.True(item.IsGroupInvite);
        Assert.True(item.CanAcceptGroupInvite);
        Assert.True(item.CanDeclineGroupInvite);
        Assert.Equal("HeartBendersMC", item.GroupName);
    }

    [Fact]
    public void ResolvedBoopArtworkSurvivesARestart()
    {
        var path = PathFor("notifications.json");
        using var details = JsonDocument.Parse(
            "{\"inventoryItemId\":\"inv_frog\"}");
        new NotificationPersistenceStore(path).Save(
            "usr_me",
            new[]
            {
                new VrcNotification
                {
                    Id = "not_boop",
                    Type = "boop",
                    SenderUserId = "usr_sender",
                    SenderUsername = "Sender",
                    Details = details.RootElement.Clone(),
                    BoopName = "Frog",
                    BoopImageUrl = "https://example.test/frog.webp",
                    BoopKind = "inventory"
                }
            },
            Array.Empty<string>());

        var item = new NotificationItem(
            Assert.Single(new NotificationPersistenceStore(path).Load("usr_me").Notifications));

        Assert.True(item.IsBoop);
        Assert.True(item.HasBoopImage);
        Assert.Equal("Frog", item.BoopAttachmentTitle);
        Assert.Equal("Booped you with Frog.", item.MessageText);
        Assert.Equal("Inventory emoji · Frog", item.BoopKindText);
    }

    [Fact]
    public void ClearingAnInvitePersistsTheRemoval()
    {
        var path = PathFor("notifications.json");
        var store = new NotificationPersistenceStore(path);
        store.Save("usr_me", new[] { Notification("not_invite", "invite") }, Array.Empty<string>());

        store.Save("usr_me", Array.Empty<VrcNotification>(), new[] { "not_invite" });

        var reopened = new NotificationPersistenceStore(path).Load("usr_me");
        Assert.Empty(reopened.Notifications);
        Assert.Contains("not_invite", reopened.ClearedIds);
    }

    [Fact]
    public void AccountsDoNotShareInvites()
    {
        var path = PathFor("notifications.json");
        var store = new NotificationPersistenceStore(path);
        store.Save("usr_a", new[] { Notification("not_a", "invite") }, Array.Empty<string>());
        store.Save("usr_b", new[] { Notification("not_b", "requestInvite") }, Array.Empty<string>());

        var reopened = new NotificationPersistenceStore(path);
        Assert.Equal(new[] { "not_a" }, reopened.Load("usr_a").Notifications.Select(notification => notification.Id));
        Assert.Equal(new[] { "not_b" }, reopened.Load("usr_b").Notifications.Select(notification => notification.Id));
    }

    [Fact]
    public void CorruptFileLoadsEmptyAndCanBeOverwritten()
    {
        var path = PathFor("notifications.json");
        File.WriteAllText(path, "{ not json");

        var store = new NotificationPersistenceStore(path);
        Assert.Empty(store.Load("usr_me").Notifications);

        store.Save("usr_me", new[] { Notification("not_invite", "invite") }, Array.Empty<string>());
        Assert.Single(new NotificationPersistenceStore(path).Load("usr_me").Notifications);
    }

    [Fact]
    public void InviteRequestExposesItsActions()
    {
        var item = new NotificationItem(Notification("not_request", "requestInvite"));

        Assert.True(item.IsPersistent);
        Assert.True(item.CanAcceptInviteRequest);
        Assert.True(item.CanDecline);
        Assert.False(new NotificationItem(Notification("not_friend", "friendRequest")).CanDecline);
    }

    [Fact]
    public async Task InviteRequestAndResponseBodiesUseTheMessageSlot()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);

        await api.UpdateInviteMessageAsync("usr_me", "message", 11, "hello");
        await api.InviteFriendAsync("usr_friend", "wrld_test:123", 11);
        await api.RequestInviteAsync("usr_friend", 11);
        await api.UpdateInviteMessageAsync("usr_me", "requestResponse", 11, "busy");
        await api.RespondInviteAsync("not_invite", 11);
        await api.HideNotificationAsync("not_invite");

        Assert.Equal(
            new[]
            {
                "PUT api/1/message/usr_me/message/11",
                "POST api/1/invite/usr_friend",
                "POST api/1/requestInvite/usr_friend",
                "PUT api/1/message/usr_me/requestResponse/11",
                "POST api/1/invite/not_invite/response",
                "PUT api/1/auth/user/notifications/not_invite/hide"
            },
            handler.Requests.Select(request => request.Path));

        Assert.Equal("hello", handler.Requests[0].Message);
        Assert.Equal("wrld_test:123", handler.Requests[1].InstanceId);
        Assert.Equal(11, handler.Requests[1].MessageSlot);
        Assert.Equal(11, handler.Requests[2].RequestSlot);
        Assert.Equal("busy", handler.Requests[3].Message);
        Assert.Equal(11, handler.Requests[4].ResponseSlot);
    }

    [Fact]
    public async Task ComposerUsesTheWorldFromTheVrChatLogWhenTheApiLocationIsStale()
    {
        var logPath = PathFor("output.log");
        File.WriteAllText(
            logPath,
            "2026.11.15 12:00:00 Log - [Behaviour] Joining wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345\n");
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => logPath, () => true));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "private"
        });

        shell.InviteFriendCommand.Execute(
            new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));

        Assert.True(shell.IsInviteComposerOpen);
        Assert.False(shell.HasInviteComposerLocationHint);
        Assert.Equal("invite", shell.InviteComposerMode);

        shell.SendInviteChoiceCommand.Execute("invite");
        await WaitForComposerAsync(shell);

        Assert.Contains(
            handler.Requests,
            request => request.Path == "POST api/1/invite/usr_friend");
    }

    [Fact]
    public async Task SavedMessagesExposeTheirTextAndCooldown()
    {
        var handler = new CaptureHandler
        {
            MessageListJson = """
                [
                  {"id":"msg_0","message":"Come hang out","messageType":"message","slot":0,"canBeUpdated":true,"remainingCooldownMinutes":0},
                  {"id":"msg_11","message":"test","messageType":"message","slot":11,"canBeUpdated":false,"remainingCooldownMinutes":42}
                ]
                """
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });

        shell.InviteFriendCommand.Execute(
            new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));
        await WaitForSavedMessagesAsync(shell);

        Assert.Equal(12, shell.InviteMessages.Count);
        var custom = shell.InviteMessages.Single(message => message.Slot == 11);
        Assert.Equal("test", custom.Preview);
        Assert.Equal("Ready in 42 min", custom.CooldownText);

        shell.SelectedInviteMessage = custom;
        Assert.Equal("test", shell.InviteComposerMessage);
        Assert.Contains("42", shell.InvitePresetStatus, StringComparison.Ordinal);
        Assert.Contains(
            handler.Requests,
            request => request.Path == "GET api/1/message/usr_me/message");
    }

    [Fact]
    public async Task InvitePresetIsAvailableForARequestAndCopiedToTheRequestPool()
    {
        var handler = new CaptureHandler
        {
            MessageListJson = """
                [{"id":"msg_0","message":"hello how are you doing?","messageType":"message","slot":0,"canBeUpdated":true,"remainingCooldownMinutes":0}]
                """
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });

        shell.InviteFriendCommand.Execute(
            new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));
        await WaitForSavedMessagesAsync(shell);

        shell.InviteComposerMode = "request";
        await WaitForSavedMessagesAsync(shell);

        var shared = shell.InviteMessages.Single(message =>
            message.Message == "hello how are you doing?");
        Assert.Equal("Invite", shared.TypeLabel);
        Assert.Contains("shared", shell.InviteMessagesStatus, StringComparison.OrdinalIgnoreCase);

        shell.SelectedInviteMessage = shared;
        Assert.Equal("hello how are you doing?", shell.InviteComposerMessage);

        shell.SendInviteChoiceCommand.Execute("request");
        await WaitForComposerAsync(shell);

        Assert.Contains(
            handler.Requests,
            request => request.Path == "PUT api/1/message/usr_me/request/11" &&
                       request.Message == "hello how are you doing?");
        var requestInvite = handler.Requests.Single(request =>
            request.Path == "POST api/1/requestInvite/usr_friend");
        Assert.Equal(11, requestInvite.RequestSlot);
    }

    [Fact]
    public async Task EditingASelectedPresetSavesItAndSendsThatSlot()
    {
        var handler = new CaptureHandler
        {
            MessageListJson = """
                [{"id":"msg_3","message":"saved text","messageType":"message","slot":3,"canBeUpdated":true,"remainingCooldownMinutes":0}]
                """
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });

        shell.InviteFriendCommand.Execute(
            new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));
        await WaitForSavedMessagesAsync(shell);
        var preset = shell.InviteMessages.Single(message => message.Slot == 3);
        shell.SelectedInviteMessage = preset;
        shell.InviteComposerMessage = "updated text";

        Assert.True(shell.CanSaveInvitePreset);
        shell.SaveInvitePresetCommand.Execute(null);
        for (var attempt = 0; attempt < 500 && shell.IsBusy; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Contains(
            handler.Requests,
            request => request.Path == "PUT api/1/message/usr_me/message/3");
        Assert.Equal("updated text", shell.SelectedInviteMessage?.Message);
        Assert.Contains("cooling down", shell.InvitePresetStatus, StringComparison.OrdinalIgnoreCase);

        shell.SendInviteChoiceCommand.Execute("invite");
        await WaitForComposerAsync(shell);

        var invite = handler.Requests.Single(request => request.Path == "POST api/1/invite/usr_friend");
        Assert.Equal(3, invite.MessageSlot);
        Assert.Single(
            handler.Requests,
            request => request.Path == "PUT api/1/message/usr_me/message/3");
    }

    [Fact]
    public async Task LockedCustomMessageStillSendsTheInviteWithoutText()
    {
        var handler = new CaptureHandler
        {
            MessageListJson = """
                [{"id":"msg_11","message":"old","messageType":"message","slot":11,"canBeUpdated":false,"remainingCooldownMinutes":42}]
                """
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });

        shell.InviteFriendCommand.Execute(
            new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));
        await WaitForSavedMessagesAsync(shell);
        Assert.True(shell.SelectInviteComposerModeCommand.CanExecute("request"));

        shell.InviteComposerMessage = "hi";
        shell.SendInviteChoiceCommand.Execute("invite");
        await WaitForComposerAsync(shell);

        var edit = handler.Requests.Single(request =>
            request.Path.StartsWith("PUT api/1/message/usr_me/message/", StringComparison.Ordinal));
        Assert.Equal("PUT api/1/message/usr_me/message/10", edit.Path);
        var invite = handler.Requests.Single(request => request.Path == "POST api/1/invite/usr_friend");
        Assert.Equal(10, invite.MessageSlot);
    }

    [Fact]
    public async Task A429FromTheMessageSlotStillSendsTheInvite()
    {
        var handler = new CaptureHandler
        {
            MessageUpdateStatus = HttpStatusCode.TooManyRequests
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });

        shell.InviteFriendCommand.Execute(
            new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));
        shell.InviteComposerMessage = "hi";
        shell.SendInviteChoiceCommand.Execute("invite");
        await WaitForComposerAsync(shell);

        var invite = handler.Requests.Single(request => request.Path == "POST api/1/invite/usr_friend");
        Assert.Equal(0, invite.MessageSlot);
        Assert.Empty(shell.ComposerError);
        Assert.Contains("cooling down", shell.InfoMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RememberedMessageSkipsTheEditAfterARelaunch()
    {
        var memoryPath = PathFor("invite-messages.json");
        var firstRun = new InviteMessageMemoryStore(memoryPath);
        firstRun.Set("usr_me", "request", "hello");

        var reopened = new InviteMessageMemoryStore(memoryPath);
        Assert.Equal("hello", reopened.Get("usr_me", "request"));
        Assert.Equal(11, reopened.GetEntry("usr_me", "request")?.Slot);
        Assert.Null(reopened.Get("usr_me", "message"));
        Assert.Null(reopened.Get("usr_other", "request"));

        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, reopened);
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });

        shell.InviteFriendCommand.Execute(
            new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" }));
        shell.InviteComposerMessage = "hello";
        shell.SendInviteChoiceCommand.Execute("request");
        await WaitForComposerAsync(shell);

        Assert.DoesNotContain(
            handler.Requests,
            request => request.Path == "PUT api/1/message/usr_me/request/11");
        Assert.Contains(
            handler.Requests,
            request => request.Path == "POST api/1/requestInvite/usr_friend");
    }

    [Fact]
    public async Task SavedMessagesAreReadFromTheSignedInAccount()
    {
        var handler = new CaptureHandler
        {
            MessageListJson = """
                [{"id":"msg_11","message":"test","messageType":"message","slot":11,"canBeUpdated":false,"remainingCooldownMinutes":42}]
                """
        };
        using var api = new VrcApiClient(null, handler);

        var messages = await api.GetInviteMessagesAsync("usr_me", "message");

        Assert.Equal("GET api/1/message/usr_me/message", handler.Requests.Single().Path);
        var message = Assert.Single(messages);
        Assert.Equal(11, message.Slot);
        Assert.Equal("test", message.Message);
        Assert.Equal(42, message.RemainingCooldownMinutes);
    }

    [Fact]
    public async Task CustomInviteMessageUsesTheSignedInAccountAndIsReusedAcrossFriends()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });
        var first = new FriendItem(new VrcUser { Id = "usr_first", DisplayName = "First" });
        var second = new FriendItem(new VrcUser { Id = "usr_second", DisplayName = "Second" });

        shell.InviteFriendCommand.Execute(first);
        shell.InviteComposerMessage = "hello";
        shell.SendInviteChoiceCommand.Execute("request");
        await WaitForComposerAsync(shell);

        shell.InviteFriendCommand.Execute(second);
        shell.InviteComposerMessage = "hello";
        shell.SendInviteChoiceCommand.Execute("request");
        await WaitForComposerAsync(shell);

        Assert.Equal(
            new[]
            {
                "PUT api/1/message/usr_me/request/11",
                "POST api/1/requestInvite/usr_first",
                "POST api/1/requestInvite/usr_second"
            },
            handler.Requests
                .Where(request => !request.Path.StartsWith("GET ", StringComparison.Ordinal))
                .Select(request => request.Path));
    }

    [Fact]
    public async Task InvitingWithoutAWorldShowsTheReasonInsideTheComposer()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "private"
        });
        var friend = new FriendItem(new VrcUser { Id = "usr_friend", DisplayName = "Friend" });

        shell.InviteFriendCommand.Execute(friend);
        shell.SendInviteChoiceCommand.Execute("invite");
        for (var attempt = 0; attempt < 500 && shell.IsBusy; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(shell.IsInviteComposerOpen, "A failed invite should leave the composer open.");
        Assert.Contains("not in a world", shell.ComposerError, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(handler.Requests, request => request.Path == "POST api/1/invite/usr_friend");
    }

    [Fact]
    public async Task AcceptInviteRequestUsesTheComposerAndItsPresets()
    {
        var handler = new CaptureHandler
        {
            MessageListJson = """
                [{"id":"msg_0","message":"come join us","messageType":"message","slot":0,"canBeUpdated":true,"remainingCooldownMinutes":0}]
                """
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });
        var request = new NotificationItem(Notification("not_request", "requestInvite"));

        shell.AcceptInviteRequestCommand.Execute(request);
        for (var attempt = 0; attempt < 500 && !shell.IsInviteComposerOpen; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.True(shell.IsInviteComposerOpen);
        Assert.Equal("invite", shell.InviteComposerMode);
        Assert.DoesNotContain(handler.Requests, item => item.Path == "POST api/1/invite/usr_sender");

        await WaitForSavedMessagesAsync(shell);
        var preset = shell.InviteMessages.Single(option => option.Message == "come join us");
        shell.SelectedInviteMessage = preset;
        shell.SendInviteChoiceCommand.Execute("invite");
        await WaitForComposerAsync(shell);

        var invite = handler.Requests.Single(item => item.Path == "POST api/1/invite/usr_sender");
        Assert.Equal(0, invite.MessageSlot);
        Assert.Contains(
            handler.Requests,
            item => item.Path == "PUT api/1/auth/user/notifications/not_request/hide");
    }

    [Fact]
    public async Task DeclineWithMessageUsesTheResponsePresetAndItsSlot()
    {
        var handler = new CaptureHandler
        {
            ResponseMessageListJson = """
                [{"id":"msg_2","message":"sorry, busy right now","messageType":"response","slot":2,"canBeUpdated":true,"remainingCooldownMinutes":0}]
                """
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });
        var invite = new NotificationItem(Notification("not_reply", "invite"));

        shell.DeclineNotificationWithMessageCommand.Execute(invite);
        Assert.True(shell.IsResponseComposerOpen);

        await WaitForSavedMessagesAsync(shell);
        var preset = shell.InviteMessages.Single(option => option.Message == "sorry, busy right now");
        Assert.Equal("response", preset.MessageType);
        shell.SelectedInviteMessage = preset;
        Assert.Equal("sorry, busy right now", shell.ResponseComposerMessage);

        shell.SendDeclineWithMessageCommand.Execute(null);
        for (var attempt = 0; attempt < 500 && shell.IsBusy; attempt++)
        {
            await Task.Delay(20);
        }

        var response = handler.Requests.Single(item => item.Path == "POST api/1/invite/not_reply/response");
        Assert.Equal(2, response.ResponseSlot);
        Assert.DoesNotContain(
            handler.Requests,
            item => item.Path.StartsWith("PUT api/1/message/usr_me/response", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplyComposerShowsInvitePresetsAndCopiesThemIntoTheReplyPool()
    {
        var handler = new CaptureHandler
        {
            MessageListJson = """
                [{"id":"msg_0","message":"hello how are you doing?","messageType":"message","slot":0,"canBeUpdated":true,"remainingCooldownMinutes":0}]
                """
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });
        var invite = new NotificationItem(Notification("not_reply", "invite"));

        shell.DeclineNotificationWithMessageCommand.Execute(invite);
        await WaitForSavedMessagesAsync(shell);

        var preset = shell.InviteMessages.Single(option =>
            option.Message == "hello how are you doing?");
        Assert.Equal("Invite", preset.TypeLabel);
        shell.SelectedInviteMessage = preset;
        Assert.Equal("hello how are you doing?", shell.ResponseComposerMessage);

        shell.SendDeclineWithMessageCommand.Execute(null);
        for (var attempt = 0; attempt < 500 && shell.IsBusy; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Contains(
            handler.Requests,
            item => item.Path == "PUT api/1/message/usr_me/response/11" &&
                    item.Message == "hello how are you doing?");
        var response = handler.Requests.Single(item => item.Path == "POST api/1/invite/not_reply/response");
        Assert.Equal(11, response.ResponseSlot);
    }

    [Fact]
    public void ClearingOneNotificationOrAllOfThemSurvivesAReload()
    {
        var path = PathFor("notifications-clear.json");
        var store = new NotificationPersistenceStore(path);
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseNotificationStore(shell, store, "usr_me");
        TestHooks.SeedNotifications(
            shell,
            new[]
            {
                new NotificationItem(Notification("not_a", "invite")),
                new NotificationItem(Notification("not_b", "requestInvite")),
                new NotificationItem(Notification("not_c", "boop"))
            });

        shell.ClearNotificationCommand.Execute(shell.Notifications[1]);
        Assert.Equal(new[] { "not_a", "not_c" }, shell.Notifications.Select(item => item.Id));

        shell.ClearAllNotificationsCommand.Execute(null);
        Assert.True(shell.IsClearConfirmationOpen);
        Assert.Contains("Clear all 2 notifications", shell.ClearConfirmationText, StringComparison.Ordinal);
        Assert.Equal(2, shell.Notifications.Count);

        shell.ConfirmClearConfirmationCommand.Execute(null);
        Assert.False(shell.IsClearConfirmationOpen);
        Assert.Empty(shell.Notifications);
        Assert.Contains("2 notifications", shell.InfoMessage, StringComparison.OrdinalIgnoreCase);

        // The cleared ids persist, so a relaunch does not bring the rows back.
        var saved = new NotificationPersistenceStore(path).Load("usr_me");
        Assert.Empty(saved.Notifications);
        Assert.Equal(
            new[] { "not_a", "not_b", "not_c" },
            saved.ClearedIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task MarkingOneFilterReadOnlyMarksWhatItShows()
    {
        var path = PathFor("notifications-filter-actions.json");
        var store = new NotificationPersistenceStore(path);
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseNotificationStore(shell, store, "usr_me");
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SeedNotifications(
            shell,
            new[]
            {
                new NotificationItem(Notification("not_invite_a", "invite")),
                new NotificationItem(Notification("not_request", "requestInvite")),
                new NotificationItem(Notification("not_invite_b", "invite")),
                new NotificationItem(Notification("not_boop", "boop"))
            });

        shell.SelectedNotificationFilter = shell.NotificationFilters.Single(option =>
            option.Key == "world-invites");
        Assert.True(shell.HasVisibleNotifications);
        Assert.True(shell.HasUnreadVisibleNotifications);

        shell.MarkFilteredNotificationsReadCommand.Execute(null);
        for (var attempt = 0; attempt < 200 && shell.IsBusy; attempt++)
        {
            await Task.Delay(20);
        }

        Assert.Contains(
            handler.Requests,
            request => request.Path == "PUT api/1/auth/user/notifications/not_invite_a/see");
        Assert.Contains(
            handler.Requests,
            request => request.Path == "PUT api/1/auth/user/notifications/not_invite_b/see");
        Assert.DoesNotContain(
            handler.Requests,
            request => request.Path.Contains("not_boop", StringComparison.Ordinal));
        Assert.Equal(4, shell.Notifications.Count);
    }

    [Fact]
    public void ClearingOneFilterLeavesTheOtherFiltersAlone()
    {
        var path = PathFor("notifications-filter-clear.json");
        var store = new NotificationPersistenceStore(path);
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseNotificationStore(shell, store, "usr_me");
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SeedNotifications(
            shell,
            new[]
            {
                new NotificationItem(Notification("not_invite_a", "invite")),
                new NotificationItem(Notification("not_request", "requestInvite")),
                new NotificationItem(Notification("not_invite_b", "invite")),
                new NotificationItem(Notification("not_boop", "boop"))
            });

        shell.SelectedNotificationFilter = shell.NotificationFilters.Single(option =>
            option.Key == "world-invites");
        Assert.True(shell.HasVisibleNotifications);

        var raised = 0;
        shell.ClearFilteredNotificationsCommand.CanExecuteChanged += (_, _) => raised++;

        shell.ClearFilteredNotificationsCommand.Execute(null);
        Assert.True(shell.IsClearConfirmationOpen);
        Assert.Contains("in this filter", shell.ClearConfirmationText, StringComparison.Ordinal);
        Assert.Equal(4, shell.Notifications.Count);

        shell.ConfirmClearConfirmationCommand.Execute(null);
        Assert.False(shell.IsClearConfirmationOpen);
        Assert.Equal(
            new[] { "not_request", "not_boop" },
            shell.Notifications.Select(item => item.Id));

        // Switching filters must re-enable the filter buttons on its own: before,
        // the command state was only refreshed by a clear.
        raised = 0;
        shell.SelectedNotificationFilter = shell.NotificationFilters.Single(option =>
            option.Key == "invite-requests");
        Assert.True(raised > 0, "Changing the filter should refresh the filter commands.");
        Assert.True(shell.HasVisibleNotifications);
        Assert.True(shell.ClearFilteredNotificationsCommand.CanExecute(null));

        shell.ClearFilteredNotificationsCommand.Execute(null);
        Assert.True(shell.IsClearConfirmationOpen);
        shell.CancelClearConfirmationCommand.Execute(null);
        Assert.False(shell.IsClearConfirmationOpen);
        Assert.Equal(
            new[] { "not_request", "not_boop" },
            shell.Notifications.Select(item => item.Id));

        // The header's Clear all is untouched and still means every row.
        shell.ClearAllNotificationsCommand.Execute(null);
        shell.ConfirmClearConfirmationCommand.Execute(null);
        Assert.Empty(shell.Notifications);

        var saved = new NotificationPersistenceStore(path).Load("usr_me");
        Assert.Empty(saved.Notifications);
        Assert.Equal(
            new[] { "not_boop", "not_invite_a", "not_invite_b", "not_request" },
            saved.ClearedIds.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public async Task DeclineWithMessageDropsAnInviteVrChatHasExpired()
    {
        var handler = new CaptureHandler
        {
            ResponseMessageListJson = """
                [{"id":"msg_2","message":"sorry, busy right now","messageType":"response","slot":2,"canBeUpdated":true,"remainingCooldownMinutes":0}]
                """,
            ResponseStatus = HttpStatusCode.NotFound
        };
        using var api = new VrcApiClient(null, handler);
        using var shell = new MainViewModel(api);
        TestHooks.UseLogPlayer(shell, new VrChatLogPlayerService(() => null, () => false));
        TestHooks.UseInviteMessageMemory(shell, new InviteMessageMemoryStore(PathFor("invite-messages.json")));
        TestHooks.SetLoggedIn(shell);
        TestHooks.SetCurrentUser(shell, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Location = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"
        });
        var invite = new NotificationItem(Notification("not_expired", "invite"));

        shell.DeclineNotificationWithMessageCommand.Execute(invite);
        Assert.True(shell.IsResponseComposerOpen);
        await WaitForSavedMessagesAsync(shell);
        shell.SelectedInviteMessage = shell.InviteMessages.Single(option =>
            option.Message == "sorry, busy right now");

        shell.SendDeclineWithMessageCommand.Execute(null);
        for (var attempt = 0; attempt < 500 && shell.IsBusy; attempt++)
        {
            await Task.Delay(20);
        }

        // The reply goes to the documented route, and the server's "Notification
        // not found" is reported as an expiry instead of a mystery failure.
        Assert.Contains(
            handler.Requests,
            item => item.Path == "POST api/1/invite/not_expired/response");
        Assert.False(shell.IsResponseComposerOpen);
        Assert.DoesNotContain(invite, shell.Notifications);
        Assert.Contains("expired", shell.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not sent", shell.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForSavedMessagesAsync(MainViewModel shell)
    {
        for (var attempt = 0;
             attempt < 500 && (shell.IsInviteMessagesLoading || shell.InviteMessages.Count == 0);
             attempt++)
        {
            await Task.Delay(20);
        }

        Assert.False(shell.IsInviteMessagesLoading, "Saved messages should finish loading.");
    }

    private static async Task WaitForComposerAsync(MainViewModel shell)
    {
        for (var attempt = 0; attempt < 500 && (shell.IsBusy || shell.IsInviteComposerOpen); attempt++)
        {
            await Task.Delay(20);
        }

        Assert.False(shell.IsBusy, "The invite request should finish.");
        Assert.False(shell.IsInviteComposerOpen, $"The composer should close after sending. {shell.ComposerError}");
    }

    private static VrcNotification Notification(string id, string type)
    {
        using var details = JsonDocument.Parse(
            "{\"worldId\":\"wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345\",\"worldName\":\"The Black Cat\"}");
        return new VrcNotification
        {
            Id = id,
            Type = type,
            SenderUserId = "usr_sender",
            SenderUsername = "Sender",
            Details = details.RootElement.Clone()
        };
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = new();
        public string MessageListJson { get; set; } = "[]";
        public string RequestMessageListJson { get; set; } = "[]";
        public string ResponseMessageListJson { get; set; } = "[]";
        public HttpStatusCode MessageUpdateStatus { get; set; } = HttpStatusCode.OK;

        /// <summary>Set to fail the invite response with VRChat's expiry error.</summary>
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body.Length == 0 ? "{}" : body);
            var root = document.RootElement;
            Requests.Add(new CapturedRequest
            {
                Path = $"{request.Method.Method} {request.RequestUri?.AbsolutePath.TrimStart('/')}",
                Message = String(root, "message"),
                InstanceId = String(root, "instanceId"),
                MessageSlot = Number(root, "messageSlot"),
                RequestSlot = Number(root, "requestSlot"),
                ResponseSlot = Number(root, "responseSlot")
            });

            if (request.Method == HttpMethod.Put &&
                (request.RequestUri?.AbsolutePath.Contains("/message/", StringComparison.Ordinal) ?? false) &&
                MessageUpdateStatus != HttpStatusCode.OK)
            {
                return new HttpResponseMessage(MessageUpdateStatus)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"Too Fast\",\"status_code\":429}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var isMessageList = request.Method == HttpMethod.Get &&
                                (request.RequestUri?.AbsolutePath.Contains("/message/", StringComparison.Ordinal) ?? false);
            var path = request.RequestUri!.AbsolutePath;
            if (ResponseStatus != HttpStatusCode.OK &&
                request.Method == HttpMethod.Post &&
                path.EndsWith("/response", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(ResponseStatus)
                {
                    Content = new StringContent(
                        "{\"error\":{\"message\":\"Notification not found\",\"status_code\":404}}",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var responseBody = isMessageList
                ? path.EndsWith("/request", StringComparison.Ordinal)
                    ? RequestMessageListJson
                    : path.EndsWith("/response", StringComparison.Ordinal) ||
                      path.EndsWith("/requestResponse", StringComparison.Ordinal)
                        ? ResponseMessageListJson
                        : MessageListJson
                : "{}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }

        private static string? String(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static int? Number(JsonElement root, string name)
            => root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)
                ? number
                : null;
    }

    private sealed class CapturedRequest
    {
        public string Path { get; init; } = string.Empty;
        public string? Message { get; init; }
        public string? InstanceId { get; init; }
        public int? MessageSlot { get; init; }
        public int? RequestSlot { get; init; }
        public int? ResponseSlot { get; init; }
    }
}
