using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class AvatarWearTests
{
    [Fact]
    public async Task WearDoesNotWaitBehindBlockedBackgroundApiTraffic()
    {
        var handler = new BlockingWorldHandler();
        using var api = new VrcApiClient(null, handler);

        var background = api.GetWorldAsync("wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd");
        await handler.WorldStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var wear = api.SetCurrentAvatarAsync("avtr_fast");
        await wear.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(
            "PUT /api/1/avatars/avtr_fast/select",
            handler.Requests);
        Assert.Contains(
            handler.Bodies,
            body => body.Contains("\"avatarId\":\"avtr_fast\"", StringComparison.Ordinal));
        Assert.False(background.IsCompleted);

        handler.ReleaseWorld.TrySetResult();
        await background.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class BlockingWorldHandler : HttpMessageHandler
    {
        public TaskCompletionSource WorldStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseWorld { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<string> Requests { get; } = new();

        public ConcurrentQueue<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue($"{request.Method} {request.RequestUri?.AbsolutePath}");
            if (request.Content is not null)
            {
                Bodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            if (request.RequestUri?.AbsolutePath.Contains("/worlds/") == true)
            {
                WorldStarted.TrySetResult();
                await ReleaseWorld.Task.WaitAsync(cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
