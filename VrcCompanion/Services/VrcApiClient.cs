using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

public sealed class VrcApiException : Exception
{
    public int StatusCode { get; }

    public VrcApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// Minimal client for the unofficial VRChat web API. Requests are serialized
/// and spaced out to stay well inside VRChat's rate limits.
/// </summary>
public sealed class VrcApiClient : IDisposable
{
    private const string BaseAddress = "https://api.vrchat.cloud/api/1/";
    private static readonly Uri BaseUri = new(BaseAddress);
    private static readonly Uri CookieUri = new("https://api.vrchat.cloud/");
    // Measured: 24 friend pages at 150 ms spacing all returned 200 with ~115 ms
    // latency, so the old one-second gap was far more conservative than needed.
    private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(200);
    private const int RateLimitRetries = 2;
    private static readonly TimeSpan AvatarMutationInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan WorldCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProfileCacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MutualCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FriendsCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan BlockedUsersCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MyGroupsCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UserGroupsCacheDuration = TimeSpan.FromMinutes(3);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly SessionStore _sessionStore;
    private readonly SemaphoreSlim _requestGate = new(1, 1);

    // Avatar detail lookups are independent, read-only requests. Queuing them
    // behind the one-at-a-time spacing gate made a list of favourites fill in
    // one avatar every 200 ms; a small parallel gate is still polite to the API
    // and lets the whole list arrive together.
    private readonly SemaphoreSlim _avatarReadGate = new(4, 4);
    private readonly SemaphoreSlim _avatarMutationGate = new(1, 1);
    private DateTimeOffset _lastAvatarMutationAt = DateTimeOffset.MinValue;
    private readonly ConcurrentDictionary<string, CacheEntry<VrcWorld>> _worldCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<VrcProfile>> _profileCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<VrcMutuals>> _mutualCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<VrcUser>> _userCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<VrcCosmetic>>> _cosmeticCache = new(StringComparer.Ordinal);
    private CacheEntry<IReadOnlyList<VrcUser>>? _friendsCache;
    private bool _friendsCacheComplete;
    private bool _friendsCacheOnlineOnly;
    private CacheEntry<IReadOnlySet<string>>? _blockedUsersCache;
    private CacheEntry<IReadOnlyList<Group>>? _myGroupsCache;
    private string? _myGroupsCacheUserId;
    private readonly ConcurrentDictionary<string, CacheEntry<IReadOnlyList<Group>>> _userGroupsCache = new(StringComparer.Ordinal);
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public VrcApiClient(SessionStore? sessionStore = null)
        : this(sessionStore, null)
    {
    }

    internal VrcApiClient(SessionStore? sessionStore, HttpMessageHandler? handler)
    {
        _sessionStore = sessionStore ?? new SessionStore();
        handler ??= new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };

        _http = new HttpClient(handler)
        {
            BaseAddress = BaseUri,
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VrcCompanion/1.0.0 (unofficial Windows companion)");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public VrcUser? CurrentUser { get; private set; }

    public bool HasSavedSession => _sessionStore.Exists;

    // ---------------------------------------------------------------- auth

    public async Task<LoginResult> LoginAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        // Make sure an old cookie cannot silently answer the login request.
        ClearAuthCookie();

        var raw = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
        var token = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(raw));

        var body = await SendApiAsync(HttpMethod.Get, "auth/user", null, cancellationToken, token);
        var result = ParseAuthResponse(body);
        if (result.User is not null)
        {
            CurrentUser = result.User;
            SyncImageAuth();
        }
        return result;
    }

