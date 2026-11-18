using System.Diagnostics;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Serializes friend-list snapshots into one background verification pass. The
/// snapshot is compared with a baseline, verified against friendStatus, then
/// published before the baseline advances. A newer snapshot arriving mid-flight
/// is queued, not allowed to erase an already verified change.
/// </summary>
public sealed class FriendChangeCoalescer : IDisposable
{
    private readonly Func<FriendListDelta, CancellationToken, Task<FriendListDelta>> _verify;
    private readonly Func<FriendListDelta, Task> _publish;
    private readonly TimeSpan _debounce;
    private readonly Action<Exception>? _log;
    private readonly object _sync = new();

    private Dictionary<string, VrcUser>? _baseline;
    private IReadOnlyList<VrcUser>? _pending;
    private bool _running;
    private bool _disposed;
    private bool _baselineRestored;
    private string? _accountId;
    private int _generation;
    private Task? _loopTask;

    /// <summary>
    /// Raised only after a full snapshot has been accepted, either as the
    /// initial baseline or after every candidate change was verified. The
    /// persisted baseline must never advance past an unverified change.
    /// </summary>
    public event Action<string?, IReadOnlyList<VrcUser>>? SnapshotAccepted;

    public FriendChangeCoalescer(
        VrcApiClient api,
        Func<FriendListDelta, Task> publish,
        TimeSpan? debounce = null)
        : this(
            (delta, cancellationToken) => FriendChangeVerifier.VerifyAsync(delta, api, cancellationToken),
            publish,
            debounce,
            ex => Debug.WriteLine($"friend change verification: {ex}"))
    {
    }

    internal FriendChangeCoalescer(
        Func<FriendListDelta, CancellationToken, Task<FriendListDelta>> verify,
        Func<FriendListDelta, Task> publish,
        TimeSpan? debounce = null,
        Action<Exception>? log = null)
    {
        _verify = verify;
        _publish = publish;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(250);
        _log = log;
    }

