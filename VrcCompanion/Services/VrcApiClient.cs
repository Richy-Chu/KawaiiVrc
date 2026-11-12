using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
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
    private static readonly TimeSpan WorldCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProfileCacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan MutualCacheDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FriendsCacheDuration = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly SessionStore _sessionStore;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CacheEntry<VrcWorld>> _worldCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<VrcProfile>> _profileCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<VrcMutuals>> _mutualCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CacheEntry<VrcUser>> _userCache = new(StringComparer.Ordinal);
    private CacheEntry<IReadOnlyList<VrcUser>>? _friendsCache;
    private DateTimeOffset _lastRequestAt = DateTimeOffset.MinValue;

    public VrcApiClient(SessionStore? sessionStore = null)
    {
        _sessionStore = sessionStore ?? new SessionStore();
        var handler = new HttpClientHandler
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
            IReadOnlyList<VrcUser> online = friends;
            _friendsCache = new CacheEntry<IReadOnlyList<VrcUser>>(online, DateTimeOffset.UtcNow);
            return online;
        }

        await FetchFriendPagesAsync("true", friends, seen, cancellationToken);

        IReadOnlyList<VrcUser> result = friends;
        _friendsCache = new CacheEntry<IReadOnlyList<VrcUser>>(result, DateTimeOffset.UtcNow);
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

    public async Task AcceptFriendRequestAsync(string notificationId, CancellationToken cancellationToken = default)
    {
        await SendApiAsync(
            HttpMethod.Put,
            $"auth/user/notifications/{Uri.EscapeDataString(notificationId)}/accept",
            null,
            cancellationToken);
    }

    public async Task InviteFriendAsync(
        string userId,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { instanceId });
        await SendApiAsync(
            HttpMethod.Post,
            $"invite/{Uri.EscapeDataString(userId)}",
            content,
            cancellationToken);
    }

    public async Task RequestInviteAsync(string userId, CancellationToken cancellationToken = default)
    {
        using var content = JsonContent.Create(new { requestSlot = 0 });
        await SendApiAsync(
            HttpMethod.Post,
            $"requestInvite/{Uri.EscapeDataString(userId)}",
            content,
            cancellationToken);
    }

    // ------------------------------------------------------- notifications

    public async Task<IReadOnlyList<VrcNotification>> GetNotificationsAsync(CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            "auth/user/notifications?n=100",
            null,
            cancellationToken);
        return Deserialize<List<VrcNotification>>(body) ?? new List<VrcNotification>();
    }

    public async Task MarkNotificationSeenAsync(string notificationId, CancellationToken cancellationToken = default)
    {
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
        if (profile is not null && !string.IsNullOrWhiteSpace(profile.Id))
        {
            _profileCache[userId] = new CacheEntry<VrcProfile>(profile, DateTimeOffset.UtcNow);
        }
        return profile;
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
        using var content = JsonContent.Create(new
        {
            worldId,
            type,
            region,
            canRequestInvite
        });

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
            var path = $"avatars?n={pageSize}&offset={page * pageSize}&releaseStatus=all";

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

    private readonly Dictionary<string, Avatar> _avatarCache = new(StringComparer.Ordinal);

    private static HttpClient? _communityHttp;

    private static HttpClient CommunityHttp => _communityHttp ??= CreateCommunityHttp();

    /// <summary>
    /// Test seam: swaps the community HTTP pipeline for a fake handler (pass null
    /// to restore the real client). Only tests should call this.
    /// </summary>
    internal static void SetCommunityHandler(HttpMessageHandler? handler)
    {
        _communityHttp?.Dispose();
        _communityHttp = handler is null
            ? null
            : new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static void MarkIndexResult(IDictionary<string, int>? results, string name, int count)
    {
        if (results is not null)
        {
            results[name] = count;
        }
    }

    private static HttpClient CreateCommunityHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // avtr.icu rejects requests without a contact-bearing user agent.
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "VrcCompanion/1.0 contact@vrccompanion.app");
        return client;
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
        ("AviSeek", "https://aviseek.vrchatlegends.com/api/vrcx"),
        ("VRCDB", "https://vrcx.vrcdb.com/avatars/Avatar/VRCX"),
        ("PAW", "https://paw-api.amelia.fun/vrcx_search"),
        ("WorldBalancer", "https://avatarwbvrcxsearch.worldbalancer.com/vrcx_search")
    };

    /// <summary>
    /// Author lookup across the VRCX-protocol providers. Used for @name and
    /// usr_ id searches, which VRChat alone answers with a thin result set.
    /// </summary>
    public async Task<List<Avatar>> SearchCommunityByAuthorAsync(
        string authorId,
        int providerLimit = 40,
        IDictionary<string, int>? indexResults = null,
        CancellationToken cancellationToken = default)
    {
        var merged = new List<Avatar>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var tasks = VrcxProviders.Select(provider => FetchVrcxProviderAsync(
            provider.Name,
            $"{provider.Base}?authorId={Uri.EscapeDataString(authorId)}&n={providerLimit}",
            providerLimit,
            cancellationToken,
            indexResults));

        foreach (var page in await Task.WhenAll(tasks))
        {
            foreach (var avatar in page)
            {
                // Providers may ignore authorId; keep only that creator's avatars.
                if (!string.IsNullOrEmpty(avatar.AuthorId) &&
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

        return merged;
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
        CancellationToken cancellationToken = default)
    {
        var tasks = VrcxProviders.Select(provider => FetchVrcxProviderAsync(
            provider.Name,
            $"{provider.Base}?search={Uri.EscapeDataString(query.Trim())}&n={providerLimit}",
            providerLimit,
            cancellationToken,
            indexResults)).ToList();

        tasks.Add(FetchCommunityAsync(
            $"search?search={Uri.EscapeDataString(query.Trim())}&limit={limit}",
            cancellationToken,
            indexResults));
        tasks.Add(FetchVrcndbAsync(query.Trim(), providerLimit, 1, cancellationToken, indexResults));

        var pages = await Task.WhenAll(tasks);
        var sources = new List<(string Source, IReadOnlyList<Avatar> Rows)>(pages.Length);
        for (var i = 0; i < pages.Length; i++)
        {
            var source = i < VrcxProviders.Length
                ? VrcxProviders[i].Name
                : i == VrcxProviders.Length ? "avtr.icu" : "VRCNDb";
            sources.Add((source, pages[i]));
        }

        return MergeCommunityResults(sources);
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
        CancellationToken cancellationToken = default)
    {
        var avtrTask = FetchCommunityAsync(
            $"search?search={Uri.EscapeDataString(query.Trim())}&limit={limit}&offset={avtrOffset}",
            cancellationToken,
            indexResults);
        var vrcndbTask = FetchVrcndbAsync(
            query.Trim(),
            providerLimit,
            vrcndbPage,
            cancellationToken,
            indexResults);
        await Task.WhenAll(avtrTask, vrcndbTask);

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
            using var response = await CommunityHttp.GetAsync(
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
            using var response = await CommunityHttp.GetAsync(url, cancellationToken);
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
                    Name = FirstString(item, "name", "displayName") ?? id,
                    AuthorId = FirstString(item, "authorId") ?? string.Empty,
                    AuthorName = FirstString(item, "authorName", "author", "creator") ?? "Community database",
                    ImageUrl = FirstString(item, "imageUrl") ?? string.Empty,
                    ThumbnailImageUrl = FirstString(item, "thumbnailImageUrl") ?? string.Empty,
                    ReleaseStatus = FirstString(item, "releaseStatus") ?? "public",
                    Description = FirstString(item, "description") ?? string.Empty,
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
        CancellationToken cancellationToken = default)
        => FetchCommunityAsync($"latest?limit={limit}&offset={offset}", cancellationToken, indexResults);

    internal async Task<List<Avatar>> FetchCommunityAsync(
        string path,
        CancellationToken cancellationToken,
        IDictionary<string, int>? indexResults = null)
    {
        var results = new List<Avatar>();
        try
        {
            using var response = await CommunityHttp.GetAsync(
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
                    Name = FirstString(item, "name", "displayName") ?? id,
                    AuthorId = FirstString(item, "authorId") ?? string.Empty,
                    AuthorName = FirstString(item, "authorName", "author") ?? "Community database",
                    ImageUrl = FirstString(item, "imageUrl") ?? string.Empty,
                    ThumbnailImageUrl = FirstString(item, "thumbnailImageUrl") ?? string.Empty,
                    ReleaseStatus = FirstString(item, "releaseStatus") ?? "public",
                    Description = FirstString(item, "description") ?? string.Empty
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
        await SendApiAsync(
            HttpMethod.Put,
            $"avatars/{Uri.EscapeDataString(avatarId)}/select",
            null,
            cancellationToken);

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
    public async Task<IReadOnlyList<Group>> GetMyGroupsAsync(CancellationToken cancellationToken = default)
    {
        if (CurrentUser is null || string.IsNullOrWhiteSpace(CurrentUser.Id))
        {
            throw new InvalidOperationException("Sign in before loading your groups.");
        }

        var body = await SendApiAsync(
            HttpMethod.Get,
            $"users/{Uri.EscapeDataString(CurrentUser.Id)}/groups?n=100&offset=0",
            null,
            cancellationToken);
        return DeserializeList<Group>(body);
    }

    public async Task<IReadOnlyList<GroupMember>> GetGroupMembersAsync(
        string groupId,
        CancellationToken cancellationToken = default)
    {
        var body = await SendApiAsync(
            HttpMethod.Get,
            $"groups/{Uri.EscapeDataString(groupId)}/members?n=100&offset=0",
            null,
            cancellationToken);
        return DeserializeList<GroupMember>(body);
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
    }

    public void Dispose()
    {
        _http.Dispose();
        _requestGate.Dispose();
    }

    private async Task<string> SendApiAsync(
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken,
        string? basicAuthToken = null)
    {
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(method, path) { Content = content };
                if (!string.IsNullOrEmpty(basicAuthToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuthToken);
                }

                var elapsed = DateTimeOffset.UtcNow - _lastRequestAt;
                if (elapsed < MinimumRequestInterval)
                {
                    await Task.Delay(MinimumRequestInterval - elapsed, cancellationToken);
                }

                _lastRequestAt = DateTimeOffset.UtcNow;
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
            _requestGate.Release();
        }
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
}
