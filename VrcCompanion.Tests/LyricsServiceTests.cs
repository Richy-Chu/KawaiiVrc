using System.Net;
using System.Net.Http;
using System.Text;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class LyricsServiceTests
{
    [Fact]
    public async Task TemporaryFailureIsNotCachedAndTheNextAttemptRecovers()
    {
        var handler = new SequenceHandler();
        using var http = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://lrclib.net")
        };
        var service = new LyricsService(http);

        var first = await service.GetForTrackAsync(
            "Ain't My Fault",
            "Zara Larsson",
            duration: TimeSpan.FromSeconds(223));
        var second = await service.GetForTrackAsync(
            "Ain't My Fault",
            "Zara Larsson",
            duration: TimeSpan.FromSeconds(223));

        Assert.True(first.TemporaryFailure);
        Assert.Null(first.Lyrics);
        Assert.False(second.TemporaryFailure);
        Assert.NotNull(second.Lyrics);
        Assert.Equal(2, handler.Calls);
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            const string json = """
                {
                  "duration": 223.0,
                  "instrumental": false,
                  "plainLyrics": null,
                  "syncedLyrics": "[00:01.00]Line one\n[00:05.00]Line two"
                }
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
