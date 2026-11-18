using System.Text.Json;
using VrcCompanion.Models;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class NotificationInviteTests
{
    private const string WorldId = "wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b";
    private const string InstanceId = "96583~private(usr_9439f8cc-1c6b-4dca-9a07-d2eccb570701)~region(use)";

    [Fact]
    public void RestInviteUsesTheCombinedWorldAndInstanceLocation()
    {
        var item = Item(Parse(
            $$"""{"worldId":"{{WorldId}}:{{InstanceId}}","worldName":"The Black Cat"}"""));

        Assert.True(item.CanJoinInvite);
        Assert.Equal($"{WorldId}:{InstanceId}", item.InviteLocation);
        Assert.Equal("The Black Cat", item.InviteWorldName);
    }

    [Fact]
    public void JsonEncodedInviteDetailsAreUnpacked()
    {
        var details = JsonSerializer.Serialize(
            $$"""{"worldId":"{{WorldId}}:{{InstanceId}}","worldName":"The Black Cat"}""");
        var item = Item(Parse(details));

        Assert.True(item.CanJoinInvite);
        Assert.Equal($"{WorldId}:{InstanceId}", item.InviteLocation);
    }

    [Fact]
    public void SplitWorldAndInstanceFieldsStillWork()
    {
        var item = Item(Parse(
            $$"""{"worldId":"{{WorldId}}","instanceId":"{{InstanceId}}","worldName":"The Black Cat"}"""));

        Assert.True(item.CanJoinInvite);
        Assert.Equal($"{WorldId}:{InstanceId}", item.InviteLocation);
    }

    [Fact]
    public void NonInviteNotificationDoesNotOfferJoin()
    {
        var item = Item(Parse(
            """{"worldId":"wrld_4cf554b4-430c-4f8f-b53e-1f294eed230b:12345"}"""),
            type: "friendRequest");

        Assert.False(item.CanJoinInvite);
        Assert.Null(item.InviteLocation);
    }

    private static NotificationItem Item(JsonElement details, string type = "invite")
        => new(new VrcNotification
        {
            Id = "not_test",
            Type = type,
            Details = details
        });

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
