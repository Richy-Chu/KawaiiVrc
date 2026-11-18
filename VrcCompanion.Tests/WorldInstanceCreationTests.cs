using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class WorldInstanceCreationTests
{
    [Fact]
    public async Task NonPublicInstancesSendTheSignedInUserAsOwner()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser
        {
            Id = "usr_me",
            DisplayName = "Me",
            Username = "me"
        });

        var instance = await api.CreateInstanceAsync(
            "wrld_123",
            "hidden",
            "us",
            canRequestInvite: false);

        Assert.Equal("usr_me", handler.OwnerId);
        Assert.Equal("hidden", handler.Type);
        Assert.Equal("wrld_123:123~hidden(usr_me)", instance.Location);
    }

    [Fact]
    public async Task PublicInstancesDoNotSendAnOwnerId()
    {
        var handler = new CaptureHandler();
        using var api = new VrcApiClient(null, handler);
        TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });

        await api.CreateInstanceAsync("wrld_123", "public", "eu", canRequestInvite: false);

        Assert.Null(handler.OwnerId);
        Assert.Equal("public", handler.Type);
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? OwnerId { get; private set; }
        public string? Type { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var json = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using (var document = JsonDocument.Parse(json))
            {
                var root = document.RootElement;
                Type = root.GetProperty("type").GetString();
                OwnerId = root.TryGetProperty("ownerId", out var owner)
                    ? owner.GetString()
                    : null;
            }

            var instanceId = Type == "public"
                ? "123~region(eu)"
                : $"123~hidden({OwnerId})";
            var body = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["instanceId"] = instanceId,
                ["location"] = $"wrld_123:{instanceId}",
                ["worldId"] = "wrld_123"
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    body,
                    System.Text.Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
