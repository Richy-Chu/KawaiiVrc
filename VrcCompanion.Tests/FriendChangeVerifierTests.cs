using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class FriendChangeVerifierTests
{
    [Fact]
    public async Task KeepsOnlyChangesConfirmedByFriendStatus()
    {
        var added = Friend("usr_added", "Added");
        var removed = Friend("usr_removed", "Removed");
        var statuses = new Dictionary<string, VrcFriendStatus>
        {
            [added.Id] = new() { IsFriend = false },
            [removed.Id] = new() { IsFriend = true }
        };

        var verified = await FriendChangeVerifier.VerifyAsync(
            new FriendListDelta(new[] { added }, new[] { removed }),
            (id, _) => Task.FromResult<VrcFriendStatus?>(statuses[id]));

        Assert.Empty(verified.Added);
        Assert.Empty(verified.Removed);
    }

    [Fact]
    public async Task ConfirmsARealAdditionAndRemoval()
    {
        var added = Friend("usr_added", "Added");
        var removed = Friend("usr_removed", "Removed");
        var statuses = new Dictionary<string, VrcFriendStatus>
        {
            [added.Id] = new() { IsFriend = true },
            [removed.Id] = new() { IsFriend = false }
        };

        var verified = await FriendChangeVerifier.VerifyAsync(
            new FriendListDelta(new[] { added }, new[] { removed }),
            (id, _) => Task.FromResult<VrcFriendStatus?>(statuses[id]));

        Assert.Equal(added.Id, Assert.Single(verified.Added).Id);
        Assert.Equal(removed.Id, Assert.Single(verified.Removed).Id);
    }

    [Fact]
    public async Task ATemporaryStatusFailureDoesNotInventAChange()
    {
        var added = Friend("usr_added", "Added");
        var removed = Friend("usr_removed", "Removed");

        var verified = await FriendChangeVerifier.VerifyAsync(
            new FriendListDelta(new[] { added }, new[] { removed }),
            (_, _) => throw new HttpRequestException("offline"));

        Assert.Empty(verified.Added);
        Assert.Empty(verified.Removed);
    }

    [Fact]
    public async Task A404ConfirmsThePersonIsNoLongerAFriend()
    {
        var removed = Friend("usr_removed", "Removed");

        var verified = await FriendChangeVerifier.VerifyAsync(
            new FriendListDelta(Array.Empty<VrcUser>(), new[] { removed }),
            (_, _) => throw new VrcApiException(404, "missing"));

        Assert.Equal(removed.Id, Assert.Single(verified.Removed).Id);
    }

    [Fact]
    public async Task StatusChecksRunConcurrentlyInsteadOfOneAtATime()
    {
        var friends = Enumerable.Range(0, 8)
            .Select(index => Friend($"usr_{index}", $"Friend {index}"))
            .ToArray();
        var started = 0;
        var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var verified = await FriendChangeVerifier.VerifyAsync(
            new FriendListDelta(friends, Array.Empty<VrcUser>()),
            async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref started) >= 4)
                {
                    fourStarted.TrySetResult();
                }

                // A serial implementation deadlocks here: the first call would
                // wait for three sibling calls that have not started yet.
                await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
                return new VrcFriendStatus { IsFriend = true };
            });

        Assert.Equal(8, verified.Added.Count);
    }

    private static VrcUser Friend(string id, string name)
        => new() { Id = id, DisplayName = name, Username = id, IsFriend = true };
}
