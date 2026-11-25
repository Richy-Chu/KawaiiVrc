using System;
using System.Collections;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace VrcCompanion.Behaviors;

/// <summary>
/// Scrolls a ScrollViewer back to the top whenever the bound token changes, so a
/// new page of results never opens at the previous page's scroll offset.
/// </summary>
public static class ScrollViewerBehavior
{
    public static readonly DependencyProperty PageTokenProperty = DependencyProperty.RegisterAttached(
        "PageToken",
        typeof(int),
        typeof(ScrollViewerBehavior),
        new PropertyMetadata(0, OnPageTokenChanged));

    public static int GetPageToken(DependencyObject element)
        => (int)element.GetValue(PageTokenProperty);

    public static void SetPageToken(DependencyObject element, int value)
        => element.SetValue(PageTokenProperty, value);

    private static void OnPageTokenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // A page may bind the token on its own ScrollViewer or on the list that
        // owns the ScrollViewer inside its template.
        var viewer = d as ScrollViewer ?? FindScrollViewer(d);
        if (viewer is null)
        {
            return;
        }

        ResetScroll(viewer);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            if (FindScrollViewer(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>
    /// Scrolls the nearest scrollable surface under the pointer and, when that
    /// surface is already at its edge, continues outward to the containing page.
    /// Returns true when a surface consumed the wheel input.
    /// </summary>
    /// <summary>
    /// Puts every scroll surface that shows a page back at the top: the list bound
    /// to the page's rows and any ScrollViewer around the pager. The reset runs now
    /// and again once the new rows have been laid out.
    /// </summary>
    public static void ResetPageScroll(DependencyObject anchor, IEnumerable? items)
    {
        var viewers = new HashSet<ScrollViewer>();
        if (items is not null)
        {
            CollectListedViewers((DependencyObject?)Window.GetWindow(anchor) ?? anchor, items, viewers);
        }

        for (var current = GetVisualOrLogicalParent(anchor);
             current is not null;
             current = GetVisualOrLogicalParent(current))
        {
            if (current is ScrollViewer host)
            {
                viewers.Add(host);
            }
        }

        foreach (var viewer in viewers)
        {
            ResetScroll(viewer);
        }
    }

    private static void CollectListedViewers(
        DependencyObject root,
        IEnumerable items,
        HashSet<ScrollViewer> viewers)
    {
        var pending = new Stack<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is ItemsControl list &&
                ReferenceEquals(list.ItemsSource, items) &&
                FindScrollViewer(list) is { } viewer)
            {
                viewers.Add(viewer);
            }

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++)
            {
                pending.Push(VisualTreeHelper.GetChild(current, index));
            }
        }
    }

    private static void ResetScroll(ScrollViewer viewer)
    {
        viewer.ScrollToTop();

        // The rows of the new page are usually published just after the token moves, and the list
        // then lays out with whatever row it still has selected or focused - which pulls the
        // viewport back to that row, so the page appeared to open half way down. Reset again once
        // that layout pass is over.
        viewer.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => viewer.ScrollToTop()));
    }

    public static bool HandleMouseWheel(DependencyObject? source, int delta)
    {
        if (source is null || delta == 0)
        {
            return false;
        }

        var viewer = FindAncestor<ScrollViewer>(source);
        while (viewer is not null)
        {
            if (viewer.ScrollableHeight > 0.5)
            {
                var atTop = viewer.VerticalOffset <= 0.5;
                var atBottom = viewer.VerticalOffset >= viewer.ScrollableHeight - 0.5;
                if ((delta > 0 && !atTop) || (delta < 0 && !atBottom))
                {
                    var lines = SystemParameters.WheelScrollLines;
                    if (lines == 0)
                    {
                        return true;
                    }

                    var steps = lines < 0 ? 3 : Math.Min(lines, 10);
                    for (var index = 0; index < steps; index++)
                    {
                        if (delta > 0)
                        {
                            viewer.LineUp();
                        }
                        else
                        {
                            viewer.LineDown();
                        }
                    }

                    return true;
                }
            }

            viewer = FindAncestor<ScrollViewer>(GetVisualOrLogicalParent(viewer));
        }

        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? current)
        where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = GetVisualOrLogicalParent(current);
        }

        return null;
    }

    private static DependencyObject? GetVisualOrLogicalParent(DependencyObject current)
    {
        if (current is Visual || current is Visual3D)
        {
            return VisualTreeHelper.GetParent(current);
        }

        return current is ContentElement content
            ? ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent
            : null;
    }
}