    public async Task<VrcUser> VerifyTwoFactorAsync(string code, TwoFactorMethod method, CancellationToken cancellationToken = default)
    {
        var path = method switch
        {
            TwoFactorMethod.Email => "auth/twofactorauth/emailotp/verify",
            TwoFactorMethod.Recovery => "auth/twofactorauth/otp/verify",
            _ => "auth/twofactorauth/totp/verify"
        };

        using var content = JsonContent.Create(new { code });
        var body = await SendApiAsync(HttpMethod.Post, path, content, cancellationToken);

        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("verified", out var verified) &&
                    verified.ValueKind == JsonValueKind.False)
                {
                    throw new VrcApiException(401, "That code was rejected. Check it and try again.");
                }
            }
            catch (JsonException)
            {
                // Some deployments return an empty or non-JSON success body.
            }
        }

        var result = await GetCurrentUserAsync(cancellationToken);
        if (result.RequiresTwoFactor)
        {
            throw new VrcApiException(401, "Two-factor verification did not complete. Try again.");
        }
        if (result.User is null)
        {
            throw new VrcApiException(401, "Two-factor verification did not return a user.");
        }

        CurrentUser = result.User;
        return result.User;
    }

    public async Task<LoginResult> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(HttpMethod.Get, "auth/user", null, cancellationToken);
        var result = ParseAuthResponse(body);
        if (result.User is not null)
        {
            CurrentUser = result.User;
            SyncImageAuth();
        }
        return result;
    }

    /// <summary>Restores a previously saved auth cookie, if it is still valid.</summary>
    public async Task<VrcUser?> RestoreSessionAsync(CancellationToken cancellationToken = default)
    {
        var value = _sessionStore.Load();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        ClearAuthCookie();
        AddAuthCookie(value);

        try
        {
            var result = await GetCurrentUserAsync(cancellationToken);
            if (result.User is null)
            {
                _sessionStore.Clear();
                ClearAuthCookie();
                return null;
            }

            CurrentUser = result.User;
            SaveSession();
            return result.User;
        }
        catch (VrcApiException ex) when (ex.StatusCode == 401)
        {
            _sessionStore.Clear();
            ClearAuthCookie();
            return null;
        }
    }

    public void SaveSession()
    {
        var value = GetAuthCookieValue();
        if (!string.IsNullOrWhiteSpace(value))
        {
            _sessionStore.Save(value);
        }
    }

    public void ClearSavedSession() => _sessionStore.Clear();

    /// <summary>The auth cookie for the current session, if one is available.</summary>
    public string? CurrentAuthCookie => GetAuthCookieValue();

    /// <summary>
    /// Returns the short-lived token VRChat's pipeline accepts. The normal auth
    /// cookie is used directly when present; this endpoint is the bootstrap
    /// fallback VRCX-0 uses when the cookie is not available.
    /// </summary>
    public async Task<string?> GetRealtimeTokenAsync(CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(HttpMethod.Get, "auth", null, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var ok = root.TryGetProperty("ok", out var okElement) &&
                 okElement.ValueKind == JsonValueKind.True;
        var token = root.TryGetProperty("token", out var tokenElement) &&
                    tokenElement.ValueKind == JsonValueKind.String
            ? tokenElement.GetString()
            : null;
        if (!ok || string.IsNullOrWhiteSpace(token))
        {
            throw new VrcApiException(401, "VRChat did not return a realtime token.");
        }

        return token;
    }

    /// <summary>
    /// Replaces the current session with a stored auth cookie and returns the
    /// account it belongs to, or null when the cookie is no longer valid.
    /// </summary>
    public async Task<VrcUser?> SwitchSessionAsync(string authCookie, CancellationToken cancellationToken = default)
    {
        var previousCookie = GetAuthCookieValue();
        var previousUser = CurrentUser;

        ClearAuthCookie();
        CurrentUser = null;
        ClearCaches();
        AddAuthCookie(authCookie);

        try
        {
            var result = await GetCurrentUserAsync(cancellationToken);
            if (result.User is not null)
            {
                return result.User;
            }

            RestorePreviousSession(previousCookie, previousUser);
            return null;
        }
        catch (VrcApiException ex) when (ex.StatusCode == 401)
        {
            RestorePreviousSession(previousCookie, previousUser);
            return null;
        }
        catch
        {
            RestorePreviousSession(previousCookie, previousUser);
            throw;
        }
    }

    /// <summary>Puts the session that was active before a failed account switch back.</summary>
    private void RestorePreviousSession(string? authCookie, VrcUser? user)
    {
        ClearAuthCookie();
        if (!string.IsNullOrWhiteSpace(authCookie))
        {
            AddAuthCookie(authCookie);
        }

        CurrentUser = user;
        ClearCaches();
        SyncImageAuth();
    }

    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(GetAuthCookieValue()))
            {
                await SendApiAsync(HttpMethod.Put, "auth/logout", null, cancellationToken);
            }
        }
        catch
        {
            // Local sign-out must succeed even if the network does not.
        }
        finally
        {
            CurrentUser = null;
            ClearAuthCookie();
            _sessionStore.Clear();
            ClearCaches();
            SyncImageAuth();
        }
    }

    // ------------------------------------------------------------- friends

    /// <summary>
    /// Loads the friend list. Large accounts need one request per 100 friends,
    /// so online friends are fetched first and handed to <paramref name="onPartial"/>,
    /// which lets the UI render them while the offline pages are still loading.
    /// A short-lived cache keeps other sections (such as the activity feed) from
    /// repeating the whole download.
    /// </summary>
    /// <param name="onlineOnly">
    /// Stops after the online pages. Offline friends change rarely and cost a
    /// request per 100 friends, so the background refresh uses this to stay quick.
    /// </param>
    public async Task<IReadOnlyList<VrcUser>> GetFriendsAsync(
        bool forceRefresh = false,
        bool onlineOnly = false,
        CancellationToken cancellationToken = default,
        Action<IReadOnlyList<VrcUser>>? onPartial = null)
    {
        if (!forceRefresh &&
            _friendsCache is { } cached &&
            _friendsCacheOnlineOnly == onlineOnly &&
            (onlineOnly || _friendsCacheComplete) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < FriendsCacheDuration)
        {
            return cached.Value;
        }

        var friends = new List<VrcUser>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // VRChat's offline filter is exclusive: offline=false returns online
        // friends, offline=true returns offline friends. Fetch both sets so the
        // companion shows the complete friend list.
        await FetchFriendPagesAsync("false", friends, seen, cancellationToken);
        onPartial?.Invoke(friends.ToList());

        if (onlineOnly)
        {
            // The current-user payload has the authoritative online/active
            // buckets. A missing bucket member means the paginated friend page
            // dropped them, so repair those directly before the UI reacts.
            var onlineExpected = ExpectedOnlineFriendIds();
            var onlineRepair = onlineExpected.Where(id => !seen.Contains(id)).ToList();
            if (onlineRepair.Count > 0)
            {
                await RepairFriendUsersAsync(
                    onlineRepair,
                    friends,
                    seen,
                    cancellationToken,
                    onPartial);
            }

            IReadOnlyList<VrcUser> online = friends;
            _friendsCache = new CacheEntry<IReadOnlyList<VrcUser>>(online, DateTimeOffset.UtcNow);
            _friendsCacheComplete = false;
            _friendsCacheOnlineOnly = true;
            return online;
        }

        await FetchFriendPagesAsync("true", friends, seen, cancellationToken);
        onPartial?.Invoke(friends.ToList());

        // VRCX reconciles the roster against /auth/user's friend-id buckets.
        // This catches friends omitted by pagination and friends whose fetched
        // presence contradicts their bucket. Repair them one by one so a large
        // account still ends up complete instead of quietly short.
        var expected = ExpectedFriendIds();
        if (expected.Count > 0)
        {
            var repairIds = new List<string>();
            foreach (var id in expected)
            {
                if (!seen.Contains(id))
                {
                    repairIds.Add(id);
                }
            }

            var onlineBuckets = ExpectedOnlineFriendIds();
            var offlineBuckets = ExpectedOfflineFriendIds();
            foreach (var friend in friends)
            {
                if (onlineBuckets.Contains(friend.Id) && IsOfflineUser(friend))
                {
                    repairIds.Add(friend.Id);
                }
                else if (offlineBuckets.Contains(friend.Id) && IsWorldUser(friend))
                {
                    repairIds.Add(friend.Id);
                }
            }

            if (repairIds.Count > 0)
            {
                await RepairFriendUsersAsync(
                    repairIds,
                    friends,
                    seen,
                    cancellationToken,
                    onPartial);
            }
        }

        IReadOnlyList<VrcUser> result = friends;
        _friendsCache = new CacheEntry<IReadOnlyList<VrcUser>>(result, DateTimeOffset.UtcNow);
        _friendsCacheComplete = true;
        _friendsCacheOnlineOnly = false;
        return result;
    }

    private async Task RepairFriendUsersAsync(
        IReadOnlyList<string> userIds,
        List<VrcUser> friends,
        HashSet<string> seen,
        CancellationToken cancellationToken,
        Action<IReadOnlyList<VrcUser>>? onPartial)
    {
        var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < friends.Count; index++)
        {
            indexById[friends[index].Id] = index;
        }

        var processed = 0;
        foreach (var userId in userIds
                     .Where(id => !string.IsNullOrWhiteSpace(id))
                     .Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var user = await GetUserAsync(
                    userId,
                    forceRefresh: true,
                    cancellationToken);
                if (user is not null && !string.IsNullOrWhiteSpace(user.Id))
                {
                    user.Id = userId;
                    user.IsFriend = true;
                    if (indexById.TryGetValue(userId, out var index))
                    {
                        friends[index] = user;
                    }
                    else
                    {
                        indexById[userId] = friends.Count;
                        friends.Add(user);
                    }

                    seen.Add(userId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // A single unavailable profile must not stop the roster repair.
            }

            processed++;
            if (processed % 10 == 0)
            {
                onPartial?.Invoke(friends.ToList());
            }
        }

        if (processed > 0)
        {
            onPartial?.Invoke(friends.ToList());
        }
    }

    private IReadOnlyList<string> ExpectedFriendIds()
    {
        var user = CurrentUser;
        if (user is null)
        {
            return Array.Empty<string>();
        }

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var list in new[]
                 {
                     user.Friends,
                     user.OnlineFriends,
                     user.ActiveFriends,
                     user.OfflineFriends
                 })
        {
            foreach (var id in list)
            {
                if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                {
                    ids.Add(id);
                }
            }
        }

        return ids;
    }

    private IReadOnlySet<string> ExpectedOnlineFriendIds()
    {
        var user = CurrentUser;
        if (user is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return user.OnlineFriends
            .Concat(user.ActiveFriends)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    private IReadOnlySet<string> ExpectedOfflineFriendIds()
    {
        var user = CurrentUser;
        if (user is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return user.OfflineFriends
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsOfflineUser(VrcUser user)
        => string.Equals(user.Status, "offline", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(user.State, "offline", StringComparison.OrdinalIgnoreCase) ||
           string.IsNullOrWhiteSpace(user.Location) ||
           string.Equals(user.Location, "offline", StringComparison.OrdinalIgnoreCase);

    private static bool IsWorldUser(VrcUser user)
        => user.Location?.StartsWith("wrld_", StringComparison.OrdinalIgnoreCase) == true ||
           user.Location?.StartsWith("traveling", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// Returns the users the signed-in account has blocked. This comes from the
    /// normal player-moderation list, whose rows are moderations you made.
    /// </summary>
    public async Task<IReadOnlySet<string>> GetBlockedUserIdsAsync(
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh &&
            _blockedUsersCache is { } cached &&
            DateTimeOffset.UtcNow - cached.FetchedAt < BlockedUsersCacheDuration)
        {
            return cached.Value;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            "auth/user/playermoderations?type=block",
            null,
            cancellationToken);
        var rows = Deserialize<List<VrcPlayerModeration>>(body) ?? new List<VrcPlayerModeration>();
        var currentUserId = CurrentUser?.Id;
        var unblockedTargets = new HashSet<string>(
            rows.Where(row =>
                    string.Equals(row.Type, "unblock", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(row.TargetUserId))
                .Select(row => row.TargetUserId!),
            StringComparer.Ordinal);
        var ids = new HashSet<string>(
            rows.Where(row =>
                    string.Equals(row.Type, "block", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(row.TargetUserId) &&
                    !string.Equals(row.TargetUserId, currentUserId, StringComparison.Ordinal) &&
                    !unblockedTargets.Contains(row.TargetUserId!) &&
                    (string.IsNullOrWhiteSpace(row.SourceUserId) ||
                     string.IsNullOrWhiteSpace(currentUserId) ||
                     string.Equals(row.SourceUserId, currentUserId, StringComparison.Ordinal)))
                .Select(row => row.TargetUserId!),
            StringComparer.Ordinal);
        IReadOnlySet<string> result = ids;
        _blockedUsersCache = new CacheEntry<IReadOnlySet<string>>(result, DateTimeOffset.UtcNow);
        return result;
    }

    private async Task FetchFriendPagesAsync(
        string offline,
        List<VrcUser> friends,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        // The maximum page size is 100. Loop in case the account has more.
        const int pageSize = 100;
        for (var offset = 0; offset < 7500; offset += pageSize)
        {
            var body = await SendApiAsync(
                HttpMethod.Get,
                $"auth/user/friends?n={pageSize}&offset={offset}&offline={offline}",
                null,
                cancellationToken);
            var page = Deserialize<List<VrcUser>>(body) ?? new List<VrcUser>();

            var added = 0;
            foreach (var friend in page)
            {
                if (!string.IsNullOrEmpty(friend.Id) && seen.Add(friend.Id))
                {
                    friends.Add(friend);
                    added++;
                }
            }

            // Stop if the API returned less than a full page, or if a whole
            // page had no new users (e.g. the filter was ignored).
            if (page.Count < pageSize || added == 0)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Reads VRChat's relationship flags for a user. The profile card uses this
    /// instead of guessing friendship from a public profile response.
    /// </summary>
    public async Task<VrcFriendStatus?> GetFriendStatusAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            $"user/{Uri.EscapeDataString(userId)}/friendStatus",
            null,
            cancellationToken);
        return Deserialize<VrcFriendStatus>(body);
    }

    public async Task SendFriendRequestAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Post,
            $"user/{Uri.EscapeDataString(userId)}/friendRequest",
            null,
            cancellationToken);
    }

    public async Task DeleteFriendRequestAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Delete,
            $"user/{Uri.EscapeDataString(userId)}/friendRequest",
            null,
            cancellationToken);
    }

    public async Task UnfriendAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Delete,
            $"auth/user/friends/{Uri.EscapeDataString(userId)}",
            null,
            cancellationToken);

        _friendsCache = null;
        _friendsCacheComplete = false;
        _friendsCacheOnlineOnly = false;
        _userCache.TryRemove(userId, out _);
        _profileCache.TryRemove(userId, out _);
    }

    public async Task AcceptFriendRequestAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Put,
            $"auth/user/notifications/{Uri.EscapeDataString(notificationId)}/accept",
            null,
            cancellationToken);

        _friendsCache = null;
        _friendsCacheComplete = false;
        _friendsCacheOnlineOnly = false;
    }

    /// <summary>
    /// Lists the account's uploaded emoji files. These are the custom emoji
    /// VRC+ users can attach to a boop.
    /// </summary>
    public async Task<IReadOnlyList<VrcFile>> GetBoopEmojiFilesAsync(
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            "files?tag=emoji&n=100",
            null,
            cancellationToken);
        return DeserializeList<VrcFile>(body);
    }

    /// <summary>
    /// Lists emoji items in the account's inventory. VRChat sends these as
    /// <c>inventoryItemId</c> instead of <c>emojiId</c>.
    /// </summary>
    public async Task<IReadOnlyList<VrcInventoryItem>> GetBoopInventoryEmojisAsync(
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            "inventory?n=100&types=emoji&notFlags=ugc&archived=false",
            null,
            cancellationToken);
        return DeserializeList<VrcInventoryItem>(body);
    }

    /// <summary>
    /// Resolves an emoji item attached by another user to a boop. The public
    /// inventory endpoint returns the image for that user's held item.
    /// </summary>
    public async Task<VrcInventoryItem?> GetUserInventoryItemAsync(
        string userId,
        string inventoryItemId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(inventoryItemId))
        {
            return null;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"user/{Uri.EscapeDataString(userId)}/inventory/{Uri.EscapeDataString(inventoryItemId)}",
            null,
            cancellationToken);
        return Deserialize<VrcInventoryItem>(body);
    }

    /// <summary>Resolves the artwork behind a custom emoji file id.</summary>
    public async Task<VrcFile?> GetFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return null;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"file/{Uri.EscapeDataString(fileId)}",
            null,
            cancellationToken);
        return Deserialize<VrcFile>(body);
    }

    /// <summary>
    /// Sends a boop to a friend. VRChat accepts an empty body for a plain boop,
    /// or exactly one emoji id. Custom files and inventory emojis use
    /// <c>emojiId</c> and <c>inventoryItemId</c> respectively; mutually exclusive
    /// fields match VRCX-0's request builder.
    /// </summary>
    public async Task SendBoopAsync(
        string userId,
        string? emojiId = null,
        string? inventoryItemId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("A user id is required to send a boop.", nameof(userId));
        }

        var normalizedEmojiId = emojiId?.Trim() ?? string.Empty;
        var normalizedInventoryItemId = inventoryItemId?.Trim() ?? string.Empty;
        if (normalizedEmojiId.Length > 0 && normalizedInventoryItemId.Length > 0)
        {
            throw new ArgumentException(
                "A boop accepts either emojiId or inventoryItemId, not both.",
                nameof(inventoryItemId));
        }

        object payload = normalizedEmojiId.Length > 0
            ? new { emojiId = normalizedEmojiId }
            : normalizedInventoryItemId.Length > 0
                ? new { inventoryItemId = normalizedInventoryItemId }
                : new { };

        using var content = JsonContent.Create(payload);
        await SendApiAsync(
            HttpMethod.Post,
            $"users/{Uri.EscapeDataString(userId.Trim())}/boop",
            content,
            cancellationToken);
    }

    /// <summary>
    /// Invites a user to an instance. <paramref name="messageSlot"/> selects one
    /// of VRChat's invite messages; slot 0 is the account's default message.
    /// </summary>
    public async Task InviteFriendAsync(
        string userId,
        string instanceId,
        int messageSlot = 0,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { instanceId, messageSlot });
        await SendApiAsync(
            HttpMethod.Post,
            $"invite/{Uri.EscapeDataString(userId)}",
            content,
            cancellationToken);
    }

    public async Task RequestInviteAsync(
        string userId,
        int requestSlot = 0,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { requestSlot });
        await SendApiAsync(
            HttpMethod.Post,
            $"requestInvite/{Uri.EscapeDataString(userId)}",
            content,
            cancellationToken);
    }

    /// <summary>
    /// Returns the account's saved messages for one of VRChat's four message
    /// collections, including each slot's remaining edit cooldown.
    /// </summary>
    public async Task<IReadOnlyList<InviteMessage>> GetInviteMessagesAsync(
        string userId,
        string messageType,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            $"message/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(messageType)}",
            null,
            cancellationToken);
        return Deserialize<List<InviteMessage>>(body) ?? new List<InviteMessage>();
    }

    /// <summary>
    /// Writes one of the account's reusable invite messages. VRChat limits
    /// message edits to once per hour, so callers should only write when the
    /// message really changed.
    /// </summary>
    public async Task UpdateInviteMessageAsync(
        string userId,
        string messageType,
        int slot,
        string message,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { message });
        await SendApiAsync(
            HttpMethod.Put,
            $"message/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(messageType)}/{slot}",
            content,
            cancellationToken);
    }

    /// <summary>
    /// Responds to an invite or invite request without joining it. The response
    /// slot comes from the same reusable message collections VRChat uses in-game.
    /// </summary>
    public async Task RespondInviteAsync(
        string notificationId,
        int responseSlot,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { responseSlot });
        await SendApiAsync(
            HttpMethod.Post,
            $"invite/{Uri.EscapeDataString(notificationId)}/response",
            content,
            cancellationToken);
    }

    public async Task HideNotificationAsync(
        string notificationId,
        bool isV2 = false,
        CancellationToken cancellationToken = default)
    {
        if (isV2)
        {
            await SendApiAsync(
                HttpMethod.Delete,
                $"notifications/{Uri.EscapeDataString(notificationId)}",
                null,
                cancellationToken);
            return;
        }

        await SendApiAsync(
            HttpMethod.Put,
            $"auth/user/notifications/{Uri.EscapeDataString(notificationId)}/hide",
            null,
            cancellationToken);
    }

    /// <summary>
    /// Sends the signed-in account an invite to one exact instance. VRChat's
    /// current endpoint is POST /invite/myself/to/{worldId}:{instanceId};
    /// this is what VRCX uses for the instance-history Self-invite action.
    /// </summary>
    public async Task InviteSelfAsync(
        string location,
        string? shortName = null,
        CancellationToken cancellationToken = default)
    {
        if (!LaunchService.IsLocation(location))
        {
            throw new ArgumentException("That is not a valid VRChat instance location.", nameof(location));
        }

        using var content = JsonContent.Create(new { shortName = shortName ?? string.Empty });
        await SendApiAsync(
            HttpMethod.Post,
            // VRChat's route parser expects the literal world:instance path,
            // including the colon. Percent-encoding it produces "malformed url".
            $"invite/myself/to/{location}",
            content,
            cancellationToken);
    }

    /// <summary>
    /// VRChat's instance short name is the launch token private/group instances
    /// need for self-invites and for the vrchat:// handoff to open the exact
    /// instance. Returns null when VRChat does not expose one.
    /// </summary>
    public async Task<string?> GetInstanceShortNameAsync(
        string location,
        CancellationToken cancellationToken = default)
    {
        var worldId = LaunchService.GetWorldIdFromLocation(location);
        var instanceId = LaunchService.GetInstanceIdFromLocation(location);
        if (worldId is null || instanceId is null)
        {
            return null;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"instances/{Uri.EscapeDataString(worldId)}:{Uri.EscapeDataString(instanceId)}/shortName",
            null,
            cancellationToken);
        var result = Deserialize<InstanceShortNameResponse>(body);
        return string.IsNullOrWhiteSpace(result?.ShortName)
            ? string.IsNullOrWhiteSpace(result?.SecureName) ? null : result!.SecureName
            : result!.ShortName;
    }

    // ------------------------------------------------------- notifications

    /// <summary>
    /// The combined notification feed. The legacy endpoint still carries
    /// friend requests and invites, while boops and other newer notification
    /// types only exist in NotificationV2. Legacy rows win when both feeds
    /// contain the same id.
    /// </summary>
    public async Task<IReadOnlyList<VrcNotification>> GetNotificationsAsync(CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            "auth/user/notifications?n=100",
            null,
            cancellationToken);
        var notifications = Deserialize<List<VrcNotification>>(body) ?? new List<VrcNotification>();

        IReadOnlyList<VrcNotification> v2Notifications;
        try
        {
            v2Notifications = await GetNotificationsV2Async(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The V2 feed is additive. A failure there must not hide the
            // legacy friend requests and invites.
            v2Notifications = Array.Empty<VrcNotification>();
        }

        if (v2Notifications.Count == 0)
        {
            return notifications;
        }

        var ids = notifications
            .Select(notification => notification.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var notification in v2Notifications)
        {
            if (!string.IsNullOrWhiteSpace(notification.Id) && ids.Add(notification.Id))
            {
                notifications.Add(notification);
            }
        }

        return notifications;
    }

    /// <summary>
    /// NotificationV2 is the only feed that carries boops. The documented
    /// route is /notifications; the notificationsV2 route is kept as a fallback
    /// for accounts and API versions where the documented route is absent.
    /// </summary>
    private async Task<IReadOnlyList<VrcNotification>> GetNotificationsV2Async(
        CancellationToken cancellationToken)
    {
        foreach (var path in new[]
                 {
                     "notifications?limit=100",
                     "auth/user/notificationsV2?n=100"
                 })
        {
            try
            {
                var body = await SendApiAsync(HttpMethod.Get, path, null, cancellationToken);
                var notifications = Deserialize<List<VrcNotification>>(body) ?? new List<VrcNotification>();
                foreach (var notification in notifications)
                {
                    notification.IsV2 = true;
                }
                return notifications;
            }
            catch (VrcApiException ex) when (ex.StatusCode is 400 or 403 or 404 or 405)
            {
                // Try the alternate route when this API version lacks the feed.
            }
        }

        return Array.Empty<VrcNotification>();
    }

    public async Task MarkNotificationSeenAsync(
        string notificationId,
        bool isV2 = false,
        CancellationToken cancellationToken = default)
    {
        if (isV2)
        {
            await SendApiAsync(
                HttpMethod.Post,
                $"notifications/{Uri.EscapeDataString(notificationId)}/see",
                null,
                cancellationToken);
            return;
        }

        await SendApiAsync(
            HttpMethod.Put,
            $"auth/user/notifications/{Uri.EscapeDataString(notificationId)}/see",
            null,
            cancellationToken);
    }

    // --------------------------------------------------------------- users

    public async Task<VrcUser> UpdateStatusAsync(
        string status,
        string statusDescription,
        CancellationToken cancellationToken = default)
    {
        if (CurrentUser is null)
        {
            throw new InvalidOperationException("Sign in before updating your status.");
        }

        var payload = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["status"] = status,
            ["statusDescription"] = statusDescription
        });

        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
        var body = await SendApiAsync(
            HttpMethod.Put,
            $"users/{Uri.EscapeDataString(CurrentUser.Id)}",
            content,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(body))
        {
            var updated = Deserialize<VrcUser>(body);
            if (updated is not null && !string.IsNullOrEmpty(updated.Id))
            {
                CurrentUser = updated;
                return updated;
            }
        }

        return CurrentUser;
    }

    public async Task<VrcUser?> GetUserAsync(
        string userId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh &&
            _userCache.TryGetValue(userId, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < ProfileCacheDuration)
        {
            return cached.Value;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"users/{Uri.EscapeDataString(userId)}",
            null,
            cancellationToken);
        var user = Deserialize<VrcUser>(body);
        if (user is not null && !string.IsNullOrWhiteSpace(user.Id))
        {
            _userCache[userId] = new CacheEntry<VrcUser>(user, DateTimeOffset.UtcNow);
        }
        return user;
    }

    /// <summary>
    /// Resolves a display name or @username to a user, so avatar search can list
    /// the avatars made by a specific person.
    /// </summary>
    public async Task<VrcUser?> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var text = query.Trim().TrimStart('@');
        try
        {
            var body = await SendApiAsync(
                HttpMethod.Get,
                $"users?search={Uri.EscapeDataString(text)}&n=10",
                null,
                cancellationToken);

            var users = Deserialize<List<VrcUser>>(body);
            if (users is { Count: > 0 })
            {
                return users.FirstOrDefault(user => string.Equals(user.DisplayName, text, StringComparison.OrdinalIgnoreCase))
                       ?? users[0];
            }
        }
        catch (Exception)
        {
            // Fall through to the single-user endpoint below.
        }

        try
        {
            return await GetUserAsync(text, forceRefresh: true, cancellationToken);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Searches players by username or resolves an exact VRChat user ID. Unlike
    /// <see cref="SearchUsersAsync"/>, this returns every close username match so
    /// the header picker can show a short result list.
    /// </summary>
    public async Task<IReadOnlyList<VrcUser>> SearchUserListAsync(
        string query,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var text = query.Trim().TrimStart('@');
        if (text.Length == 0)
        {
            return Array.Empty<VrcUser>();
        }

        if (text.StartsWith("usr_", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var user = await GetUserAsync(text, forceRefresh: true, cancellationToken);
                if (user is not null)
                {
                    return new[] { user };
                }
            }
            catch (VrcApiException)
            {
                // Fall through to the text search for a clearer result list.
            }
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"users?search={Uri.EscapeDataString(text)}&n={Math.Clamp(limit, 1, 20)}",
            null,
            cancellationToken);

        return Deserialize<List<VrcUser>>(body) ?? new List<VrcUser>();
    }

    public async Task<VrcProfile?> GetProfileAsync(
        string userId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh &&
            _profileCache.TryGetValue(userId, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < ProfileCacheDuration)
        {
            return cached.Value;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"profile/{Uri.EscapeDataString(userId)}?withGroupsAndWorlds=true",
            null,
            cancellationToken);
        var profile = Deserialize<VrcProfile>(body);
        if (profile is not null &&
            !string.IsNullOrWhiteSpace(profile.Id) &&
            !string.Equals(profile.Id, userId, StringComparison.OrdinalIgnoreCase))
        {
            // Never cache or return a profile under a different user's key.
            return null;
        }

        if (profile is not null && !string.IsNullOrWhiteSpace(profile.Id))
        {
            _profileCache[userId] = new CacheEntry<VrcProfile>(profile, DateTimeOffset.UtcNow);
        }
        return profile;
    }

    /// <summary>
    /// Lists the published artwork for one decoration type. VRChat stores only
    /// the cosmetic id on a user/profile, so this index is what turns an
    /// iconFrame/profileEffect/nameplateEffect id into displayable assets.
    /// </summary>
    public async Task<IReadOnlyList<VrcCosmetic>> GetCosmeticsAsync(
        string itemType,
        CancellationToken cancellationToken = default)
    {
        itemType = itemType.Trim();
        if (_cosmeticCache.TryGetValue(itemType, out var cached))
        {
            return cached.Value;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"cosmetics/index/{Uri.EscapeDataString(itemType)}",
            null,
            cancellationToken);
        var cosmetics = DeserializeList<VrcCosmetic>(body);
        _cosmeticCache[itemType] = new CacheEntry<IReadOnlyList<VrcCosmetic>>(
            cosmetics,
            DateTimeOffset.UtcNow);
        return cosmetics;
    }

    public async Task<VrcMutuals?> GetMutualsAsync(
        string userId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh &&
            _mutualCache.TryGetValue(userId, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < MutualCacheDuration)
        {
            return cached.Value;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"users/{Uri.EscapeDataString(userId)}/mutuals",
            null,
            cancellationToken);
        var mutuals = Deserialize<VrcMutuals>(body);
        if (mutuals is not null)
        {
            _mutualCache[userId] = new CacheEntry<VrcMutuals>(mutuals, DateTimeOffset.UtcNow);
        }
        return mutuals;
    }

    // -------------------------------------------------------------- worlds

    public async Task<IReadOnlyList<VrcWorld>> SearchWorldsAsync(string? query, CancellationToken cancellationToken = default)
    {
        var path = "worlds?n=24&sort=popularity&releaseStatus=public";
        if (!string.IsNullOrWhiteSpace(query))
        {
            path += $"&search={Uri.EscapeDataString(query.Trim())}";
        }

        var body = await SendApiAsync(HttpMethod.Get, path, null, cancellationToken);
        return Deserialize<List<VrcWorld>>(body) ?? new List<VrcWorld>();
    }

    public async Task<VrcWorld?> GetWorldAsync(
        string worldId,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        if (!forceRefresh &&
            _worldCache.TryGetValue(worldId, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < WorldCacheDuration)
        {
            return cached.Value;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"worlds/{Uri.EscapeDataString(worldId)}",
            null,
            cancellationToken);
        var world = Deserialize<VrcWorld>(body);
        if (world is not null && !string.IsNullOrWhiteSpace(world.Id))
        {
            _worldCache[worldId] = new CacheEntry<VrcWorld>(world, DateTimeOffset.UtcNow);
        }
        return world;
    }

    public async Task<VrcInstance> CreateInstanceAsync(
        string worldId,
        string type,
        string region,
        bool canRequestInvite,
        CancellationToken cancellationToken = default)
    {
        var ownerId = string.Equals(type, "public", StringComparison.OrdinalIgnoreCase)
            ? null
            : CurrentUser?.Id;
        if (!string.Equals(type, "public", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(ownerId))
        {
            throw new VrcApiException(0, "Sign in again before creating a friends-only instance.");
        }

        var payload = new Dictionary<string, object?>
        {
            ["worldId"] = worldId,
            ["type"] = type,
            ["region"] = region,
            ["canRequestInvite"] = canRequestInvite
        };
        if (ownerId is not null)
        {
            payload["ownerId"] = ownerId;
        }

        using var content = JsonContent.Create(payload);

        var body = await SendApiAsync(HttpMethod.Post, "instances", content, cancellationToken);
        var instance = Deserialize<VrcInstance>(body);
        if (instance is null || string.IsNullOrWhiteSpace(instance.InstanceId))
        {
            throw new VrcApiException(0, "VRChat did not return the new instance.");
        }

        if (string.IsNullOrWhiteSpace(instance.WorldId))
        {
            instance.WorldId = worldId;
        }
        if (string.IsNullOrWhiteSpace(instance.Location))
        {
            instance.Location = $"{instance.WorldId}:{instance.InstanceId}";
        }

        // The instance list on any cached world response is now stale.
        _worldCache.TryRemove(worldId, out _);
        return instance;
    }

    public async Task<VrcInstance?> GetInstanceByShortNameAsync(
        string shortName,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            $"instances/s/{Uri.EscapeDataString(shortName.Trim())}",
            null,
            cancellationToken);
        return Deserialize<VrcInstance>(body);
    }

    /// <summary>
    /// Loads the full instance record for a world location. The response includes
    /// the world, owner, capacity, region, type, tags and — when VRChat allows it —
    /// the instance's user list.
    /// </summary>
    public async Task<VrcInstance?> GetInstanceAsync(
        string location,
        CancellationToken cancellationToken = default)
    {
        if (!LaunchService.IsLocation(location))
        {
            return null;
        }

        var worldId = LaunchService.GetWorldIdFromLocation(location);
        var instanceId = LaunchService.GetInstanceIdFromLocation(location);
        if (string.IsNullOrWhiteSpace(worldId) || string.IsNullOrWhiteSpace(instanceId))
        {
            return null;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"instances/{worldId}:{instanceId}",
            null,
            cancellationToken);

        var instance = Deserialize<VrcInstance>(body);
        if (instance is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(instance.WorldId))
        {
            instance.WorldId = worldId;
        }

        if (string.IsNullOrWhiteSpace(instance.InstanceId))
        {
            instance.InstanceId = instanceId;
        }

        if (string.IsNullOrWhiteSpace(instance.Location))
        {
            instance.Location = location;
        }

        return instance;
    }

    // -------------------------------------------------------------- avatars

    public async Task<AvatarSearchResult> SearchAvatarsAsync(
        AvatarFilter filter,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, filter.Page);
        var pageSize = filter.PageSize is > 0 and <= 100 ? filter.PageSize : 50;

        // The avatar list only returns rows for a broad release status, and text
        // search additionally requires a marketplace (paid/free/all). Adding a
        // sort to a search query makes VRChat return nothing, so none is sent.
        var releaseStatus = string.IsNullOrWhiteSpace(filter.ReleaseStatus)
            ? "all"
            : filter.ReleaseStatus;

        var offset = (page - 1) * pageSize;
        var path = $"avatars?n={pageSize}&offset={offset}" +
                   $"&releaseStatus={Uri.EscapeDataString(releaseStatus)}";

        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            path += $"&search={Uri.EscapeDataString(filter.SearchTerm.Trim())}&marketplace=all";
        }
                if (!string.IsNullOrWhiteSpace(filter.AuthorId))
        {
            // VRChat filters avatars by creator with userId=<usr_id>.
            path += $"&userId={Uri.EscapeDataString(filter.AuthorId)}";
        }
        if (filter.Tags.Count > 0)
        {
            path += $"&tags={Uri.EscapeDataString(string.Join(",", filter.Tags))}";
        }

        // VRChat cannot browse other people's avatars for a normal account, so
        // there is no browse mode here: the community index serves that view.
        var body = await SendApiAsync(HttpMethod.Get, path, null, cancellationToken);
        var avatars = Deserialize<List<Avatar>>(body) ?? new List<Avatar>();

        // The avatar list endpoint reports no total, so a full page implies more.
        var totalCount = avatars.Count == pageSize
            ? (page * pageSize) + 1
            : ((page - 1) * pageSize) + avatars.Count;

        return new AvatarSearchResult
        {
            Avatars = avatars,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        };
    }

    /// <summary>
    /// Loads every avatar the account can list. VRChat returns at most 100 per
    /// request and no total, so pages are followed until a short page arrives.
    /// </summary>
    /// <param name="onPartial">
    /// Called after every page with everything fetched so far, so the grid can
    /// render while the remaining pages are still downloading.
    /// </param>
    /// <remarks>
    /// This returns the signed-in account's own avatars. Text search is done
    /// client-side because the API's <c>search</c> parameter switches the
    /// endpoint to marketplace results from other users.
    /// </remarks>
    public async Task<IReadOnlyList<Avatar>> GetAllAvatarsAsync(
        CancellationToken cancellationToken = default,
        Action<IReadOnlyList<Avatar>>? onPartial = null)
    {
        const int pageSize = 100;
        const int maxPages = 15;

        var avatars = new List<Avatar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 0; page < maxPages; page++)
        {
            // Match VRCX/VRCNext: scope the request to this account. Without
            // user=me VRChat can return a thinner record shape that omits tags.
            var path = $"avatars?user=me&releaseStatus=all&n={pageSize}" +
                       $"&offset={page * pageSize}&sort=updated&order=descending";

            var body = await SendApiAsync(HttpMethod.Get, path, null, cancellationToken);
            var items = Deserialize<List<Avatar>>(body) ?? new List<Avatar>();

            var added = 0;
            foreach (var avatar in items)
            {
                if (!string.IsNullOrEmpty(avatar.Id) && seen.Add(avatar.Id))
                {
                    avatars.Add(avatar);
                    added++;
                }
            }

            onPartial?.Invoke(avatars.ToList());

            if (items.Count < pageSize || added == 0)
            {
                break;
            }
        }

        return avatars;
    }

    /// <summary>
    /// Reads the signed-in account's avatars that have any of VRChat's content
    /// tags. Avatar list rows can omit <c>tags</c> for private/own records, while
    /// a tag-filtered query returns the tags needed by the card UI. This is one
    /// small request instead of fetching every avatar's full detail record.
    /// </summary>
    public async Task<IReadOnlyList<Avatar>> GetOwnContentTaggedAvatarsAsync(
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 100;
        const int maxPages = 15;
        const string contentTags =
            "content_adult,content_gore,content_horror,content_sex,content_violence";

        var avatars = new List<Avatar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < maxPages; page++)
        {
            var path = $"avatars?user=me&releaseStatus=all&n={pageSize}" +
                       $"&offset={page * pageSize}&sort=updated&order=descending" +
                       $"&tag={Uri.EscapeDataString(contentTags)}";
            var body = await SendApiAsync(HttpMethod.Get, path, null, cancellationToken);
            var items = Deserialize<List<Avatar>>(body) ?? new List<Avatar>();
            var added = 0;
            foreach (var avatar in items)
            {
                if (!string.IsNullOrWhiteSpace(avatar.Id) && seen.Add(avatar.Id))
                {
                    avatars.Add(avatar);
                    added++;
                }
            }

            if (items.Count < pageSize || added == 0)
            {
                break;
            }
        }

        return avatars;
    }

    private readonly Dictionary<string, Avatar> _avatarCache = new(StringComparer.Ordinal);

    private static HttpClient? _communityHttp;
    private static int _communityGeneration;

    // Snapshot of the pipeline this client was created under: a swapped handler
    // belongs to whoever swaps it, never to work left over from before the swap.
    private readonly int _communityPipelineGeneration = CommunityGeneration;

    internal static int CommunityGeneration => Volatile.Read(ref _communityGeneration);

    private static HttpClient CommunityHttp => _communityHttp ??= CreateCommunityHttp();

    private HttpClient CommunityHttpForThisClient
    {
        get
        {
            if (_communityPipelineGeneration != CommunityGeneration)
            {
                throw new OperationCanceledException(
                    "The community pipeline this client was created with has been replaced.");
            }

            return CommunityHttp;
        }
    }

    /// <summary>
    /// Test seam: swaps the community HTTP pipeline for a fake handler (pass null
    /// to restore the real client). Only tests should call this.
    /// </summary>
    internal static void SetCommunityHandler(HttpMessageHandler? handler)
    {
        Interlocked.Increment(ref _communityGeneration);
        _communityHttp?.Dispose();
        _communityHttp = handler is null
            ? null
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Records what one index answered. Callers must own the map: parallel
    /// fetches write to per-task maps and merge them after Task.WhenAll, so no
    /// shared object is ever written concurrently and nothing needs locking.
    /// </summary>
    internal static void MarkIndexResult(IDictionary<string, int>? results, string name, int count)
    {
        if (results is not null)
        {
            results[name] = count;
        }
    }

    /// <summary>Single-threaded merge of per-task index results, run after the awaits.</summary>
    private static void MergeIndexResults(
        IDictionary<string, int>? target,
        params IReadOnlyDictionary<string, int>[] sources)
    {
        if (target is null)
        {
            return;
        }

        foreach (var source in sources)
        {
            foreach (var entry in source)
            {
                target[entry.Key] = entry.Value;
            }
        }
    }

    private static HttpClient CreateCommunityHttp()
    {
        // avtr.zip advertises an IPv6 address that completes the TCP and TLS
        // handshake but then stalls while the response body is read. Prefer
        // IPv4 for community indexes, falling back to IPv6 when no IPv4 address
        // exists or IPv4 cannot connect.
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = ConnectCommunityAsync
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // avtr.icu rejects requests without a contact-bearing user agent.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "VrcCompanion/1.0 contact@vrccompanion.app");
        return client;
    }

    /// <summary>
    /// Tries IPv4 addresses before IPv6. A short per-address timeout keeps a
    /// broken route from consuming the whole provider deadline.
    /// </summary>
    private static async ValueTask<Stream> ConnectCommunityAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var ordered = addresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        Exception? lastError = null;
        foreach (var address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(5));
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port),
                    attempt.Token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                lastError = ex;
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        throw new HttpRequestException(
            $"Could not connect to {context.DnsEndPoint.Host}.",
            lastError);
    }

    /// <summary>
    /// Searches the community-run public avatar index (KitsuneDB), which lists
    /// avatars VRChat's own API will not return to an ordinary user. Failures are
    /// non-fatal: the VRChat results still show.
    /// </summary>
    public Task<List<Avatar>> SearchCommunityAvatarsAsync(
        string query,
        int limit = 25,
        int offset = 0,
        CancellationToken cancellationToken = default)
        => FetchCommunityAsync(string.IsNullOrWhiteSpace(query)
            ? $"latest?limit={limit}&offset={offset}"
            : $"search?search={Uri.EscapeDataString(query.Trim())}&limit={limit}&offset={offset}", cancellationToken);

    /// <summary>
    /// Public avatar indexes that speak the VRCX provider protocol
    /// ({url}?search=term&amp;n=count returns a bare JSON array). These are query
    /// endpoints, not downloadable catalogues: each is asked for a page and the
    /// results are merged locally.
    /// </summary>
    private static readonly (string Name, string Base)[] VrcxProviders =
    {
        // VRCX-0's current default provider. Unlike the older v1 endpoint, v3
        // answers ?search=, ?authorId= and ?fileId=.
        ("avtrDB", "https://api.avtrdb.com/v3/avatar/search/vrcx"),
        ("VRCDB", "https://vrcx.vrcdb.com/avatars/Avatar/VRCX"),
        // AviSeek indexes VRC Nexus and VRChat Legends submissions.
        ("AviSeek", "https://aviapi.vrchatlegends.com/api/vrcx"),
        ("PAW", "https://paw-api.amelia.fun/vrcx_search"),
        // NSVR moved from avtr.nekosunevr.co.uk to this host. It caps with
        // ?limit= rather than VRCX's ?n=, so both are sent below.
        ("NSVR", "https://vrcavatarsearch.nekosunevr.co.uk/vrcx_search"),
        // avtr.zip's public VRCX endpoint. The old vrcx.avtr.zip host is gone,
        // and its /v3 API needs a Turnstile token, but this path is open.
        ("avtr.zip", "https://avtr.zip/vrcx"),
        // AvatarRecovery rejects non-VRCX user agents at Cloudflare. The request
        // below sends its VRCX UA; no proxy or VPN is needed with that header.
        ("AvatarRecovery", "https://api.avatarrecovery.com/Avatar/vrcx"),
        ("WorldBalancer", "https://avatarwbvrcxsearch.worldbalancer.com/vrcx_search")
    };

    /// <summary>Every community index that can contribute to an avatar search.</summary>
    public static IReadOnlyList<string> CommunityProviderNames { get; } = VrcxProviders
        .Select(provider => provider.Name)
        .Concat(new[] { "avtr.icu", "VRCNDb" })
        .ToArray();

    /// <summary>
    /// A null selection means every provider; an explicit set limits the request
    /// to the named indexes. An empty set therefore means "no community index".
    /// </summary>
    private static bool IncludesProvider(IReadOnlyCollection<string>? providers, string name)
        => providers is null || providers.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Author lookup across the VRCX-protocol providers. Used for @name and
    /// usr_ id searches, which VRChat alone answers with a thin result set.
    /// </summary>
    public async Task<List<Avatar>> SearchCommunityByAuthorAsync(
        string authorId,
        int providerLimit = 40,
        IDictionary<string, int>? indexResults = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? providers = null,
        string? authorName = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token = deadline.Token;

        var merged = new List<Avatar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Each provider owns its own results map; the merge happens after the await.
        var selectedProviders = VrcxProviders
            .Where(provider => IncludesProvider(providers, provider.Name))
            .ToArray();
        var probes = selectedProviders
            .Select(_ => new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var tasks = selectedProviders.Select((provider, index) => FetchVrcxProviderAsync(
            provider.Name,
            $"{provider.Base}?authorId={Uri.EscapeDataString(authorId)}&n={providerLimit}&limit={providerLimit}",
            providerLimit,
            token,
            probes[index]));

        var pages = await Task.WhenAll(tasks);
        MergeIndexResults(indexResults, probes);

        foreach (var page in pages)
        {
            foreach (var avatar in page)
            {
                // Providers may ignore authorId; never let an unrelated avatar
                // into a profile's "public avatars" list. Some providers also
                // omit authorId on valid author-search results, so keep those
                // rather than dropping the very avatars the author published.
                if (!string.IsNullOrWhiteSpace(avatar.AuthorId) &&
                    !string.Equals(avatar.AuthorId, authorId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (seen.Add(avatar.Id))
                {
                    merged.Add(avatar);
                }
            }
        }

        // avtr.icu and VRCNDb do not answer the VRCX author-id parameter. Ask
        // them (and only them) by the display name as well, then keep only
        // rows that explicitly identify the same author.
        if (!string.IsNullOrWhiteSpace(authorName))
        {
            var extraProviders = new[] { "avtr.icu", "VRCNDb" }
                .Where(name => IncludesProvider(providers, name))
                .ToArray();
            if (extraProviders.Length > 0)
            {
                var named = await SearchAllCommunityAvatarsAsync(
                    authorName.Trim(),
                    limit: Math.Max(25, providerLimit),
                    providerLimit: providerLimit,
                    cancellationToken: token,
                    providers: extraProviders);
                foreach (var avatar in named)
                {
                    var sameAuthor =
                        (!string.IsNullOrWhiteSpace(avatar.AuthorId) &&
                         string.Equals(avatar.AuthorId, authorId, StringComparison.Ordinal)) ||
                        string.Equals(avatar.AuthorName, authorName.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (sameAuthor && seen.Add(avatar.Id))
                    {
                        merged.Add(avatar);
                    }
                }
            }
        }

        return merged;
    }

    /// <summary>
    /// Author-name lookup across every community index, including avtr.icu and
    /// VRCNDb, which do not implement the VRCX author-id query. Used when a
    /// profile exposes the creator of its current avatar but not the avatar id.
    /// </summary>
    public async Task<List<Avatar>> SearchCommunityByAuthorNameAsync(
        string authorName,
        int providerLimit = 40,
        IDictionary<string, int>? indexResults = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? providers = null)
    {
        if (string.IsNullOrWhiteSpace(authorName))
        {
            return new List<Avatar>();
        }

        var results = await SearchAllCommunityAvatarsAsync(
            authorName.Trim(),
            limit: Math.Max(25, providerLimit),
            providerLimit: providerLimit,
            indexResults: indexResults,
            cancellationToken: cancellationToken,
            providers: providers);
        return results
            .Where(avatar =>
                string.Equals(avatar.AuthorName, authorName.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Resolves an avatar from the image URL carried by a VRChat user record.
    /// VRCX does the same "find avatar" lookup because the user API frequently
    /// omits the avatar id while still returning currentAvatarImageUrl. Public
    /// community indexes answer ?fileId= with one avatar object; providers that
    /// do not support it are skipped. A missing result means the avatar is not
    /// publicly indexed (normally private).
    /// </summary>
    public async Task<Avatar?> ResolveCommunityAvatarByImageAsync(
        string? imageUrl,
        string? authorId = null,
        CancellationToken cancellationToken = default)
    {
        var fileId = ExtractAvatarFileId(imageUrl);
        if (string.IsNullOrWhiteSpace(fileId))
        {
            return null;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var token = deadline.Token;

        var tasks = VrcxProviders.Select(provider => FetchVrcxAvatarByFileIdAsync(
            provider.Name,
            $"{provider.Base}?fileId={Uri.EscapeDataString(fileId)}",
            token)).ToList();
        // VRCNext resolves pictures in one batch call rather than through the
        // VRCX search protocol. It is a useful extra source and often answers
        // when a search-only provider does not support ?fileId=.
        tasks.Add(FetchAvtrIcuByFileIdAsync(fileId, token));

        // Return the first hit instead of waiting for every index. One slow or
        // dead provider should not add five seconds to opening a profile.
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            Avatar? direct;
            try
            {
                direct = await completed;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"avatar image index: {ex.Message}");
                direct = null;
            }

            if (direct is not null)
            {
                return direct;
            }
        }

        // Some indexes do not implement ?fileId=. Author lookup is a cheap last
        // chance when the person is using an avatar they published themselves.
        if (string.IsNullOrWhiteSpace(authorId))
        {
            return null;
        }

        try
        {
            var authored = await SearchCommunityByAuthorAsync(
                authorId,
                providerLimit: 80,
                cancellationToken: token);
            return authored.FirstOrDefault(avatar =>
                string.Equals(ExtractAvatarFileId(avatar.ImageUrl), fileId, StringComparison.Ordinal));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"avatar image lookup: {ex.Message}");
            return null;
        }
    }

    private async Task<Avatar?> FetchVrcxAvatarByFileIdAsync(
        string name,
        string url,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Referer", "https://vrcx.app");
            using var response = await CommunityHttpForThisClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await ReadLimitedBodyAsync(response.Content, cancellationToken);
            return ParseVrcxAvatarObject(body, name);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"{name} avatar file lookup: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// VRCNext's avtr.icu picture resolver. It answers QUERY with a map keyed by
    /// the requested file id, so a profile picture can resolve even when the
    /// index does not expose a VRCX ?fileId= endpoint.
    /// </summary>
    private async Task<Avatar?> FetchAvtrIcuByFileIdAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(
                new HttpMethod("QUERY"),
                "https://avtr.icu/resolve")
            {
                Content = JsonContent.Create(new { ids = new[] { fileId } })
            };
            request.Headers.TryAddWithoutValidation("Referer", "https://vrccompanion.app");

            using var response = await CommunityHttpForThisClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await ReadLimitedBodyAsync(response.Content, cancellationToken);
            return ParseAvtrIcuAvatarObject(body, fileId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"avtr.icu avatar file lookup: {ex.Message}");
            return null;
        }
    }

    internal static Avatar? ParseAvtrIcuAvatarObject(string body, string fileId)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(fileId, out var root) ||
                root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            var id = FirstString(root, "id", "vrc_id");
            if (string.IsNullOrWhiteSpace(id) ||
                !id.StartsWith("avtr_", StringComparison.Ordinal) ||
                IsPlaceholderAvatarId(id))
            {
                return null;
            }

            return new Avatar
            {
                Id = id,
                Name = FirstString(root, "avatarName", "name", "displayName") ?? id,
                AuthorId = FirstString(root, "authorId", "author_id") ?? string.Empty,
                AuthorName = FirstString(root, "authorName", "author_name") ?? "Community database",
                ImageUrl = FirstString(root, "imageUrl", "image_url") ?? string.Empty,
                ThumbnailImageUrl = FirstString(root, "thumbnailImageUrl", "thumbnail_image_url") ?? string.Empty,
                ReleaseStatus = FirstString(root, "releaseStatus", "release_status") ?? "public",
                Description = FirstString(root, "description") ?? string.Empty,
                CreatedAt = FirstDate(root, "created_at", "createdAt") ?? default,
                UpdatedAt = FirstDate(root, "updated_at", "updatedAt") ?? default,
                Platform = StringList(root, "platforms", "platform", "compatibility"),
                Tags = StringList(root, "tags"),
                SourceName = "avtr.icu"
            };
        }
        catch (System.Text.Json.JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"avtr.icu file lookup body: {ex.Message}");
            return null;
        }
    }

    /// <summary>Pulls the VRChat file_… id from either a full or thumbnail image URL.</summary>
    internal static string? ExtractAvatarFileId(string? imageUrl)
        => ImageUrlHelper.ExtractFileId(imageUrl);

    internal static bool IsPlaceholderAvatarId(string? avatarId)
        => string.Equals(
            avatarId,
            "avtr_c38a1615-5bf5-42b4-84eb-a8b6c37cbd11",
            StringComparison.Ordinal);

    /// <summary>Parses the single-object response from a provider's ?fileId= mode.</summary>
    internal static Avatar? ParseVrcxAvatarObject(string body, string source)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                root = root.EnumerateArray().FirstOrDefault();
            }
            else if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                     root.TryGetProperty("avatars", out var wrapped) &&
                     wrapped.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                root = wrapped.EnumerateArray().FirstOrDefault();
            }

            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return null;
            }

            var id = FirstString(root, "id", "avatarId", "vrchatAvatarId");
            if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("avtr_", StringComparison.Ordinal))
            {
                return null;
            }

            // avtrDB uses VRChat's Robot record for an avatar that is hidden or
            // unavailable; treating that as a hit would show the wrong avatar.
            if (IsPlaceholderAvatarId(id))
            {
                return null;
            }

            return new Avatar
            {
                Id = id,
                Name = FirstString(root, "avatarName", "name", "displayName") ?? id,
                AuthorId = FirstString(root, "authorId", "author_id") ?? string.Empty,
                AuthorName = FirstString(root, "authorName", "author_name", "author", "creator") ?? "Community database",
                ImageUrl = FirstString(root, "imageUrl", "image_url") ?? string.Empty,
                ThumbnailImageUrl = FirstString(root, "thumbnailImageUrl", "thumbnail_image_url") ?? string.Empty,
                ReleaseStatus = FirstString(root, "releaseStatus", "release_status") ?? "public",
                Description = FirstString(root, "description") ?? string.Empty,
                CreatedAt = FirstDate(root, "created_at", "createdAt") ?? default,
                UpdatedAt = FirstDate(root, "updated_at", "updatedAt") ?? default,
                Platform = StringList(root, "platform", "platforms"),
                UnityPackages = ParseUnityPackages(root),
                SourceName = source
            };
        }
        catch (System.Text.Json.JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"{source}: file lookup body is not JSON ({ex.Message})");
            return null;
        }
    }

    /// <summary>
    /// Federated search: the paged avtr.icu index plus every live VRCX-protocol
    /// provider, merged by avatar id.
    /// </summary>
    public async Task<List<Avatar>> SearchAllCommunityAvatarsAsync(
        string query,
        int limit = 25,
        int providerLimit = 40,
        IDictionary<string, int>? indexResults = null,
        Action<string, IReadOnlyList<Avatar>>? onPartial = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? providers = null)
    {
        // A slow index used to hold back every result until the 15-second HTTP
        // timeout. Give the whole federated request a short deadline and publish
        // each source as soon as it answers.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token = deadline.Token;

        var pending = new List<(string Source, Task<List<Avatar>> Task, Dictionary<string, int> Probe)>();
        foreach (var provider in VrcxProviders.Where(provider => IncludesProvider(providers, provider.Name)))
        {
            var probe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            pending.Add((
                provider.Name,
                FetchVrcxProviderAsync(
                    provider.Name,
                    $"{provider.Base}?search={Uri.EscapeDataString(query.Trim())}&n={providerLimit}&limit={providerLimit}",
                    providerLimit,
                    token,
                    probe),
                probe));
        }

        if (IncludesProvider(providers, "avtr.icu"))
        {
            var avtrProbe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            pending.Add((
                "avtr.icu",
                FetchCommunityAsync(
                    $"search?search={Uri.EscapeDataString(query.Trim())}&limit={limit}",
                    token,
                    avtrProbe),
                avtrProbe));
        }

        if (IncludesProvider(providers, "VRCNDb"))
        {
            var vrcndbProbe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            pending.Add((
                "VRCNDb",
                FetchVrcndbAsync(query.Trim(), providerLimit, 1, token, vrcndbProbe),
                vrcndbProbe));
        }

        var merged = new List<Avatar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending.Select(entry => entry.Task));
            var index = pending.FindIndex(entry => ReferenceEquals(entry.Task, completed));
            if (index < 0)
            {
                break;
            }

            var entry = pending[index];
            pending.RemoveAt(index);

            List<Avatar> page;
            try
            {
                page = await completed;
            }
            catch (OperationCanceledException)
            {
                MarkIndexResult(entry.Probe, entry.Source, -1);
                page = new List<Avatar>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(ex);
                MarkIndexResult(entry.Probe, entry.Source, -1);
                page = new List<Avatar>();
            }

            MergeIndexResults(indexResults, entry.Probe);

            var newlyAdded = new List<Avatar>();
            foreach (var avatar in page)
            {
                if (seen.Add(avatar.Id))
                {
                    // Keep the source label with the row: the status line and the
                    // summary count community indexes from this value.
                    avatar.SourceName = entry.Source;
                    merged.Add(avatar);
                    newlyAdded.Add(avatar);
                }
            }

            // The caller adds these to its observable collection on the UI thread;
            // this loop resumes on the calling context between awaits.
            if (newlyAdded.Count > 0)
            {
                onPartial?.Invoke(entry.Source, newlyAdded);
            }
        }

        return merged;
    }

    /// <summary>
    /// Later result pages: VRCNDb and avtr.icu are the only sources with real
    /// paging, so Next asks each for its next page instead of re-reading page 1.
    /// </summary>
    public async Task<List<Avatar>> SearchCommunityNextPageAsync(
        string query,
        int vrcndbPage,
        int avtrOffset,
        int limit = 25,
        int providerLimit = 40,
        IDictionary<string, int>? indexResults = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? providers = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var token = deadline.Token;

        var avtrProbe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var vrcndbProbe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var avtrTask = IncludesProvider(providers, "avtr.icu")
            ? FetchCommunityAsync(
                $"search?search={Uri.EscapeDataString(query.Trim())}&limit={limit}&offset={avtrOffset}",
                token,
                avtrProbe)
            : Task.FromResult(new List<Avatar>());
        var vrcndbTask = IncludesProvider(providers, "VRCNDb")
            ? FetchVrcndbAsync(
                query.Trim(),
                providerLimit,
                vrcndbPage,
                token,
                vrcndbProbe)
            : Task.FromResult(new List<Avatar>());
        await Task.WhenAll(avtrTask, vrcndbTask);

        MergeIndexResults(indexResults, avtrProbe, vrcndbProbe);
        var avtr = await avtrTask;
        var vrcndb = await vrcndbTask;

        return MergeCommunityResults(new[]
        {
            ("avtr.icu", (IReadOnlyList<Avatar>)avtr),
            ("VRCNDb", (IReadOnlyList<Avatar>)vrcndb)
        });
    }

    /// <summary>
    /// VRCNDb (db.vrcnext.com) indexes well over a million avatars and has its own
    /// response shape: {page, limit, total, has_more, results:[{id, name,
    /// author_id, author_name, thumbnail}]}. Thumbnails are site-relative.
    /// </summary>
    internal async Task<List<Avatar>> FetchVrcndbAsync(
        string query,
        int limit,
        int page,
        CancellationToken cancellationToken,
        IDictionary<string, int>? indexResults = null)
    {
        var results = new List<Avatar>();
        try
        {
            using var response = await CommunityHttpForThisClient.GetAsync(
                $"https://db.vrcnext.com/api/search?q={Uri.EscapeDataString(query)}&limit={limit}&page={page}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"VRCNDb: HTTP {(int)response.StatusCode}");
                MarkIndexResult(indexResults, "VRCNDb", -1);
                return results;
            }

            using var document = ParseJsonBodyOrNull(
                await ReadLimitedBodyAsync(response.Content, cancellationToken),
                "VRCNDb");
            if (document is null)
            {
                MarkIndexResult(indexResults, "VRCNDb", -1);
                return results;
            }

            if (!document.RootElement.TryGetProperty("results", out var array) ||
                array.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                MarkIndexResult(indexResults, "VRCNDb", -1);
                return results;
            }

            foreach (var item in array.EnumerateArray())
            {
                var id = FirstString(item, "id");
                if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("avtr_", StringComparison.Ordinal))
                {
                    continue;
                }

                var thumb = FirstString(item, "thumbnail") ?? string.Empty;
                if (thumb.StartsWith("/", StringComparison.Ordinal))
                {
                    thumb = "https://db.vrcnext.com" + thumb;
                }

                results.Add(new Avatar
                {
                    Id = id,
                    Name = FirstString(item, "name") ?? id,
                    AuthorId = FirstString(item, "author_id") ?? string.Empty,
                    AuthorName = FirstString(item, "author_name") ?? "Community database",
                    ImageUrl = thumb,
                    ThumbnailImageUrl = thumb,
                    ReleaseStatus = "public",
                    Description = FirstString(item, "description") ?? string.Empty,
                    UpdatedAt = FirstDate(item, "updated_at", "updatedAt") ?? default,
                    Platform = StringList(item, "platforms", "platform"),
                    Tags = StringList(item, "tags"),
                    SourceName = "VRCNDb"
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            MarkIndexResult(indexResults, "VRCNDb", -1);
            return results;
        }

        MarkIndexResult(indexResults, "VRCNDb", results.Count);
        return results;
    }

    /// <summary>
    /// VRCDB and WorldBalancer ignore the requested row count and return their whole
    /// match set, so provider bodies are read with a hard byte budget instead of
    /// trusting the limit. Truncation is repaired to the last complete element.
    /// </summary>
    // VRCDB answers a 80-row request with 1000 rows (~460 KB) and WorldBalancer
    // with 889, so the budget is sized to hold far more rows than the cap needs
    // while refusing oversized bodies outright.
    internal const int MaxProviderBytes = 400_000;

    internal static async Task<string> ReadLimitedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaxProviderBytes];
        var total = 0;
        int read;
        while (total < buffer.Length &&
               (read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cancellationToken)) > 0)
        {
            total += read;
        }

        var text = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        if (total < buffer.Length)
        {
            return text;
        }

        var repaired = RepairTruncatedJson(text);
        if (repaired is null)
        {
            // The cut landed before even one complete record: keep it visible in
            // the log rather than failing as if the index had nothing to say.
            System.Diagnostics.Debug.WriteLine(
                "Provider body was truncated before any complete record; ignoring it.");
            return string.Empty;
        }

        return repaired;
    }

    /// <summary>
    /// Truncates an oversized body back to the last complete record. String and
    /// escape state are tracked, so braces inside descriptions cannot be mistaken
    /// for structure, and a cut inside a string still yields a valid document.
    /// </summary>
    /// <summary>
    /// Merges index pages by avatar id. An avatar listed by two indexes appears
    /// once, labelled with the first index that supplied it.
    /// </summary>
    internal static List<Avatar> MergeCommunityResults(
        IEnumerable<(string Source, IReadOnlyList<Avatar> Rows)> pages)
    {
        var merged = new List<Avatar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (source, rows) in pages)
        {
            foreach (var avatar in rows)
            {
                if (string.IsNullOrWhiteSpace(avatar.Id) || !seen.Add(avatar.Id))
                {
                    continue;
                }

                avatar.SourceName = source;
                merged.Add(avatar);
            }
        }

        return merged;
    }

    /// <summary>
    /// Shared guard for index responses: an empty or non-JSON body (HTML error or
    /// challenge page, SPA shell) returns null so that index simply contributes
    /// nothing instead of throwing.
    /// </summary>
    internal static System.Text.Json.JsonDocument? ParseJsonBodyOrNull(string body, string source)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            System.Diagnostics.Debug.WriteLine($"{source}: empty body");
            return null;
        }

        try
        {
            return System.Text.Json.JsonDocument.Parse(body);
        }
        catch (System.Text.Json.JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"{source}: body is not JSON ({ex.Message})");
            return null;
        }
    }

    internal static string? RepairTruncatedJson(string text)
    {
        var root = '\0';
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            root = character;
            break;
        }

        if (root is not ('[' or '{'))
        {
            return null;
        }

        // Root array: records sit at depth 2. Root object (VRCNDb) keeps them one
        // level deeper, inside its "results" array.
        var elementDepth = root == '[' ? 2 : 3;
        var depth = 0;
        var inString = false;
        var escaped = false;
        var lastComplete = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var character = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                case '[':
                    depth++;
                    break;
                case '}':
                case ']':
                    depth--;
                    if (character == '}' && depth == elementDepth - 1)
                    {
                        lastComplete = i + 1;
                    }

                    break;
            }
        }

        if (lastComplete <= 0)
        {
            return null;
        }

        var head = text[..lastComplete];
        return root == '[' ? head + "]" : head + "]}";
    }

    internal async Task<List<Avatar>> FetchVrcxProviderAsync(
        string name,
        string url,
        int cap,
        CancellationToken cancellationToken,
        IDictionary<string, int>? indexResults = null)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("Referer", "https://vrcx.app");
            // AvatarRecovery bans its public API for non-VRCX user agents. Its
            // endpoint is still a normal VRCX provider once the client identifies
            // itself the same way VRCX does.
            if (string.Equals(name, "AvatarRecovery", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "VRCX/2025.10.1");
            }

            using var response = await CommunityHttpForThisClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"{name}: HTTP {(int)response.StatusCode}");
                MarkIndexResult(indexResults, name, -1);
                return new List<Avatar>();
            }

            var body = await ReadLimitedBodyAsync(response.Content, cancellationToken);
            var rows = ParseVrcxProviderBody(body, name, cap, out var failed);
            MarkIndexResult(indexResults, name, failed ? -1 : rows.Count);
            return rows;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            MarkIndexResult(indexResults, name, -1);
            return new List<Avatar>();
        }
    }

    /// <summary>
    /// Parses a VRCX-protocol body. A non-JSON body (an HTML error or challenge
    /// page) yields an empty list instead of throwing, so one unhelpful index
    /// cannot take the federated merge down with it.
    /// </summary>
    internal static List<Avatar> ParseVrcxProviderBody(string body, string name, int cap)
        => ParseVrcxProviderBody(body, name, cap, out _);

    internal static List<Avatar> ParseVrcxProviderBody(
        string body,
        string name,
        int cap,
        out bool failed)
    {
        failed = false;
        var results = new List<Avatar>();
        if (string.IsNullOrWhiteSpace(body))
        {
            failed = true;
            return results;
        }

        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);

            var root = document.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object &&
                root.TryGetProperty("avatars", out var wrapped) &&
                wrapped.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                root = wrapped;
            }

            if (root.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                failed = true;
                return results;
            }

            foreach (var item in root.EnumerateArray())
            {
                if (results.Count >= cap)
                {
                    break;
                }

                var id = FirstString(item, "id", "avatarId", "vrchatAvatarId");
                if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("avtr_", StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new Avatar
                {
                    Id = id,
                    Name = FirstString(item, "avatarName", "name", "displayName") ?? id,
                    AuthorId = FirstString(item, "authorId", "author_id") ?? string.Empty,
                    AuthorName = FirstString(item, "authorName", "author_name", "author", "creator") ?? "Community database",
                    ImageUrl = FirstString(item, "imageUrl", "image_url") ?? string.Empty,
                    ThumbnailImageUrl = FirstString(item, "thumbnailImageUrl", "thumbnail_image_url") ?? string.Empty,
                    ReleaseStatus = FirstString(item, "releaseStatus", "release_status") ?? "public",
                    Description = FirstString(item, "description") ?? string.Empty,
                    CreatedAt = FirstDate(item, "created_at", "createdAt") ?? default,
                    UpdatedAt = FirstDate(item, "updated_at", "updatedAt") ?? default,
                    Platform = StringList(item, "platform", "platforms"),
                    Tags = StringList(item, "tags"),
                    ScanStatus = FirstString(item, "scanStatus", "scan_status") ?? string.Empty,
                    UnityPackages = ParseUnityPackages(item),
                    SourceName = name
                });
            }
        }
        catch (System.Text.Json.JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"{name}: body is not JSON ({ex.Message})");
            failed = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            failed = true;
        }

        return results;
    }

    /// <summary>Latest public avatars from the community index (browse view, paged by offset).</summary>
    public Task<List<Avatar>> GetCommunityLatestAsync(
        int limit = 50,
        int offset = 0,
        IDictionary<string, int>? indexResults = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? providers = null)
        => IncludesProvider(providers, "avtr.icu")
            ? FetchCommunityAsync($"latest?limit={limit}&offset={offset}", cancellationToken, indexResults)
            : Task.FromResult(new List<Avatar>());

    /// <summary>
    /// Latest public avatars from VRCNDb. An empty query is its newest-first
    /// browse feed; pages use the same 1-based page counter as search.
    /// </summary>
    public Task<List<Avatar>> GetVrcndbLatestAsync(
        int limit = 40,
        int page = 1,
        IDictionary<string, int>? indexResults = null,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? providers = null)
        => IncludesProvider(providers, "VRCNDb")
            ? FetchVrcndbAsync(string.Empty, limit, page, cancellationToken, indexResults)
            : Task.FromResult(new List<Avatar>());

    internal async Task<List<Avatar>> FetchCommunityAsync(
        string path,
        CancellationToken cancellationToken,
        IDictionary<string, int>? indexResults = null)
    {
        var results = new List<Avatar>();
        try
        {
            using var response = await CommunityHttpForThisClient.GetAsync(
                $"https://avtr.icu/{path}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine($"avtr.icu {path}: {(int)response.StatusCode}");
                MarkIndexResult(indexResults, "avtr.icu", -1);
                return results;
            }

            using var document = ParseJsonBodyOrNull(
                await ReadLimitedBodyAsync(response.Content, cancellationToken),
                "avtr.icu");
            if (document is null)
            {
                MarkIndexResult(indexResults, "avtr.icu", -1);
                return results;
            }

            var root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                MarkIndexResult(indexResults, "avtr.icu", -1);
                return results;
            }

            foreach (var item in root.EnumerateArray())
            {
                var id = FirstString(item, "id", "avatarId", "vrchatAvatarId");
                if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("avtr_", StringComparison.Ordinal))
                {
                    continue;
                }

                results.Add(new Avatar
                {
                    Id = id,
                    Name = FirstString(item, "avatarName", "name", "displayName") ?? id,
                    AuthorId = FirstString(item, "authorId", "author_id") ?? string.Empty,
                    AuthorName = FirstString(item, "authorName", "author_name", "author") ?? "Community database",
                    ImageUrl = FirstString(item, "imageUrl", "image_url") ?? string.Empty,
                    ThumbnailImageUrl = FirstString(item, "thumbnailImageUrl", "thumbnail_image_url") ?? string.Empty,
                    ReleaseStatus = FirstString(item, "releaseStatus", "release_status") ?? "public",
                    Description = FirstString(item, "description") ?? string.Empty,
                    CreatedAt = FirstDate(item, "created_at", "createdAt") ?? default,
                    UpdatedAt = FirstDate(item, "updated_at", "updatedAt") ?? default,
                    Platform = StringList(item, "platform", "platforms"),
                    Tags = StringList(item, "tags"),
                    ScanStatus = FirstString(item, "scanStatus", "scan_status") ?? string.Empty,
                    UnityPackages = ParseUnityPackages(item)
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
            MarkIndexResult(indexResults, "avtr.icu", -1);
            return results;
        }

        MarkIndexResult(indexResults, "avtr.icu", results.Count);
        return results;
    }

    private static string? FirstString(System.Text.Json.JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            else if (value.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                var nested = FirstString(value, "name", "displayName", "username");
                if (!string.IsNullOrWhiteSpace(nested))
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static DateTimeOffset? FirstDate(System.Text.Json.JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) &&
                value.ValueKind == System.Text.Json.JsonValueKind.String &&
                DateTimeOffset.TryParse(value.GetString(), out var date))
            {
                return date;
            }
        }

        return null;
    }

    private static List<string> StringList(System.Text.Json.JsonElement element, params string[] names)
    {
        var values = new List<string>();
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        values.Add(item.GetString()!);
                    }
                }
            }
            else if (value.ValueKind == System.Text.Json.JsonValueKind.String &&
                     !string.IsNullOrWhiteSpace(value.GetString()))
            {
                values.Add(value.GetString()!);
            }
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<AvatarUnityPackage> ParseUnityPackages(System.Text.Json.JsonElement element)
    {
        var packages = new List<AvatarUnityPackage>();
        if (!element.TryGetProperty("unityPackages", out var value) &&
            !element.TryGetProperty("unity_packages", out value))
        {
            return packages;
        }

        if (value.ValueKind != System.Text.Json.JsonValueKind.Array)
        {
            return packages;
        }

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                continue;
            }

            var platform = FirstString(item, "platform") ?? string.Empty;
            var scanStatus = FirstString(item, "scanStatus", "scan_status") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(platform) && string.IsNullOrWhiteSpace(scanStatus))
            {
                continue;
            }

            packages.Add(new AvatarUnityPackage
            {
                Platform = platform,
                Variant = FirstString(item, "variant") ?? string.Empty,
                PerformanceRating = FirstString(item, "performanceRating", "performance_rating") ?? string.Empty,
                ScanStatus = scanStatus
            });
        }

        return packages;
    }

    /// <summary>
    /// A single avatar by id. Favourites can include public avatars that are not
    /// in your library, and VRChat only returns their details from this endpoint.
    /// </summary>
    public async Task<Avatar?> GetAvatarAsync(string avatarId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(avatarId))
        {
            return null;
        }

        if (_avatarCache.TryGetValue(avatarId, out var cached))
        {
            return cached;
        }

        try
        {
            var body = await SendApiAsync(
                HttpMethod.Get,
                $"avatars/{Uri.EscapeDataString(avatarId)}",
                null,
                cancellationToken);

            var avatar = Deserialize<Avatar>(body);
            if (avatar is not null && !string.IsNullOrEmpty(avatar.Id))
            {
                _avatarCache[avatarId] = avatar;
            }

            return avatar;
        }
        catch (VrcApiException ex) when (ex.StatusCode == 404 || ex.StatusCode == 403)
        {
            // Deleted or private avatar: leave it out of the slot list.
            return null;
        }
    }

    /// <summary>Every avatar favourite on the account (id plus the record id).</summary>
    public async Task<IReadOnlyList<AvatarFavorite>> GetAvatarFavoritesAsync(
        CancellationToken cancellationToken = default)
    {
        const int pageSize = 100;
        const int maxPages = 10;

        var favorites = new List<AvatarFavorite>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var page = 0; page < maxPages; page++)
        {
            var body = await SendApiAsync(
                HttpMethod.Get,
                $"favorites?type=avatar&n={pageSize}&offset={page * pageSize}",
                null,
                cancellationToken);
            var items = Deserialize<List<AvatarFavorite>>(body) ?? new List<AvatarFavorite>();

            var added = 0;
            foreach (var favorite in items)
            {
                if (!string.IsNullOrEmpty(favorite.FavoriteId) && seen.Add(favorite.FavoriteId))
                {
                    favorites.Add(favorite);
                    added++;
                }
            }

            if (items.Count < pageSize || added == 0)
            {
                break;
            }
        }

        return favorites;
    }

    /// <summary>Favourites an avatar and returns the new favourite record id.</summary>
    public async Task<string?> AddAvatarFavoriteAsync(
        string avatarId,
        string tag = "avatars1",
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new
        {
            type = "avatar",
            favoriteId = avatarId,
            tags = tag
        });

        var body = await SendApiAsync(HttpMethod.Post, "favorites", content, cancellationToken);
        return Deserialize<AvatarFavorite>(body)?.Id;
    }

    public async Task RemoveFavoriteAsync(string favoriteRecordId, CancellationToken cancellationToken = default)
        => await SendApiAsync(
            HttpMethod.Delete,
            $"favorites/{Uri.EscapeDataString(favoriteRecordId)}",
            null,
            cancellationToken);

    /// <summary>
    /// Wears an avatar on this account. VRChat applies the change immediately,
    /// so the avatar is what the user sees in game.
    /// </summary>
    public async Task SetCurrentAvatarAsync(string avatarId, CancellationToken cancellationToken = default)
    {
        // This is a direct user action. It must not sit behind a long library or
        // friend prefetch in the normal request queue. VRCX-0 serializes avatar
        // mutations behind a 250 ms gate and sends the avatar id as a JSON body;
        // firing this concurrently with background API traffic can update the
        // account without VRChat emitting a usable avatar-change notification.
        await _avatarMutationGate.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTimeOffset.UtcNow - _lastAvatarMutationAt;
            if (elapsed < AvatarMutationInterval)
            {
                await Task.Delay(AvatarMutationInterval - elapsed, cancellationToken);
            }

            _lastAvatarMutationAt = DateTimeOffset.UtcNow;
            using var content = JsonContent.Create(new { avatarId });
            await SendUrgentApiAsync(
                HttpMethod.Put,
                $"avatars/{Uri.EscapeDataString(avatarId)}/select",
                content,
                cancellationToken);
        }
        finally
        {
            _avatarMutationGate.Release();
        }

        if (CurrentUser is not null)
        {
            CurrentUser.CurrentAvatar = avatarId;
        }
    }

    // --------------------------------------------------------------- groups

    /// <summary>
    /// Groups the signed-in user actually belongs to. <c>GET groups</c> returns
    /// a general group listing instead, so the user groups endpoint is used.
    /// </summary>
    public async Task<IReadOnlyList<Group>> GetMyGroupsAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (CurrentUser is null || string.IsNullOrWhiteSpace(CurrentUser.Id))
        {
            throw new InvalidOperationException("Sign in before loading your groups.");
        }

        var userId = CurrentUser.Id;
        if (!forceRefresh &&
            _myGroupsCacheUserId == userId &&
            _myGroupsCache is { } cached &&
            DateTimeOffset.UtcNow - cached.FetchedAt < MyGroupsCacheDuration)
        {
            return cached.Value;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"users/{Uri.EscapeDataString(userId)}/groups?n=100&offset=0",
            null,
            cancellationToken);
        var groups = DeserializeList<Group>(body);
        _myGroupsCache = new CacheEntry<IReadOnlyList<Group>>(groups, DateTimeOffset.UtcNow);
        _myGroupsCacheUserId = userId;
        return groups;
    }

    /// <summary>
    /// Every group on another user's profile. The profile payload only carries a
    /// handful of groups (its count can be much higher), so the user groups
    /// endpoint supplies the full membership list.
    /// </summary>
    public async Task<IReadOnlyList<Group>> GetUserGroupsAsync(
        string userId,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Array.Empty<Group>();
        }

        if (!forceRefresh &&
            _userGroupsCache.TryGetValue(userId, out var cached) &&
            DateTimeOffset.UtcNow - cached.FetchedAt < UserGroupsCacheDuration)
        {
            return cached.Value;
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"users/{Uri.EscapeDataString(userId)}/groups?n=100&offset=0",
            null,
            cancellationToken);
        var groups = DeserializeList<Group>(body);
        _userGroupsCache[userId] = new CacheEntry<IReadOnlyList<Group>>(groups, DateTimeOffset.UtcNow);
        return groups;
    }

    public async Task<Group?> GetGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        // includeRoles brings the role definitions and myMember.permissions along, which is
        // what tells the moderation panel what this account may do on the group.
        var body = await SendApiAsync(
            HttpMethod.Get,
            $"groups/{Uri.EscapeDataString(groupId)}?includeRoles=true",
            null,
            cancellationToken);
        return Deserialize<Group>(body);
    }

    /// <summary>
    /// Searches public groups by name or short code. A full group ID is looked
    /// up directly because the search endpoint only matches searchable names.
    /// </summary>
    public async Task<IReadOnlyList<Group>> SearchGroupsAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var trimmed = query.Trim();
        if (trimmed.Length == 0)
        {
            return Array.Empty<Group>();
        }

        if (trimmed.StartsWith("grp_", StringComparison.OrdinalIgnoreCase))
        {
            var group = await GetGroupAsync(trimmed, cancellationToken);
            return group is null ? Array.Empty<Group>() : new[] { group };
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"groups?query={Uri.EscapeDataString(trimmed)}&n=50&offset=0",
            null,
            cancellationToken);
        return DeserializeList<Group>(body);
    }

    /// <summary>Active instances the group is currently hosting.</summary>
    public async Task<IReadOnlyList<GroupInstance>> GetGroupInstancesAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            $"groups/{Uri.EscapeDataString(groupId)}/instances",
            null,
            cancellationToken);
        return DeserializeList<GroupInstance>(body);
    }

    /// <summary>
    /// Joins a group, or asks to join one that takes requests: VRChat decides from the
    /// group's join state and answers with the member object (POST groups/{id}/join).
    /// Returns that answer's membershipStatus — "member" or "requested".
    /// </summary>
    public async Task<string?> JoinGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { });
        var body = await SendApiAsync(
            HttpMethod.Post,
            $"groups/{Uri.EscapeDataString(groupId)}/join",
            content,
            cancellationToken);
        _myGroupsCache = null;
        return Deserialize<GroupMember>(body)?.MembershipStatus;
    }

    /// <summary>
    /// Declines the signed-in account's pending invite to a group
    /// (PUT groups/{id}/invites). This is separate from leaving a group: it
    /// does not touch membership if the invite was already accepted.
    /// </summary>
    public async Task DeclineGroupInviteAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { });
        await SendApiAsync(
            HttpMethod.Put,
            $"groups/{Uri.EscapeDataString(groupId)}/invites",
            content,
            cancellationToken);
        _myGroupsCache = null;
    }

    /// <summary>Leaves a group you belong to (POST groups/{id}/leave).</summary>
    public async Task LeaveGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { });
        await SendApiAsync(
            HttpMethod.Post,
            $"groups/{Uri.EscapeDataString(groupId)}/leave",
            content,
            cancellationToken);
        _myGroupsCache = null;
    }

    /// <summary>One page of members; VRChat returns at most 100 per request, so pass offset to continue.</summary>
    public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(
        string groupId,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            $"groups/{Uri.EscapeDataString(groupId)}/members?n=100&offset={Math.Max(0, offset)}",
            null,
            cancellationToken);
        return DeserializeList<GroupMember>(body);
    }

    /// <summary>Group members matching a display name; VRChat requires at least 3 characters.</summary>
    public async Task<IReadOnlyList<GroupMember>> SearchGroupMembersAsync(
        string groupId,
        string query,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            $"groups/{Uri.EscapeDataString(groupId)}/members/search" +
            $"?query={Uri.EscapeDataString(query.Trim())}&n=100&offset={Math.Max(0, offset)}",
            null,
            cancellationToken);
        var parsed = Deserialize<GroupMember.SearchResponse>(body);
        return parsed?.Results ?? DeserializeList<GroupMember>(body);
    }

    /// <summary>One member of a group by user id, or null when that user is not a member.</summary>
    public async Task<GroupMember?> GetGroupMemberAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var body = await SendApiAsync(
                HttpMethod.Get,
                $"groups/{Uri.EscapeDataString(groupId)}/members/{Uri.EscapeDataString(userId)}",
                null,
                cancellationToken);
            return Deserialize<GroupMember>(body);
        }
        catch (VrcApiException ex) when (ex.StatusCode == 404)
        {
            return null;
        }
    }

    // ------------------------------------------------------ group moderation

    private static string GroupModerationPath(string groupId, string suffix)
        => $"groups/{Uri.EscapeDataString(groupId)}/{suffix}";

    /// <summary>Banned accounts of a group (needs group-bans-manage).</summary>
    public async Task<IReadOnlyList<GroupRosterEntry>> GetGroupBansAsync(
        string groupId,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            GroupModerationPath(groupId, $"bans?n=100&offset={Math.Max(0, offset)}"),
            null,
            cancellationToken);
        return DeserializeList<GroupRosterEntry>(body);
    }

    public async Task BanGroupMemberAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { userId });
        await SendApiAsync(HttpMethod.Post, GroupModerationPath(groupId, "bans"), content, cancellationToken);
    }

    public async Task UnbanGroupMemberAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Delete,
            GroupModerationPath(groupId, $"bans/{Uri.EscapeDataString(userId)}"),
            null,
            cancellationToken);
    }

    /// <summary>Removes a member from the group; the same call unblocks or declines an invite.</summary>
    public async Task KickGroupMemberAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Delete,
            GroupModerationPath(groupId, $"members/{Uri.EscapeDataString(userId)}"),
            null,
            cancellationToken);
        _myGroupsCache = null;
    }

    /// <summary>Updates the staff-only moderator notes of one member.</summary>
    public async Task UpdateGroupMemberAsync(
        string groupId,
        string userId,
        string managerNotes,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { managerNotes });
        await SendApiAsync(
            HttpMethod.Put,
            GroupModerationPath(groupId, $"members/{Uri.EscapeDataString(userId)}"),
            content,
            cancellationToken);
    }

    public async Task AddGroupMemberRoleAsync(
        string groupId,
        string userId,
        string roleId,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { });
        await SendApiAsync(
            HttpMethod.Put,
            GroupModerationPath(groupId, $"members/{Uri.EscapeDataString(userId)}/roles/{Uri.EscapeDataString(roleId)}"),
            content,
            cancellationToken);
    }

    public async Task RemoveGroupMemberRoleAsync(
        string groupId,
        string userId,
        string roleId,
        CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Delete,
            GroupModerationPath(groupId, $"members/{Uri.EscapeDataString(userId)}/roles/{Uri.EscapeDataString(roleId)}"),
            null,
            cancellationToken);
    }

    /// <summary>Every permission name a role can carry, as VRChat lists them for the group.</summary>
    public async Task<IReadOnlyList<string>> GetGroupPermissionsAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            GroupModerationPath(groupId, "permissions"),
            null,
            cancellationToken);
        return DeserializeList<string>(body);
    }

    public async Task<GroupRole?> CreateGroupRoleAsync(
        string groupId,
        string name,
        string description,
        IReadOnlyList<string> permissions,
        bool isAddedOnJoin,
        bool isSelfAssignable,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new
        {
            name,
            description,
            permissions,
            isAddedOnJoin,
            isSelfAssignable
        });
        var body = await SendApiAsync(
            HttpMethod.Post,
            GroupModerationPath(groupId, "roles"),
            content,
            cancellationToken);
        return Deserialize<GroupRole>(body);
    }

    public async Task UpdateGroupRoleAsync(
        string groupId,
        string roleId,
        string name,
        string description,
        IReadOnlyList<string> permissions,
        bool isAddedOnJoin,
        bool isSelfAssignable,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new
        {
            name,
            description,
            permissions,
            isAddedOnJoin,
            isSelfAssignable
        });
        await SendApiAsync(
            HttpMethod.Put,
            GroupModerationPath(groupId, $"roles/{Uri.EscapeDataString(roleId)}"),
            content,
            cancellationToken);
    }

    public async Task DeleteGroupRoleAsync(
        string groupId,
        string roleId,
        CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Delete,
            GroupModerationPath(groupId, $"roles/{Uri.EscapeDataString(roleId)}"),
            null,
            cancellationToken);
    }

    public async Task<IReadOnlyList<GroupRole>> GetGroupRolesAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            GroupModerationPath(groupId, "roles"),
            null,
            cancellationToken);
        return DeserializeList<GroupRole>(body);
    }

    /// <summary>Join requests waiting for a moderator (needs group-members-manage).</summary>
    public async Task<IReadOnlyList<GroupRosterEntry>> GetGroupJoinRequestsAsync(
        string groupId,
        int offset = 0,
        bool blocked = false,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            GroupModerationPath(
                groupId,
                $"requests?n=100&offset={Math.Max(0, offset)}&blocked={(blocked ? "true" : "false")}"),
            null,
            cancellationToken);
        return DeserializeList<GroupRosterEntry>(body);
    }

    /// <summary>Accepts or rejects one join request; block also bars future requests.</summary>
    public async Task RespondGroupJoinRequestAsync(
        string groupId,
        string userId,
        bool accept,
        bool block = false,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(block
            ? new { action = accept ? "accept" : "reject", block = true }
            : (object)new { action = accept ? "accept" : "reject" });
        await SendApiAsync(
            HttpMethod.Put,
            GroupModerationPath(groupId, $"requests/{Uri.EscapeDataString(userId)}"),
            content,
            cancellationToken);
    }

    public async Task<IReadOnlyList<GroupRosterEntry>> GetGroupInvitesAsync(
        string groupId,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            GroupModerationPath(groupId, $"invites?n=100&offset={Math.Max(0, offset)}"),
            null,
            cancellationToken);
        return DeserializeList<GroupRosterEntry>(body);
    }

    public async Task InviteToGroupAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { userId, confirmOverrideBlock = false });
        await SendApiAsync(HttpMethod.Post, GroupModerationPath(groupId, "invites"), content, cancellationToken);
    }

    public async Task DeleteGroupInviteAsync(
        string groupId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Delete,
            GroupModerationPath(groupId, $"invites/{Uri.EscapeDataString(userId)}"),
            null,
            cancellationToken);
    }

    /// <summary>Recent audit entries (needs group-audit-view).</summary>
    public async Task<IReadOnlyList<GroupAuditEntry>> GetGroupAuditLogsAsync(
        string groupId,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            GroupModerationPath(groupId, $"auditLogs?n=100&offset={Math.Max(0, offset)}"),
            null,
            cancellationToken);
        var wrapped = Deserialize<GroupAuditEntry.AuditLogResponse>(body);
        return wrapped?.Results ?? DeserializeList<GroupAuditEntry>(body);
    }

    public async Task<Group?> CreateGroupAsync(
        string name,
        string description,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { name, description });
        var body = await SendApiAsync(HttpMethod.Post, "groups", content, cancellationToken);
        return Deserialize<Group>(body);
    }

    // -------------------------------------------------------------- helpers

    public void ClearCaches()
    {
        _worldCache.Clear();
        _profileCache.Clear();
        _mutualCache.Clear();
        _userCache.Clear();
        _friendsCache = null;
        _friendsCacheComplete = false;
        _friendsCacheOnlineOnly = false;
        _blockedUsersCache = null;
        _myGroupsCache = null;
        _myGroupsCacheUserId = null;
        _userGroupsCache.Clear();
    }

    public void Dispose()
    {
        _http.Dispose();
        // The request gates are deliberately left alone. A SemaphoreSlim only holds
        // unmanaged state while a wait is parked, and disposing one strands whoever
        // is already parked there: the wait never completes and its token does not
        // wake it. A background read that outlives this client would then never
        // return, and any lock it held on the way in would stay held for good.
    }

    private async Task<string> SendApiAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken,
        string? basicAuthToken = null)
    {
        var parallelRead = method == HttpMethod.Get &&
                           content is null &&
                           path.StartsWith("avatars/", StringComparison.Ordinal);
        var gate = parallelRead ? _avatarReadGate : _requestGate;
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, path) { Content = content };
                if (!string.IsNullOrEmpty(basicAuthToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuthToken);
                }

                if (!parallelRead)
                {
                    var elapsed = DateTimeOffset.UtcNow - _lastRequestAt;
                    if (elapsed < MinimumRequestInterval)
                    {
                        await Task.Delay(MinimumRequestInterval - elapsed, cancellationToken);
                    }

                    _lastRequestAt = DateTimeOffset.UtcNow;
                }
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);

                // Back off briefly if VRChat throttles us. Requests with a body
                // cannot be replayed, so only plain reads are retried.
                if ((int)response.StatusCode == 429 && attempt < RateLimitRetries && content is null)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                    await Task.Delay(wait, cancellationToken);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw CreateApiException(response.StatusCode, body);
                }

                return body;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sends a user action immediately, without waiting for queued background
    /// requests or the normal spacing between them. Used for avatar changes so the
    /// click reaches VRChat as soon as possible.
    /// </summary>
    private async Task<string> SendUrgentApiAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateApiException(response.StatusCode, body);
        }

        return body;
    }

    private static LoginResult ParseAuthResponse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new VrcApiException(0, "VRChat returned an unexpected response.");
        }

        if (doc.RootElement.TryGetProperty("requiresTwoFactorAuth", out var methodsElement) &&
            methodsElement.ValueKind == JsonValueKind.Array)
        {
            var methods = methodsElement
                .EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => x.Length > 0)
                .ToArray();

            if (methods.Length > 0)
            {
                return LoginResult.NeedsTwoFactor(methods);
            }
        }

        var user = Deserialize<VrcUser>(body);
        if (user is null || string.IsNullOrWhiteSpace(user.Id))
        {
            throw new VrcApiException(0, "VRChat returned an unreadable account response.");
        }

        return LoginResult.SignedIn(user);
    }

    private static IReadOnlyList<T> DeserializeList<T>(string body)
    {
        var list = Deserialize<List<T>>(body);
        if (list is not null)
        {
            return list;
        }

        // Some VRChat list endpoints wrap the array in a { "json": [...] } object.
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "json", "results", "data" })
                {
                    if (doc.RootElement.TryGetProperty(name, out var array) &&
                        array.ValueKind == JsonValueKind.Array)
                    {
                        return JsonSerializer.Deserialize<List<T>>(array.GetRawText(), JsonOptions) ?? new List<T>();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to an empty list.
        }

        return Array.Empty<T>();
    }

    private static T? Deserialize<T>(string body) where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(body, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static VrcApiException CreateApiException(System.Net.HttpStatusCode statusCode, string body)
    {
        var status = (int)statusCode;
        var detail = ExtractApiMessage(body);

        var message = status switch
        {
            401 when !string.IsNullOrWhiteSpace(detail) && !detail.Equals("Missing Credentials", StringComparison.OrdinalIgnoreCase) => detail,
            401 => "Incorrect username, password, or 2FA code.",
            403 when !string.IsNullOrWhiteSpace(detail) => detail,
            403 => "VRChat refused the request (403). Your account may need email verification, or the API is blocking this client.",
            429 => "VRChat's rate limit was reached. Wait a minute or two, then refresh.",
            404 => string.IsNullOrWhiteSpace(detail) ? "That VRChat item could not be found." : detail,
            >= 500 => $"VRChat is having server trouble (HTTP {status}). Try again shortly.",
            _ => string.IsNullOrWhiteSpace(detail) ? $"VRChat returned HTTP {status}." : detail
        };

        return new VrcApiException(status, message);
    }

    private static string ExtractApiMessage(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return string.Empty;
            }

            if (root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return CleanMessage(error.GetString());
                }

                if (error.ValueKind == JsonValueKind.Object &&
                    error.TryGetProperty("message", out var errorMessage) &&
                    errorMessage.ValueKind == JsonValueKind.String)
                {
                    return CleanMessage(errorMessage.GetString());
                }
            }

            if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.String)
            {
                return CleanMessage(message.GetString());
            }
        }
        catch (JsonException)
        {
            // Non-JSON error (often HTML from a proxy or WAF).
        }

        return string.Empty;
    }

    private static string CleanMessage(string? message)
    {
        var cleaned = message?.Trim() ?? string.Empty;
        if (cleaned.Length >= 2 && cleaned[0] == '"' && cleaned[^1] == '"')
        {
            cleaned = cleaned[1..^1];
        }
        return cleaned.Trim();
    }

    /// <summary>Lets the shared image cache reuse the current session cookie.</summary>
    private void SyncImageAuth() => ImageLoader.Shared.SetAuthCookie(GetAuthCookieValue());

    private string? GetAuthCookieValue()
    {
        try
        {
            return _cookies.GetCookies(CookieUri)["auth"]?.Value;
        }
        catch
        {
            return null;
        }
    }

    private void AddAuthCookie(string value)
    {
        try
        {
            _cookies.Add(new Cookie("auth", value, "/", "api.vrchat.cloud")
            {
                Secure = true,
                HttpOnly = true
            });
        }
        catch
        {
            // If the cookie cannot be added, the restore request will simply fail.
        }
    }

    private void ClearAuthCookie()
    {
        foreach (var domain in new[] { "api.vrchat.cloud", ".vrchat.cloud" })
        {
            try
            {
                _cookies.Add(new Cookie("auth", string.Empty, "/", domain)
                {
                    Expires = DateTime.UtcNow.AddDays(-1)
                });
            }
            catch
            {
                // Ignore malformed-domain errors; clearing is best effort.
            }
        }
    }

    private sealed record CacheEntry<T>(T Value, DateTimeOffset FetchedAt);

    private sealed class InstanceShortNameResponse
    {
        [System.Text.Json.Serialization.JsonPropertyName("shortName")]
        public string? ShortName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("secureName")]
        public string? SecureName { get; set; }
    }
}
