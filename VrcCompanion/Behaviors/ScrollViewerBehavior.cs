using System.Windows;
using System.Windows.Controls;

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
}
