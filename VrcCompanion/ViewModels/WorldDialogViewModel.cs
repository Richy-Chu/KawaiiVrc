using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

public sealed class InstanceTypeOption
{
    public InstanceTypeOption(string label, string value, bool canRequestInvite, string description)
    {
        Label = label;
        Value = value;
        CanRequestInvite = canRequestInvite;
        Description = description;
    }

    public string Label { get; }
    public string Value { get; }
    public bool CanRequestInvite { get; }
    public string Description { get; }
}

public sealed class RegionOption
{
    public RegionOption(string label, string value, string detail)
    {
        Label = label;
        Value = value;
        Detail = detail;
    }

    public string Label { get; }
    public string Value { get; }
    public string Detail { get; }
}

public sealed class WorldInstanceItem : ViewModelBase
{
    private string? _groupName;
    private bool _groupResolved;
    private bool _isGroupMember;
    private string? _groupMembershipStatus;
    private string? _groupJoinState;

    public WorldInstanceItem(VrcInstance instance, string worldId)
    {
        Instance = instance;
        WorldId = worldId;
        GroupId = ParseGroupId(instance.InstanceId) ?? ParseGroupId(instance.Location);
    }

    public VrcInstance Instance { get; }
    public string WorldId { get; }
    public string InstanceId => Instance.InstanceId;

    /// <summary>Group instances carry their group in the instance id,
    /// e.g. `12345~group(grp_…)~groupAccessType(public)`; other instances have none.</summary>
    public string? GroupId { get; }

    public bool HasGroup => !string.IsNullOrWhiteSpace(GroupId);

    public string? GroupName => _groupName;

    public bool IsGroupMember => _isGroupMember;

    /// <summary>Says what the viewer can do with the group, so the chip is not just "Group".</summary>
    public string GroupStateText => !HasGroup
        ? string.Empty
        : !_groupResolved
            ? "loading…"
            : _isGroupMember
                ? "Joined"
                : string.Equals(_groupMembershipStatus, "requested", StringComparison.OrdinalIgnoreCase)
                    ? "Request pending"
                    : _groupJoinState switch
                    {
                        "open" => "Open to join",
                        "request" => "Request to join",
                        null or "" or "unknown" => "View group",
                        _ => "Invite only"
                    };

    public string GroupChipText => HasGroup ? $"Group · {GroupStateText}" : string.Empty;

    public string Location
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Instance.Location))
            {
                return Instance.Location;
            }
            if (!string.IsNullOrWhiteSpace(Instance.WorldId))
            {
                return $"{Instance.WorldId}:{Instance.InstanceId}";
            }
            return $"{WorldId}:{Instance.InstanceId}";
        }
    }

    public int Occupants => Instance.Occupants;

    public string AccessLabel => InstancePresentation.AccessLabel(Instance.Type, Instance.InstanceId);
    public string RegionLabel => InstancePresentation.RegionLabel(Instance.Region ?? Instance.InstanceId);

    public string OccupancyText
    {
        get
        {
            var capacity = Instance.Capacity > 0 ? Instance.Capacity : Instance.RecommendedCapacity;
            return capacity > 0
                ? $"{Occupants:N0} / {capacity:N0} people"
                : $"{Occupants:N0} people";
        }
    }

    public string DetailText => $"{OccupancyText} · {RegionLabel}";

    public string Title => !string.IsNullOrWhiteSpace(_groupName)
        ? _groupName!
        : string.IsNullOrWhiteSpace(Instance.DisplayName)
            ? $"{AccessLabel} instance"
            : Instance.DisplayName!;

    public bool CanJoin => !string.IsNullOrWhiteSpace(InstanceId);

    /// <summary>Applies what the group lookup learned; null means it could not be read.</summary>
    /// <summary>Keeps an open row honest when the group popup joins, requests or leaves.</summary>
    public void SetGroupMembership(bool isMember, string? membershipStatus = null)
    {
        if (_isGroupMember == isMember &&
            string.Equals(_groupMembershipStatus, membershipStatus, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _isGroupMember = isMember;
        _groupMembershipStatus = membershipStatus;
        OnPropertyChanged(nameof(IsGroupMember));
        OnPropertyChanged(nameof(GroupStateText));
        OnPropertyChanged(nameof(GroupChipText));
    }

    public void ApplyGroup(Group? group)
    {
        _groupResolved = true;
        _groupName = group?.Name;
        _isGroupMember = group?.IsMember == true;
        _groupMembershipStatus = group?.MembershipStatus;
        _groupJoinState = group?.JoinState;
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsGroupMember));
        OnPropertyChanged(nameof(GroupStateText));
        OnPropertyChanged(nameof(GroupChipText));
    }

    private static string? ParseGroupId(string? value)
    {
        const string marker = "~group(";
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var start = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = value.IndexOf(')', start);
        return end <= start ? null : value[start..end];
    }
}

