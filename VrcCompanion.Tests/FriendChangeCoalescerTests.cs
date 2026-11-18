using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class FriendChangeCoalescerTests
{
    [Fact]
    public async Task NewerSnapshotDuringVerificationStillPublishesTheVerifiedChange()
    {
        var added = Friend("usr_added", "Added");
        var another = Friend("usr_another", "Another");
        var verifiedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseVerification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var published = new TaskCompletionSource<FriendListDelta>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var coalescer = new FriendChangeCoalescer(
            async (delta, cancellationToken) =>
            {
                if (delta.Added.Any(friend => friend.Id == added.Id))
                {
                    verifiedStarted.TrySetResult();
                    // Hold the first verification open while a newer snapshot
                    // is queued, reproducing the production race.
                    await releaseVerification.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                }

                return delta;
            },
            delta =>
            {
                if (delta.Added.Any(friend => friend.Id == added.Id))
                {
                    published.TrySetResult(delta);
                }

                return Task.CompletedTask;
            },
            debounce: TimeSpan.Zero);

        coalescer.Queue(Array.Empty<VrcUser>());
        await coalescer.WaitForIdleAsync();

        coalescer.Queue(new[] { added });
        await verifiedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        // This arrives before the first verification has returned. It must not
        // prevent the already verified addition from being published.
        coalescer.Queue(new[] { added, another });
        releaseVerification.TrySetResult();

        var change = await published.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Contains(change.Added, friend => friend.Id == added.Id);

        await coalescer.WaitForIdleAsync();
    }

    private static VrcUser Friend(string id, string name)
        => new() { Id = id, DisplayName = name, Username = id, IsFriend = true };
}