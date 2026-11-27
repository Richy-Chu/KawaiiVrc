using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// The filtered list of joins, leaves and Udon events for the instance the reader is
/// in. One feed is shared by the Context page, the Instance tab and the world
/// dialog's Players panel, so the three surfaces read the same rows and the same
/// filter instead of each keeping a copy.
/// </summary>
public sealed class InstanceActivityFeed : ViewModelBase
{
    private readonly Dictionary<string, InstanceActivityItem> _cache = new(StringComparer.Ordinal);
    private IReadOnlyList<VrChatLogActivity> _events = Array.Empty<VrChatLogActivity>();
    private FilterOption _selectedActivityFilter;
    private string _selfUserId = string.Empty;
    private string _selfDisplayName = string.Empty;
    private readonly Dictionary<string, string> _avatarImages = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _avatarImageLookups = new(StringComparer.OrdinalIgnoreCase);
    private Func<string, string?, Task<string?>>? _avatarImageResolver;
    private Func<string, string, string?>? _personImageResolver;
    private readonly Dictionary<string, string> _personImages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _personIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _personImageLookups = new(StringComparer.Ordinal);
    private Func<string, string, Task<PersonLookup?>>? _personLookup;

    /// <summary>
    /// What VRChat's user record gives an avatar row: the player's id, so the row can open
    /// their profile, and their picture, which fills the slot when the avatar has none.
    /// </summary>
    public sealed record PersonLookup(string? UserId, string? ImageUrl);