/// <summary>State for the world details overlay.</summary>
public sealed class WorldDialogViewModel : ViewModelBase
{
    private readonly WorldItem _summary;
    private readonly VrcApiClient _api;
    private readonly ImageLoader _images;
    private VrcWorld? _world;
    private ImageSource? _heroImage;
    private InstanceTypeOption _selectedType;
    private RegionOption _selectedRegion;
    private string _joinInstanceInput = string.Empty;
    private VrcInstance? _createdInstance;
    private string? _errorMessage;
    private bool _isLoading;
    private bool _isCreating;
    // Group details do not change while a world is open, so a refresh reuses them
    // instead of re-querying the same group for every rebuilt row.
    private readonly ConcurrentDictionary<string, Group> _groupCache = new(StringComparer.OrdinalIgnoreCase);

    public WorldDialogViewModel(WorldItem summary, VrcApiClient api, ImageLoader images)
    {
        _summary = summary;
        _api = api;
        _images = images;
        HeroImage = summary.Image;

        InstanceTypes = new ObservableCollection<InstanceTypeOption>
        {
            new("Public", "public", false, "Anyone can join."),
            new("Friends+", "hidden", false, "Friends of friends can join."),
            new("Friends", "friends", false, "Only your friends can join."),
            new("Invite+", "private", true, "Invite only, friends can ask."),
            new("Invite", "private", false, "Only invited friends can join.")
        };
        _selectedType = InstanceTypes[0];

        Regions = new ObservableCollection<RegionOption>
        {
            new("US West", "us", "Los Angeles"),
            new("US East", "use", "Virginia"),
            new("Europe", "eu", "Frankfurt"),
            new("Japan", "jp", "Tokyo")
        };
        _selectedRegion = Regions[0];

        Instances = new ObservableCollection<WorldInstanceItem>();
    }

    public WorldItem Summary => _summary;

    public VrcWorld? World
    {
        get => _world;
        private set
        {
            if (SetProperty(ref _world, value))
            {
                OnAllPropertiesChanged();
            }
        }
    }