    /// <summary>Queues a full friend snapshot, replacing any older queued one.</summary>
    public void Queue(IReadOnlyList<VrcUser> friends)
    {
        if (friends is null)
        {
            return;
        }

        var snapshot = friends.ToArray();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _pending = snapshot;
            if (_running)
            {
                return;
            }

            _running = true;
            _loopTask = RunLoopAsync();
            _ = _loopTask;
        }
    }

    /// <summary>Associates accepted snapshots with the account they belong to.</summary>
    public void SetAccount(string? accountId)
    {
        lock (_sync)
        {
            _accountId = string.IsNullOrWhiteSpace(accountId) ? null : accountId;
        }
    }

    /// <summary>
    /// Seeds the detector with the last complete friend list seen for this
    /// account, so the first refresh after a relaunch can verify changes that
    /// happened while the app was closed.
    /// </summary>
    public void SeedBaseline(IEnumerable<VrcUser> friends)
    {
        var baseline = friends
            .Where(friend => !string.IsNullOrWhiteSpace(friend.Id))
            .GroupBy(friend => friend.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        lock (_sync)
        {
            if (!_disposed)
            {
                _baseline = baseline;
                _baselineRestored = true;
            }
        }
    }

    /// <summary>Records an explicit local friendship change without a list refresh.</summary>
    public void MarkFriend(VrcUser friend)
    {
        if (string.IsNullOrWhiteSpace(friend.Id))
        {
            return;
        }

        lock (_sync)
        {
            _baseline ??= new Dictionary<string, VrcUser>(StringComparer.Ordinal);
            _baseline[friend.Id] = friend;
        }
    }

    public void RemoveFriend(string userId)
    {
        lock (_sync)
        {
            _baseline?.Remove(userId);
        }
    }

    /// <summary>Drops the account baseline and discards in-flight results.</summary>
    public void Reset()
    {
        lock (_sync)
        {
            _generation++;
            _pending = null;
            _baseline = null;
            _baselineRestored = false;
            _accountId = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            ResetCore();
        }
    }

    internal Task WaitForIdleAsync()
    {
        lock (_sync)
        {
            return _loopTask ?? Task.CompletedTask;
        }
    }

    private void ResetCore()
    {
        _generation++;
        _pending = null;
        _baseline = null;
        _baselineRestored = false;
        _accountId = null;
    }

    private async Task RunLoopAsync()
    {
        try
        {
            while (true)
            {
                // Never run the loop synchronously under the Queue lock.
                await Task.Yield();

                if (_debounce > TimeSpan.Zero)
                {
                    await Task.Delay(_debounce).ConfigureAwait(false);
                }

                IReadOnlyList<VrcUser> snapshot;
                int generation;
                lock (_sync)
                {
                    if (_disposed || _pending is null)
                    {
                        return;
                    }

                    snapshot = _pending;
                    _pending = null;
                    generation = _generation;
                }

                FriendListDelta? verified;
                try
                {
                    verified = await VerifyAsync(snapshot, generation).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.Invoke(ex);
                    continue;
                }

                if (verified is null ||
                    (verified.Added.Count == 0 && verified.Removed.Count == 0))
                {
                    continue;
                }

                // Publish to the UI first. Only after that succeeds may the
                // baseline advance, so a superseding snapshot cannot suppress
                // a change that was already verified.
                try
                {
                    await _publish(verified).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log?.Invoke(ex);
                    continue;
                }

                if (Commit(verified, generation))
                {
                    if (verified.IsComplete)
                    {
                        lock (_sync)
                        {
                            if (generation == _generation)
                            {
                                _baselineRestored = false;
                            }
                        }

                        AcceptSnapshot(snapshot, generation);
                    }
                }
            }
        }
        finally
        {
            Task? restart = null;
            lock (_sync)
            {
                _running = false;
                if (!_disposed && _pending is not null)
                {
                    _running = true;
                    restart = RunLoopAsync();
                    _loopTask = restart;
                }
            }

            if (restart is not null)
            {
                _ = restart;
            }
        }
    }

    private async Task<FriendListDelta?> VerifyAsync(
        IReadOnlyList<VrcUser> friends,
        int generation)
    {
        var current = friends
            .Where(friend => !string.IsNullOrWhiteSpace(friend.Id))
            .GroupBy(friend => friend.Id, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        Dictionary<string, VrcUser> baseline;
        bool detectedAfterRelaunch;
        lock (_sync)
        {
            if (generation != _generation || _disposed)
            {
                return null;
            }

            detectedAfterRelaunch = _baselineRestored;
            if (_baseline is null)
            {
                _baseline = current;
                baseline = new Dictionary<string, VrcUser>(current, StringComparer.Ordinal);
            }
            else
            {
                // Verification compares on a worker thread. Copy the dictionary
                // under the lock so MarkFriend/RemoveFriend cannot mutate it while
                // it is being enumerated.
                baseline = new Dictionary<string, VrcUser>(_baseline, StringComparer.Ordinal);
            }
        }

        if (baseline.Count == current.Count &&
            current.Values.All(friend => baseline.ContainsKey(friend.Id)))
        {
            // This snapshot is already the baseline (including a fresh one).
            // Accepting it persists the current profile details without
            // pretending the whole list was just friended.
            lock (_sync)
            {
                if (generation == _generation && !_disposed)
                {
                    _baselineRestored = false;
                }
            }

            AcceptSnapshot(friends, generation);
            return null;
        }

        var added = current.Values
            .Where(friend => !baseline.ContainsKey(friend.Id))
            .ToList();
        var removed = baseline.Values
            .Where(friend => !current.ContainsKey(friend.Id))
            .ToList();

        if (added.Count == 0 && removed.Count == 0)
        {
            var accepted = false;
            lock (_sync)
            {
                if (generation == _generation && !_disposed)
                {
                    foreach (var friend in current.Values)
                    {
                        _baseline![friend.Id] = friend;
                    }
                    accepted = true;
                }
            }

            if (accepted)
            {
                AcceptSnapshot(friends, generation);
            }

            return null;
        }

        var verified = await _verify(
            new FriendListDelta(added, removed)
            {
                DetectedAfterRelaunch = detectedAfterRelaunch
            },
            CancellationToken.None).ConfigureAwait(false);

        lock (_sync)
        {
            if (generation != _generation || _disposed)
            {
                return null;
            }
        }

        return verified;
    }

    private bool Commit(FriendListDelta verified, int generation)
    {
        lock (_sync)
        {
            if (generation != _generation || _disposed || _baseline is null)
            {
                return false;
            }

            foreach (var friend in verified.Added)
            {
                _baseline[friend.Id] = friend;
            }

            foreach (var friend in verified.Removed)
            {
                _baseline.Remove(friend.Id);
            }

            return true;
        }
    }

    private void AcceptSnapshot(IReadOnlyList<VrcUser> friends, int generation)
    {
        string? accountId;
        lock (_sync)
        {
            if (generation != _generation || _disposed)
            {
                return;
            }

            accountId = _accountId;
        }

        SnapshotAccepted?.Invoke(accountId, friends);
    }
}