    public InstanceActivityFeed()
    {
        _selectedActivityFilter = ActivityFilters[0];
        // Paging brings a different set of avatar rows into view, and only the rows
        // the reader can see are worth a picture lookup.
        ActivityPage.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ActivityPage.PageToken))
            {
                ResolvePageAvatarImages();
            }
        };
    }

    /// <summary>The rows the selected filter keeps, newest first.</summary>
    public ObservableCollection<InstanceActivityItem> Activity { get; } = new();

    /// <summary>
    /// Eight rows a page: this box sits beside a roster and is read from the top, so a
    /// short page is enough and a busy instance builds few rows.
    /// </summary>
    public PagedList<InstanceActivityItem> ActivityPage { get; } = new(8);

    public ObservableCollection<FilterOption> ActivityFilters { get; } = new()
    {
        new FilterOption("all", "All"),
        new FilterOption("joined", "Joined"),
        new FilterOption("left", "Left"),
        new FilterOption("udon", "Udon"),
        new FilterOption("avatar", "Avatar")
    };

    /// <summary>Which kinds the box shows. Changing it re-pages the same rows.</summary>
    public FilterOption SelectedActivityFilter
    {
        get => _selectedActivityFilter;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedActivityFilter))
            {
                return;
            }

            _selectedActivityFilter = value;
            OnPropertyChanged();
            Rebuild(resetPage: true);
        }
    }

    public bool HasActivity => Activity.Count > 0;

    public string ActivityText => _events.Count == 0
        ? "Nothing yet in this instance."
        : Activity.Count == _events.Count
            ? $"{Activity.Count:N0} event(s)"
            : $"{Activity.Count:N0} of {_events.Count:N0} event(s)";

    public string ActivityEmptyText =>
        "Nothing yet. This box fills from VRChat's log while you are in an instance.";

    /// <summary>
    /// Takes the log's activity list as it was at the last read. Rows are cached by what
    /// they describe, so a refresh that finds the same events updates no row and the
    /// bound collection is left alone.
    /// </summary>
    public void Apply(IReadOnlyList<VrChatLogActivity>? events)
    {
        _events = events ?? Array.Empty<VrChatLogActivity>();
        Rebuild(resetPage: false);
    }

    public void Clear() => Apply(null);

    /// <summary>
    /// Names the signed-in account so its own joins, leaves and avatar switches can say
    /// YOU, exactly as the Activity page tags its rows. VRChat's switch line sometimes
    /// omits the user id, so the display name is kept as the fallback.
    /// </summary>
    public void SetSelf(string? userId, string? displayName)
    {
        var id = userId ?? string.Empty;
        var name = displayName ?? string.Empty;
        if (string.Equals(_selfUserId, id, StringComparison.Ordinal) &&
            string.Equals(_selfDisplayName, name, StringComparison.Ordinal))
        {
            return;
        }

        _selfUserId = id;
        _selfDisplayName = name;
        // Rows are cached, so the YOU flag baked into them is stale now.
        _cache.Clear();
        Rebuild(resetPage: false);
    }

    /// <summary>
    /// The lookup that turns an avatar name into its picture. The shell hands in the
    /// Activity page's own resolver, so both boxes show the same avatar for the same
    /// switch instead of two searches fighting over the same name.
    /// </summary>
    public void SetAvatarImageResolver(Func<string, string?, Task<string?>>? resolver)
    {
        _avatarImageResolver = resolver;
        ResolvePageAvatarImages();
    }

    /// <summary>
    /// The person's own picture, which the Activity page draws on a switch row. It is
    /// filled from the roster the reader already sees, and always wins over the changed
    /// avatar's picture, so both surfaces show the same face for the same switch.
    /// </summary>
    public void SetPersonImageResolver(Func<string, string, string?>? resolver)
    {
        _personImageResolver = resolver;
        RefreshPictures();
    }

    /// <summary>
    /// Asks VRChat for the person's own picture when the roster has no profile for the
    /// row - typically a switch line that carried no user id. This is the same picture
    /// the Activity page draws, so the avatar filter never shows an empty face just
    /// because the instance roster did not know the player.
    /// </summary>
    public void SetPersonLookup(Func<string, string, Task<PersonLookup?>>? lookup)
    {
        _personLookup = lookup;
        ResolvePageAvatarImages();
    }

    /// <summary>
    /// Re-reads the person pictures after the roster gained a profile or a picture.
    /// The rows are cached, so a new roster image has to be pushed into them.
    /// </summary>
    public void RefreshPictures()
    {
        if (_personImageResolver is null)
        {
            return;
        }

        foreach (var item in Activity)
        {
            if (!item.IsAvatarChange)
            {
                continue;
            }

            var person = _personImageResolver(item.Activity.UserId, item.Activity.DisplayName);
            if (!string.IsNullOrWhiteSpace(person))
            {
                item.SetPersonImage(person);
            }
        }
    }

    /// <summary>
    /// The Activity page's person picture for a roster row, matched by user id and then
    /// by display name for rows VRChat's log gave no id.
    /// </summary>
    public static string? PlayerThumbnail(
        IEnumerable<InstancePlayerItem> players,
        string userId,
        string displayName)
    {
        foreach (var player in players)
        {
            var match = !string.IsNullOrWhiteSpace(userId)
                ? string.Equals(player.UserId, userId, StringComparison.Ordinal)
                : string.Equals(player.DisplayName, displayName, StringComparison.CurrentCultureIgnoreCase);
            if (match)
            {
                return ImageUrlHelper.PersonThumbnailUrl(player.User);
            }
        }

        return null;
    }

    /// <summary>
    /// True when this event is the signed-in account's. When both ids are known the id
    /// decides, so another player with the same display name is never tagged as you.
    /// </summary>
    private bool IsSelf(VrChatLogActivity entry)
    {
        if (string.IsNullOrWhiteSpace(entry.DisplayName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(_selfUserId) && !string.IsNullOrWhiteSpace(entry.UserId))
        {
            return string.Equals(entry.UserId, _selfUserId, StringComparison.Ordinal);
        }

        return !string.IsNullOrWhiteSpace(_selfDisplayName) &&
               string.Equals(entry.DisplayName, _selfDisplayName, StringComparison.CurrentCultureIgnoreCase);
    }

    private void Rebuild(bool resetPage)
    {
        var filter = _selectedActivityFilter?.Key ?? "all";
        var next = new List<InstanceActivityItem>(_events.Count);
        var kept = new Dictionary<string, InstanceActivityItem>(_events.Count, StringComparer.Ordinal);
        foreach (var entry in _events)
        {
            if (!MatchesFilter(entry.Kind, filter))
            {
                continue;
            }

            var key = KeyFor(entry);
            if (!_cache.TryGetValue(key, out var item))
            {
                item = new InstanceActivityItem(entry, IsSelf(entry));
            }

            if (item.IsAvatarChange)
            {
                // The row names the avatar it is about, so that avatar's picture is the
                // one it draws. The person's picture only fills the slot when the avatar
                // has no public image - never in place of the avatar.
                if (string.IsNullOrWhiteSpace(entry.UserId) &&
                    _personIds.TryGetValue(PersonKey(entry.UserId, entry.DisplayName), out var resolvedId))
                {
                    item.SetUserId(resolvedId);
                }

                if (_avatarImages.TryGetValue(AvatarKey(item.AvatarName, item.AvatarAuthor), out var image))
                {
                    item.SetAvatarImage(image);
                }

                var person = _personImageResolver?.Invoke(entry.UserId, entry.DisplayName);
                if (string.IsNullOrWhiteSpace(person) &&
                    _personImages.TryGetValue(PersonKey(entry.UserId, entry.DisplayName), out var fetched))
                {
                    person = fetched;
                }

                if (!string.IsNullOrWhiteSpace(person))
                {
                    item.SetPersonImage(person);
                }
            }

            kept[key] = item;
            next.Add(item);
        }

        // The cache holds only what the filter shows; keeping the rest would leak a row
        // for every event the reader never asked to see.
        _cache.Clear();
        foreach (var pair in kept)
        {
            _cache[pair.Key] = pair.Value;
        }

        // Newest first, and OrderByDescending is stable, so lines written in the same
        // second keep the order VRChat wrote them in.
        var ordered = next.OrderByDescending(item => item.Activity.At).ToList();
        var same = Activity.Count == ordered.Count;
        for (var index = 0; same && index < ordered.Count; index++)
        {
            same = ReferenceEquals(Activity[index], ordered[index]);
        }

        if (!same)
        {
            Activity.Clear();
            foreach (var item in ordered)
            {
                Activity.Add(item);
            }
        }

        ActivityPage.SetSource(ordered, resetPage);
        ResolvePageAvatarImages();
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(ActivityText));
    }

    /// <summary>
    /// Resolves a picture for the avatar rows on screen: the switched avatar's picture
    /// first, the person's own picture only when that avatar has none. Each id and each
    /// name is looked up once and reused by every row that carries it.
    /// </summary>
    private void ResolvePageAvatarImages()
    {
        if (_avatarImageResolver is null && _personLookup is null)
        {
            return;
        }

        foreach (var item in ActivityPage.Items)
        {
            if (!item.IsAvatarChange || string.IsNullOrWhiteSpace(item.AvatarName))
            {
                continue;
            }

            // A person picture on the row is only a placeholder: the avatar lookup still
            // has to run, or that fallback would be mistaken for the avatar.

            var userId = item.Activity.UserId;
            var displayName = item.Activity.DisplayName;
            var personKey = PersonKey(userId, displayName);

            var key = AvatarKey(item.AvatarName, item.AvatarAuthor);
            if (_avatarImages.TryGetValue(key, out var knownAvatar))
            {
                item.SetAvatarImage(knownAvatar);
                continue;
            }

            // A person picture already on the row is only a placeholder for this slot.
            if (!item.HasAvatarImage &&
                _personImages.TryGetValue(personKey, out var knownPerson))
            {
                item.SetPersonImage(knownPerson);
            }

            // Both run: the avatar's picture replaces the person fallback when it
            // answers, and the person's picture covers a private or unlisted avatar.
            if (_personLookup is not null &&
                !string.IsNullOrWhiteSpace(displayName) &&
                _personImageLookups.Add(personKey))
            {
                _ = ResolvePersonImageAsync(userId, displayName, personKey);
            }

            if (_avatarImageResolver is not null && _avatarImageLookups.Add(key))
            {
                _ = ResolveAvatarImageAsync(item.AvatarName, item.AvatarAuthor, key);
            }
        }
    }

    private async Task ResolvePersonImageAsync(string userId, string displayName, string key)
    {
        var lookup = _personLookup;
        if (lookup is null)
        {
            return;
        }

        try
        {
            var result = await lookup(userId, displayName);
            var resolvedId = result?.UserId?.Trim();
            var url = result?.ImageUrl?.Trim();
            if (string.IsNullOrWhiteSpace(resolvedId) && string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(resolvedId))
            {
                _personIds[key] = resolvedId;
            }

            if (!string.IsNullOrWhiteSpace(url))
            {
                _personImages[key] = url;
            }

            foreach (var item in Activity)
            {
                if (item.IsAvatarChange &&
                    string.Equals(
                        PersonKey(item.Activity.UserId, item.Activity.DisplayName),
                        key,
                        StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(resolvedId))
                    {
                        item.SetUserId(resolvedId);
                    }

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        item.SetPersonImage(url);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"instance person image {displayName}: {ex.Message}");
        }
    }

    private static string PersonKey(string userId, string displayName)
        => !string.IsNullOrWhiteSpace(userId)
            ? "id:" + userId
            : "name:" + (displayName ?? string.Empty).Trim().ToLowerInvariant();

    private async Task ResolveAvatarImageAsync(string avatarName, string avatarAuthor, string key)
    {
        var resolver = _avatarImageResolver;
        if (resolver is null)
        {
            return;
        }

        try
        {
            var url = await resolver(
                avatarName,
                string.IsNullOrWhiteSpace(avatarAuthor) ? null : avatarAuthor);
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            _avatarImages[key] = url;
            foreach (var item in Activity)
            {
                if (item.IsAvatarChange &&
                    string.Equals(item.AvatarName, avatarName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.AvatarAuthor, avatarAuthor, StringComparison.OrdinalIgnoreCase))
                {
                    item.SetAvatarImage(url);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"instance avatar image {avatarName}: {ex.Message}");
        }
    }

    private static string AvatarKey(string avatarName, string avatarAuthor)
        => avatarName.Trim() + "\u0001" + (avatarAuthor ?? string.Empty).Trim();

    private static bool MatchesFilter(VrChatLogActivityKind kind, string filter) => filter switch
    {
        "joined" => kind == VrChatLogActivityKind.Joined,
        "left" => kind == VrChatLogActivityKind.Left,
        "udon" => kind == VrChatLogActivityKind.Udon,
        "avatar" => kind == VrChatLogActivityKind.AvatarChanged,
        _ => true
    };

    private static string KeyFor(VrChatLogActivity entry)
        => string.Concat(
            ((int)entry.Kind).ToString(CultureInfo.InvariantCulture),
            "|",
            entry.At.UtcTicks.ToString(CultureInfo.InvariantCulture),
            "|",
            entry.RepeatCount.ToString(CultureInfo.InvariantCulture),
            "|",
            entry.UserId,
            "|",
            entry.DisplayName,
            "|",
            entry.Detail);
}