    public ImageSource? HeroImage
    {
        get => _heroImage;
        private set => SetProperty(ref _heroImage, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(IsNotLoading));
            }
        }
    }

    public bool IsNotLoading => !IsLoading;

    public bool IsCreating
    {
        get => _isCreating;
        private set
        {
            if (SetProperty(ref _isCreating, value))
            {
                OnPropertyChanged(nameof(IsNotCreating));
            }
        }
    }

    public bool IsNotCreating => !IsCreating;

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetProperty(ref _errorMessage, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public ObservableCollection<WorldInstanceItem> Instances { get; }

    public bool HasActiveInstances => Instances.Count > 0;

    public ObservableCollection<InstanceTypeOption> InstanceTypes { get; }

    public InstanceTypeOption SelectedType
    {
        get => _selectedType;
        set
        {
            if (value is not null && SetProperty(ref _selectedType, value))
            {
                OnPropertyChanged(nameof(SelectedTypeDescription));
            }
        }
    }

    public string SelectedTypeDescription => SelectedType.Description;

    public ObservableCollection<RegionOption> Regions { get; }

    public RegionOption SelectedRegion
    {
        get => _selectedRegion;
        set
        {
            if (value is not null)
            {
                SetProperty(ref _selectedRegion, value);
            }
        }
    }

    public string JoinInstanceInput
    {
        get => _joinInstanceInput;
        set => SetProperty(ref _joinInstanceInput, value ?? string.Empty);
    }

    public VrcInstance? CreatedInstance
    {
        get => _createdInstance;
        private set
        {
            if (SetProperty(ref _createdInstance, value))
            {
                OnPropertyChanged(nameof(HasCreatedInstance));
                OnPropertyChanged(nameof(CreatedTitle));
                OnPropertyChanged(nameof(CreatedMeta));
                OnPropertyChanged(nameof(CreatedShareLink));
            }
        }
    }

    public bool HasCreatedInstance => CreatedInstance is not null;

    public string CreatedTitle
    {
        get
        {
            if (CreatedInstance is null)
            {
                return string.Empty;
            }

            var access = InstancePresentation.AccessLabel(CreatedInstance.Type, CreatedInstance.InstanceId);
            var region = InstancePresentation.RegionLabel(CreatedInstance.Region ?? CreatedInstance.InstanceId);
            return $"{access} instance ready · {region}";
        }
    }

    public string CreatedMeta
    {
        get
        {
            if (CreatedInstance is null)
            {
                return string.Empty;
            }

            var shortName = CreatedInstance.ShortName;
            return string.IsNullOrWhiteSpace(shortName)
                ? "Created just now"
                : $"Short code {shortName} · created just now";
        }
    }

    public string CreatedShareLink
    {
        get
        {
            if (CreatedInstance is null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(CreatedInstance.ShortName))
            {
                return $"https://vrch.at/{CreatedInstance.ShortName}";
            }

            return $"https://vrchat.com/home/launch?worldId={Uri.EscapeDataString(WorldId)}&instanceId={Uri.EscapeDataString(CreatedInstance.InstanceId)}";
        }
    }

    public string Name => string.IsNullOrWhiteSpace(World?.Name) ? _summary.Name : World!.Name;

    public string AuthorText => string.IsNullOrWhiteSpace(World?.AuthorName)
        ? _summary.AuthorText
        : $"by {World!.AuthorName}";

    public string WorldId => string.IsNullOrWhiteSpace(World?.Id) ? _summary.Id : World!.Id;

    public string WebUrl => $"https://vrchat.com/home/world/{WorldId}";

    public string Description => string.IsNullOrWhiteSpace(World?.Description)
        ? "No description was provided for this world."
        : World!.Description!.Trim();

    public string PlatformsText
    {
        get
        {
            var platforms = (World?.SupportedPlatforms ?? new List<string>())
                .Select(platform => platform.ToLowerInvariant() switch
                {
                    "standalonewindows" or "standalonewindows64" => "PC",
                    "android" => "Quest",
                    "ios" => "iOS",
                    _ => platform
                })
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return platforms.Length == 0 ? _summary.PlatformsText : string.Join(" · ", platforms);
        }
    }

    public string OccupancyText
    {
        get
        {
            var world = World;
            if (world is null)
            {
                return _summary.MetaText;
            }

            return $"{world.Occupants:N0} online · {world.Capacity:N0} capacity";
        }
    }

    public string VisitsText => World is null ? "—" : World.Visits.ToString("N0", CultureInfo.CurrentCulture);
    public string FavoritesText => World is null ? "—" : World.Favorites.ToString("N0", CultureInfo.CurrentCulture);

    public string ReleaseStatusText => string.IsNullOrWhiteSpace(World?.ReleaseStatus)
        ? "Public"
        : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(World!.ReleaseStatus!.Replace('_', ' '));

    public string CreatedText => World?.PublicationDate is { Length: > 0 } published && published != "none"
        ? $"Published {FormatDate(published)}"
        : World?.CreatedAt is { } created
            ? $"Created {created:MMM yyyy}"
            : string.Empty;

    public bool HasCreatedText => !string.IsNullOrWhiteSpace(CreatedText);

    public string UpdatedText => World?.UpdatedAt is { } updated
        ? $"Updated {TimeText.Relative(updated)}"
        : string.Empty;

    public bool HasUpdatedText => !string.IsNullOrWhiteSpace(UpdatedText);

    public string TagsText
    {
        get
        {
            var tags = (World?.Tags ?? new List<string>())
                .Where(tag => tag.StartsWith("author_tag_", StringComparison.OrdinalIgnoreCase))
                .Select(tag => tag["author_tag_".Length..].Replace('_', ' ').Trim())
                .Where(tag => tag.Length > 0)
                .Select(tag => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();

            return tags.Length == 0 ? "Community world" : string.Join(" · ", tags);
        }
    }

    public bool HasDescription => !string.IsNullOrWhiteSpace(World?.Description);

    public async Task LoadAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var world = await _api.GetWorldAsync(WorldId, forceRefresh, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (world is null)
            {
                ErrorMessage = "VRChat did not return this world's details.";
                return;
            }

            World = world;
            RebuildInstances(cancellationToken);
            _ = LoadHeroImageAsync(world, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed or another world opened.
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat to load this world.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not load the full world details.";
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    public void SetCreatedInstance(VrcInstance instance)
    {
        CreatedInstance = instance;
    }

    public VrcInstance? GetCreatedInstance() => CreatedInstance;

    public async Task<VrcInstance?> CreateInstanceAsync(CancellationToken cancellationToken)
    {
        IsCreating = true;
        ErrorMessage = null;
        try
        {
            var instance = await _api.CreateInstanceAsync(
                WorldId,
                SelectedType.Value,
                SelectedRegion.Value,
                SelectedType.CanRequestInvite,
                cancellationToken);

            CreatedInstance = instance;
            _ = LoadUpdatedInstancesAsync(cancellationToken);
            return instance;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
            return null;
        }
        catch (System.Net.Http.HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat to create the instance.";
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not create that instance.";
            return null;
        }
        finally
        {
            IsCreating = false;
        }
    }

    public void ReportError(string message) => ErrorMessage = message;

    /// <summary>The group lookups the last instance rebuild started, so they can be awaited.</summary>
    internal Task? GroupHydration { get; private set; }

    /// <summary>
    /// Joining or leaving from the group popup invalidates the cached group and updates
    /// the rows already on screen, so a refresh does not resurrect the old membership.
    /// </summary>
    public void ApplyGroupMembership(string groupId, bool isMember, string? membershipStatus = null)
    {
        if (string.IsNullOrWhiteSpace(groupId))
        {
            return;
        }

        _groupCache.TryRemove(groupId, out _);
        foreach (var item in Instances.Where(item =>
                     string.Equals(item.GroupId, groupId, StringComparison.OrdinalIgnoreCase)))
        {
            item.SetGroupMembership(isMember, membershipStatus);
        }
    }

    private async Task LoadUpdatedInstancesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var world = await _api.GetWorldAsync(WorldId, forceRefresh: true, cancellationToken);
            if (cancellationToken.IsCancellationRequested || world is null)
            {
                return;
            }

            World = world;
            RebuildInstances(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void RebuildInstances(CancellationToken cancellationToken = default)
    {
        Instances.Clear();
        if (World is null)
        {
            OnPropertyChanged(nameof(HasActiveInstances));
            return;
        }

        var added = new List<WorldInstanceItem>();
        foreach (var instance in World.SlimInstances
                     .Where(item => !string.IsNullOrWhiteSpace(item.InstanceId))
                     .OrderByDescending(item => item.Occupants)
                     .Take(12))
        {
            var item = new WorldInstanceItem(instance, World.Id);
            Instances.Add(item);
            added.Add(item);
        }

        OnPropertyChanged(nameof(HasActiveInstances));
        GroupHydration = HydrateGroupsAsync(added, cancellationToken);
    }

    /// <summary>
    /// Instances only name their group by id, so resolve each group once and let every
    /// row using it show the real name and the viewer's membership.
    /// </summary>
    private async Task HydrateGroupsAsync(
        IReadOnlyCollection<WorldInstanceItem> items,
        CancellationToken cancellationToken)
    {
        var pending = new List<IGrouping<string, WorldInstanceItem>>();
        foreach (var grouping in items
                     .Where(item => item.HasGroup)
                     .GroupBy(item => item.GroupId!, StringComparer.OrdinalIgnoreCase))
        {
            // A refresh rebuilds every row, so a group this dialog already read is
            // applied straight from the cache instead of being fetched again.
            if (_groupCache.TryGetValue(grouping.Key, out var cached))
            {
                foreach (var item in grouping)
                {
                    item.ApplyGroup(cached);
                }

                continue;
            }

            pending.Add(grouping);
        }

        if (pending.Count == 0)
        {
            return;
        }

        try
        {
            using var gate = new SemaphoreSlim(4);
            await Task.WhenAll(pending.Select(async grouping =>
            {
                await gate.WaitAsync(cancellationToken);
                try
                {
                    Group? info = null;
                    try
                    {
                        info = await _api.GetGroupAsync(grouping.Key, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"world dialog group {grouping.Key}: {ex.Message}");
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // Only a real answer is cached; a failed lookup is retried on the
                    // next refresh. Either way the rows resolve, so the chip stops saying
                    // "loading" and falls back to opening the group.
                    if (info is not null)
                    {
                        _groupCache[grouping.Key] = info;
                    }

                    foreach (var item in grouping)
                    {
                        item.ApplyGroup(info);
                    }
                }
                finally
                {
                    gate.Release();
                }
            }));
        }
        catch (OperationCanceledException)
        {
            // Dialog closed while the groups were loading.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task LoadHeroImageAsync(VrcWorld world, CancellationToken cancellationToken)
    {
        var url = !string.IsNullOrWhiteSpace(world.ImageUrl) ? world.ImageUrl : world.ThumbnailImageUrl;
        try
        {
            var image = await _images.LoadAsync(url, 900, cancellationToken);
            if (!cancellationToken.IsCancellationRequested && image is not null)
            {
                HeroImage = image;
            }
        }
        catch (OperationCanceledException)
        {
            // Dialog closed.
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private static string FormatDate(string iso)
    {
        return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value.ToString("MMM yyyy", CultureInfo.CurrentCulture)
            : string.Empty;
    }
}

internal static class InstancePresentation
{
    public static string AccessLabel(string? type, string? instanceId)
    {
        var value = instanceId ?? string.Empty;
        if (value.Contains("~hidden(", StringComparison.OrdinalIgnoreCase)) return "Friends+";
        if (value.Contains("~friends(", StringComparison.OrdinalIgnoreCase)) return "Friends";
        if (value.Contains("~private(", StringComparison.OrdinalIgnoreCase))
        {
            return value.Contains("canRequestInvite", StringComparison.OrdinalIgnoreCase)
                ? "Invite+"
                : "Invite";
        }
        if (value.Contains("~group(", StringComparison.OrdinalIgnoreCase)) return "Group";

        return type?.ToLowerInvariant() switch
        {
            "hidden" => "Friends+",
            "friends" => "Friends",
            "private" => "Invite",
            "group" => "Group",
            _ => "Public"
        };
    }

    public static string RegionLabel(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Contains("~region(use)", StringComparison.OrdinalIgnoreCase)) return "US East";
        if (text.Contains("~region(us)", StringComparison.OrdinalIgnoreCase)) return "US West";
        if (text.Contains("~region(eu)", StringComparison.OrdinalIgnoreCase)) return "Europe";
        if (text.Contains("~region(jp)", StringComparison.OrdinalIgnoreCase)) return "Japan";

        return text.ToLowerInvariant() switch
        {
            "us" => "US West",
            "use" => "US East",
            "eu" => "Europe",
            "jp" => "Japan",
            _ => "Automatic"
        };
    }
}
