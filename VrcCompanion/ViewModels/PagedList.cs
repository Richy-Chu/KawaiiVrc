using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Non-generic view of a paged list, so the shared pager can find the rows it
/// pages and follow its page token without knowing the row type.
/// </summary>
public interface IPagedList
{
    IEnumerable Items { get; }

    int PageToken { get; }
}

/// <summary>
/// A window of a longer list with Previous and Next. Lists that used to hand
/// every row to a panel kept a visual, a decoded thumbnail and a binding for
/// each one; paging bounds that work to one page at a time whether or not the
/// panel is virtualizing. The source is kept as a snapshot so a page flip never
/// re-filters or re-sorts the underlying collection.
/// </summary>
public sealed class PagedList<T> : ViewModelBase, IPagedList
{
    private readonly List<T> _source = new();
    private readonly RelayCommand _previousCommand;
    private readonly RelayCommand _nextCommand;
    private int _pageIndex;
    private int _pageToken;
    private int _publishedPageIndex = -1;

    public PagedList(int pageSize = 24)
    {
        PageSize = Math.Max(1, pageSize);
        _previousCommand = new RelayCommand(Previous, () => HasPrevious);
        _nextCommand = new RelayCommand(Next, () => HasNext);
    }

    /// <summary>The rows on the current page; bind the list to this.</summary>
    public ObservableCollection<T> Items { get; } = new();

    IEnumerable IPagedList.Items => Items;

    /// <summary>
    /// Bumped when the visible page changes. The pager scrolls the list back to
    /// its top when it moves; a refresh that keeps the same page does not move
    /// it, so a live update never yanks the reader away from what they read.
    /// </summary>
    public int PageToken => _pageToken;

    public int PageSize { get; }

    public int PageCount => _source.Count == 0
        ? 0
        : ((_source.Count - 1) / PageSize) + 1;

    /// <summary>Only worth showing the pager when there is another page to reach.</summary>
    public bool HasPages => PageCount > 1;

    public bool HasPrevious => _pageIndex > 0;

    public bool HasNext => _pageIndex + 1 < PageCount;

    public string PageText => PageCount == 0
        ? "0 of 0"
        : $"{_pageIndex + 1} of {PageCount}";

    public int PageNumber => PageCount == 0 ? 0 : _pageIndex + 1;

    public RelayCommand PreviousCommand => _previousCommand;

    public RelayCommand NextCommand => _nextCommand;

    /// <summary>
    /// Publishes a new snapshot. A filter or a fresh load resets to the first
    /// page; a presence refresh keeps the page the user is reading unless the
    /// list shrank past it.
    /// </summary>
    public void SetSource(IEnumerable<T> items, bool resetPage = false)
    {
        _source.Clear();
        _source.AddRange(items);

        if (resetPage)
        {
            _pageIndex = 0;
        }
        else if (_pageIndex >= PageCount)
        {
            _pageIndex = Math.Max(0, PageCount - 1);
        }

        Publish();
    }

    public void Clear() => SetSource(Array.Empty<T>(), resetPage: true);

    /// <summary>Goes back to the first page without touching the rows.</summary>
    public void ResetPage()
    {
        if (_pageIndex == 0)
        {
            return;
        }

        _pageIndex = 0;
        Publish();
    }

    private void Previous()
    {
        if (!HasPrevious)
        {
            return;
        }

        _pageIndex--;
        Publish();
    }

    private void Next()
    {
        if (!HasNext)
        {
            return;
        }

        _pageIndex++;
        Publish();
    }

    private void Publish()
    {
        var start = _pageIndex * PageSize;
        var take = Math.Max(0, Math.Min(PageSize, _source.Count - start));

        Items.Clear();
        for (var index = 0; index < take; index++)
        {
            Items.Add(_source[start + index]);
        }

        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(HasPages));
        OnPropertyChanged(nameof(HasPrevious));
        OnPropertyChanged(nameof(HasNext));
        OnPropertyChanged(nameof(PageText));
        OnPropertyChanged(nameof(PageNumber));

        if (_publishedPageIndex != _pageIndex)
        {
            _publishedPageIndex = _pageIndex;
            _pageToken++;
            OnPropertyChanged(nameof(PageToken));
        }

        _previousCommand.RaiseCanExecuteChanged();
        _nextCommand.RaiseCanExecuteChanged();
    }
}
