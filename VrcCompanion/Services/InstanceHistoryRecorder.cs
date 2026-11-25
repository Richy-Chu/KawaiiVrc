using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Turns live VRChat log snapshots into durable join/leave events. This follows
/// VRCX-0's model: the database stores observations, not a mutable session
/// object, and row durations/visits are rebuilt from those observations.
/// </summary>
public sealed class InstanceHistoryRecorder
{
    private readonly InstanceHistoryStore _store;
    private string _accountUserId = string.Empty;
    private string _accountDisplayName = string.Empty;
    private string _accountAvatarUrl = string.Empty;
    private string _activeEntryId = string.Empty;
    private string _activeLocation = string.Empty;

    // Observe now runs on a thread-pool thread, while the account can be set and the recorder
    // stopped from the dispatcher. Both move the same session fields, so they take turns.
    private readonly object _gate = new();

    public InstanceHistoryRecorder(InstanceHistoryStore store)
    {
        _store = store;
    }

    public InstanceHistoryStore Store => _store;

    public void SetAccount(VrcUser user)
    {
        lock (_gate)
        {
            SetAccountCore(user);
        }
    }

    private void SetAccountCore(VrcUser user)
    {
        var changed = !string.Equals(_accountUserId, user.Id, StringComparison.Ordinal);
        _accountUserId = user.Id ?? string.Empty;
        _accountDisplayName = user.DisplayName ?? string.Empty;
        _accountAvatarUrl = ImageUrlHelper.PickAvatarThumbnailUrl(
            user.UserIcon,
            user.ProfilePicOverrideThumbnail ?? user.IconUrl,
            user.CurrentAvatarThumbnailImageUrl,
            user.CurrentAvatarImageUrl,
            256) ?? string.Empty;

        if (changed)
        {
            _activeEntryId = string.Empty;
            _activeLocation = string.Empty;
            CloseStaleRows(_accountUserId);
        }
    }

