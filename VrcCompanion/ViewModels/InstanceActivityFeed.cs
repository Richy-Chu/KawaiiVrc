using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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

    public InstanceActivityFeed()
    {
        _selectedActivityFilter = ActivityFilters[0];
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
        new FilterOption("udon", "Udon")
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
                item = new InstanceActivityItem(entry);
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
        OnPropertyChanged(nameof(HasActivity));
        OnPropertyChanged(nameof(ActivityText));
    }

    private static bool MatchesFilter(VrChatLogActivityKind kind, string filter) => filter switch
    {
        "joined" => kind == VrChatLogActivityKind.Joined,
        "left" => kind == VrChatLogActivityKind.Left,
        "udon" => kind == VrChatLogActivityKind.Udon,
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
