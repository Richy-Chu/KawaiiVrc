using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using VrcCompanion.Behaviors;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Controls;

/// <summary>
/// Previous / page / Next for a <see cref="ViewModels.PagedList{T}"/>; bind its
/// DataContext to the page and its Visibility to HasPages. The pager also puts
/// the list back at its first row whenever the page changes, so Next never opens
/// half way down a page the reader had scrolled.
/// </summary>
public partial class ListPager : UserControl
{
    private INotifyPropertyChanged? _page;

    public ListPager()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach();
        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
    }

    private void Attach()
    {
        Detach();
        if (DataContext is not INotifyPropertyChanged page)
        {
            return;
        }

        _page = page;
        page.PropertyChanged += OnPagePropertyChanged;
    }

    private void Detach()
    {
        if (_page is null)
        {
            return;
        }

        _page.PropertyChanged -= OnPagePropertyChanged;
        _page = null;
    }

    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!string.Equals(e.PropertyName, nameof(IPagedList.PageToken), StringComparison.Ordinal))
        {
            return;
        }

        ScrollViewerBehavior.ResetPageScroll(this, (DataContext as IPagedList)?.Items);
    }
}
