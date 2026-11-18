using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

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
        if (d is ScrollViewer viewer)
        {
            viewer.ScrollToTop();
        }
    }

    /// <summary>
    /// Scrolls the nearest scrollable surface under the pointer and, when that
    /// surface is already at its edge, continues outward to the containing page.
    /// Returns true when a surface consumed the wheel input.
    /// </summary>
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
