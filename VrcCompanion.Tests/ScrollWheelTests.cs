using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Behaviors;
using Xunit;

namespace VrcCompanion.Tests;

public class ScrollWheelTests
{
    [Fact]
    public void WheelAtTheInnerListEdgeContinuesToTheOuterPage()
    {
        WpfTestHost.Run(() =>
        {
            var outer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        new Border { Height = 300, Background = Brushes.Transparent },
                        new ListBox
                        {
                            Height = 100,
                            ItemsSource = Enumerable.Range(0, 40).Select(index => $"Row {index}").ToList()
                        },
                        new Border { Height = 600, Background = Brushes.Transparent }
                    }
                }
            };
            var window = new Window
            {
                Content = outer,
                Width = 320,
                Height = 240,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                var inner = Assert.IsType<ListBox>(
                    Assert.IsType<StackPanel>(outer.Content).Children[1]);
                inner.ScrollIntoView(inner.Items[^1]);
                Pump();

                var innerScroll = Assert.Single(FindVisualChildren<ScrollViewer>(inner));
                Assert.True(innerScroll.ScrollableHeight > 0, "The inner list should scroll.");
                Assert.True(
                    innerScroll.VerticalOffset >= innerScroll.ScrollableHeight - 0.5,
                    "The inner list should be at its end before the wheel test.");

                var lastRow = Assert.IsType<ListBoxItem>(
                    inner.ItemContainerGenerator.ContainerFromIndex(inner.Items.Count - 1));
                var before = outer.VerticalOffset;

                Assert.True(ScrollViewerBehavior.HandleMouseWheel(lastRow, -120));
                window.UpdateLayout();

                Assert.True(
                    outer.VerticalOffset > before,
                    $"The outer page should scroll when the inner list is at its edge. " +
                    $"before={before}, after={outer.VerticalOffset}, scrollable={outer.ScrollableHeight}.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T match)
        {
            yield return match;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }
}
