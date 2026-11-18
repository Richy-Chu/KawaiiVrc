using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// A friend-list refresh can be incomplete or stale. Before the app reports a
/// friendship change it asks VRChat's friendStatus endpoint, which is
/// authoritative. A list diff alone must never create a false "friended" or
/// "unfriended" activity row.
/// </summary>
public static class FriendChangeVerifier
{
    // friendStatus is a small, independent endpoint call. A handful of these
    // may run together without hammering VRChat or serializing the refresh.
    private const int MaxConcurrentChecks = 4;

    public static Task<FriendListDelta> VerifyAsync(
        FriendListDelta delta,
        VrcApiClient api,
        CancellationToken cancellationToken = default)
        => VerifyAsync(delta, api.GetFriendStatusAsync, cancellationToken);

    public static async Task<FriendListDelta> VerifyAsync(
        FriendListDelta delta,
        Func<string, CancellationToken, Task<VrcFriendStatus?>> getStatus,
        CancellationToken cancellationToken = default)
    {
        using var gate = new SemaphoreSlim(MaxConcurrentChecks, MaxConcurrentChecks);

        var addedTasks = delta.Added
            .Select(friend => CheckAddedAsync(friend, getStatus, gate, cancellationToken))
            .ToArray();
        var removedTasks = delta.Removed
            .Select(friend => CheckRemovedAsync(friend, getStatus, gate, cancellationToken))
            .ToArray();

        var added = (await Task.WhenAll(addedTasks).ConfigureAwait(false))
            .Where(friend => friend is not null)
            .Select(friend => friend!)
            .ToList();
        var removed = (await Task.WhenAll(removedTasks).ConfigureAwait(false))
            .Where(friend => friend is not null)
            .Select(friend => friend!)
            .ToList();

        return new FriendListDelta(added, removed)
        {
            IsComplete = added.Count == delta.Added.Count && removed.Count == delta.Removed.Count,
            DetectedAfterRelaunch = delta.DetectedAfterRelaunch
        };
    }

    private static async Task<VrcUser?> CheckAddedAsync(
        VrcUser friend,
        Func<string, CancellationToken, Task<VrcFriendStatus?>> getStatus,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(friend.Id))
        {
            return null;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await getStatus(friend.Id, cancellationToken).ConfigureAwait(false);
            return status?.IsFriend == true ? friend : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Missing accounts, rate limits and transient failures must not
            // invent a change.
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<VrcUser?> CheckRemovedAsync(
        VrcUser friend,
        Func<string, CancellationToken, Task<VrcFriendStatus?>> getStatus,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(friend.Id))
        {
            return null;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var status = await getStatus(friend.Id, cancellationToken).ConfigureAwait(false);
            return status is not null && !status.IsFriend ? friend : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (VrcApiException ex) when (ex.StatusCode == 404)
        {
            return friend;
        }
        catch (Exception)
        {
            // Keep the old friendship when VRChat cannot answer.
            return null;
        }
        finally
        {
            gate.Release();
        }
    }
}
