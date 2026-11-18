using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class VrChatRealtimeTests
{
    [Fact]
    public void ParsesNestedJsonContentIntoTypedFields()
    {
        var parsed = VrChatRealtimeMessage.TryParse(
            "{\"type\":\"friend-location\",\"content\":\"{\\\"userId\\\":\\\"usr_1\\\",\\\"location\\\":\\\"wrld_1:2\\\"}\"}",
            DateTimeOffset.Now,
            out var message);

        Assert.True(parsed);
        Assert.NotNull(message);
        Assert.Equal("friend-location", message!.Type);
        Assert.Equal("usr_1", message.Content.GetProperty("userId").GetString());
        Assert.Equal("wrld_1:2", message.Content.GetProperty("location").GetString());
    }

    [Fact]
    public void KeepsNonJsonContentAsAString()
    {
        var parsed = VrChatRealtimeMessage.TryParse(
            "{\"type\":\"notification\",\"content\":\"hello\"}",
            DateTimeOffset.Now,
            out var message);

        Assert.True(parsed);
        Assert.Equal(JsonValueKind.String, message!.Content.ValueKind);
        Assert.Equal("hello", message.Content.GetString());
    }

    [Fact]
    public void RejectsMessagesWithoutAStringType()
    {
        Assert.False(VrChatRealtimeMessage.TryParse("{\"content\":{}}", DateTimeOffset.Now, out _));
        Assert.False(VrChatRealtimeMessage.TryParse(
            "{\"type\":1,\"content\":{}}",
            DateTimeOffset.Now,
            out _));
        Assert.False(VrChatRealtimeMessage.TryParse("not json", DateTimeOffset.Now, out _));
    }

    [Fact]
    public void BuildsTheAuthenticatedPipelineUrl()
    {
        Assert.Equal(
            "wss://pipeline.vrchat.cloud/?auth=token%20value",
            VrChatRealtimeService.BuildUrl("token value"));
        Assert.Throws<ArgumentException>(() => VrChatRealtimeService.BuildUrl(" "));
    }

    [Fact]
    public async Task BootstrapsARealtimeTokenFromTheSession()
    {
        var handler = new TokenHandler("{\"ok\":true,\"token\":\"abc\"}");
        using var api = new VrcApiClient(null, handler);

        var token = await api.GetRealtimeTokenAsync();

        Assert.Equal("abc", token);
        Assert.Equal("/api/1/auth", handler.Path);
    }

    [Fact]
    public async Task RejectsAnUnsuccessfulTokenBootstrap()
    {
        var handler = new TokenHandler("{\"ok\":false,\"token\":\"abc\"}");
        using var api = new VrcApiClient(null, handler);

        await Assert.ThrowsAsync<VrcApiException>(() => api.GetRealtimeTokenAsync());
    }

    [Fact]
    public void QuietConnectionWithHeartbeatRepliesStaysAlive()
    {
        var start = DateTimeOffset.Parse("2026-11-14T12:00:00Z");
        var lastInbound = start;

        for (var beat = 1; beat <= 10; beat++)
        {
            var now = start.AddSeconds(30 * beat);
            Assert.False(VrChatRealtimeLiveness.IsExpired(lastInbound, now));

            // A pong (or any other frame) refreshes the liveness deadline.
            lastInbound = now;
        }
    }

    [Fact]
    public void SilentlyDeadConnectionExpiresAfterThreeHeartbeats()
    {
        var start = DateTimeOffset.Parse("2026-11-14T12:00:00Z");

        Assert.False(VrChatRealtimeLiveness.IsExpired(start, start.AddSeconds(60)));
        Assert.True(VrChatRealtimeLiveness.IsExpired(start, start.AddSeconds(90)));
        Assert.True(VrChatRealtimeLiveness.IsExpired(start, start.AddSeconds(120)));
    }

    private sealed class TokenHandler : HttpMessageHandler
    {
        private readonly string _body;

        public TokenHandler(string body) => _body = body;

        public string Path { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Path = request.RequestUri?.AbsolutePath ?? string.Empty;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