    /// <summary>
    /// Records the current log state. Returns true when the account changed
    /// instances (the history list needs a refresh), not on every player tick.
    /// </summary>
    public bool Observe(
        VrcCompanion.Services.VrChatLogSnapshot? snapshot,
        bool gameRunning,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            return ObserveCore(snapshot, gameRunning, now);
        }
    }

    private bool ObserveCore(
        VrcCompanion.Services.VrChatLogSnapshot? snapshot,
        bool gameRunning,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(_accountUserId))
        {
            return false;
        }

        var openEntries = _store.ForAccount(_accountUserId)
            .Where(entry => entry.LeftAt is null)
            .ToList();
        var active = openEntries.FirstOrDefault(entry =>
            string.Equals(entry.Id, _activeEntryId, StringComparison.Ordinal));

        if (!gameRunning)
        {
            if (active is null)
            {
                return false;
            }

            // A crashed VRChat process can leave the log's last location behind.
            // Do not keep that observation open until the next poll decides it
            // is stale; close it at the last event that actually happened.
            _store.CloseSession(
                active,
                active.LastEventAt == default ? active.LastSeenAt : active.LastEventAt);
            _activeEntryId = string.Empty;
            _activeLocation = string.Empty;
            return true;
        }

        if (snapshot is null || !LaunchService.IsLocation(snapshot.Location))
        {
            return false;
        }

        var locationChanged = !string.Equals(snapshot.Location, _activeLocation, StringComparison.Ordinal);
        if (!locationChanged && active is null)
        {
            active = _store.FindOpen(_accountUserId, snapshot.Location);
            if (active is not null)
            {
                _activeEntryId = active.Id;
                _activeLocation = active.Location;
            }
        }

        if (active is null || locationChanged)
        {
            if (active is not null)
            {
                _store.CloseSession(active, now);
            }

            // A stale open row can be closed while VRChat is still in the same
            // instance (for example after the app was suspended). Reuse its
            // latest event, or start a fresh observation rather than going
            // silent until the next world change.
            active = _store.FindOpen(_accountUserId, snapshot.Location);
            if (active is null)
            {
                var newEntry = InstanceHistoryStore.NewFromLog(
                    _accountUserId,
                    snapshot.Location,
                    now);
                if (newEntry is null)
                {
                    return false;
                }

                var self = newEntry.GetOrAddPlayer(_accountUserId, _accountDisplayName, _accountAvatarUrl);
                self.StartVisit(now);
                newEntry.LastSeenAt = now;
                active = _store.Add(newEntry);
            }

            _activeEntryId = active.Id;
            _activeLocation = active.Location;
            locationChanged = true;
        }

        SyncPlayers(active, snapshot.Players, now);
        return locationChanged;
    }

    /// <summary>Events are written immediately, so there is nothing left to flush.</summary>
    public void Flush(DateTimeOffset? now = null)
    {
    }

    public void Stop(DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            StopCore(now);
        }
    }

    private void StopCore(DateTimeOffset? now)
    {
        if (string.IsNullOrWhiteSpace(_activeEntryId))
        {
            return;
        }

        var active = _store.Find(_activeEntryId);
        if (active is not null && active.LeftAt is null)
        {
            _store.CloseSession(
                active,
                now ?? (active.LastEventAt == default ? active.LastSeenAt : active.LastEventAt));
        }

        _activeEntryId = string.Empty;
        _activeLocation = string.Empty;
    }

    private void CloseStaleRows(string accountUserId)
    {
        _store.CloseOpenSessionsOlderThan(
            accountUserId,
            DateTimeOffset.Now - TimeSpan.FromMinutes(30));
    }

    private void SyncPlayers(
        InstanceHistoryEntry entry,
        IReadOnlyList<VrcCompanion.Services.VrChatLogPlayer> snapshotPlayers,
        DateTimeOffset now)
    {
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshotPlayer in snapshotPlayers)
        {
            if (string.Equals(snapshotPlayer.UserId, _accountUserId, StringComparison.Ordinal))
            {
                current.Add(PlayerKey(snapshotPlayer.UserId, snapshotPlayer.DisplayName));
                continue;
            }

            var key = PlayerKey(snapshotPlayer.UserId, snapshotPlayer.DisplayName);
            current.Add(key);
            current.Add("name:" + snapshotPlayer.DisplayName.Trim());

            var player = FindPlayer(entry, snapshotPlayer.UserId, snapshotPlayer.DisplayName);
            if (player is null || !player.HasOpenVisit)
            {
                _store.RecordPlayerEvent(
                    entry.AccountUserId,
                    entry.Location,
                    snapshotPlayer.UserId,
                    snapshotPlayer.DisplayName,
                    player?.AvatarUrl ?? string.Empty,
                    joined: true,
                    now);
            }
        }

        foreach (var player in entry.Players)
        {
            if (string.Equals(player.UserId, entry.AccountUserId, StringComparison.Ordinal) ||
                !player.HasOpenVisit)
            {
                continue;
            }

            if (current.Contains(PlayerKey(player.UserId, player.DisplayName)) ||
                current.Contains("name:" + player.DisplayName.Trim()))
            {
                continue;
            }

            _store.RecordPlayerEvent(
                entry.AccountUserId,
                entry.Location,
                player.UserId,
                player.DisplayName,
                player.AvatarUrl,
                joined: false,
                now);
        }
    }

    private static InstanceHistoryPlayer? FindPlayer(
        InstanceHistoryEntry entry,
        string userId,
        string displayName)
    {
        if (!string.IsNullOrWhiteSpace(userId))
        {
            var byId = entry.Players.FirstOrDefault(player =>
                string.Equals(player.UserId, userId, StringComparison.Ordinal));
            if (byId is not null)
            {
                return byId;
            }
        }

        return entry.Players.FirstOrDefault(player =>
            string.Equals(player.DisplayName, displayName.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static string PlayerKey(string userId, string displayName)
        => !string.IsNullOrWhiteSpace(userId)
            ? userId
            : "name:" + displayName.Trim();
}
