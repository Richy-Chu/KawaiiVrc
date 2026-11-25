using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Behaviors;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A page of results is read from its first row. Clicking Next after scrolling must not leave the
/// viewport where the previous page was, however that page was left.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class PagingScrollTests
{
    [Fact]
    public void ANewPageStartsAtTheTopWhenTheOldPageLeftARowSelected()
    {
        WpfTestHost.Run(() =>
        {
            var rows = new ObservableCollection<string>(Enumerable.Range(0, 80).Select(Row));
            var list = new ListBox { ItemsSource = rows, Height = 200 };
            var window = new Window
            {
                Content = list,
                Width = 260,
                Height = 220,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                Pump();

                var viewer = Assert.Single(FindVisualChildren<ScrollViewer>(list));
                Assert.True(viewer.ScrollableHeight > 0, "the list should scroll");

                // Scroll down and click a row, the way a page is left before Next is pressed.
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * 0.6);
                window.UpdateLayout();
                Pump();
                list.SelectedIndex = 30;
                window.UpdateLayout();
                Pump();

                // Keep looking at the rest of the page after the click.
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight * 0.6);
                window.UpdateLayout();
                Pump();
                // A ListBox scrolls by item, so the offset here is a row number, not pixels.
                Assert.True(
                    viewer.VerticalOffset > viewer.ScrollableHeight * 0.5,
                    $"the page should be scrolled before paging, was {viewer.VerticalOffset} " +
                    $"of {viewer.ScrollableHeight}");

                // What a pager does: the page's rows are replaced, then the token moves.
                rows.Clear();
                foreach (var index in Enumerable.Range(100, 80))
                {
                    rows.Add(Row(index));
                }

                ScrollViewerBehavior.SetPageToken(list, 1);

                window.UpdateLayout();
                Pump();
                window.UpdateLayout();
                Pump();

                Assert.True(
                    viewer.VerticalOffset < 1,
                    $"the new page started at {viewer.VerticalOffset}, not at the top");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ANewPageStartsAtTheTopWhenTheRowsArriveAfterTheToken()
    {
        WpfTestHost.Run(() =>
        {
            var rows = new ObservableCollection<string>(Enumerable.Range(0, 80).Select(Row));
            var list = new ListBox { ItemsSource = rows, Height = 200 };
            var window = new Window
            {
                Content = list,
                Width = 260,
                Height = 220,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                Pump();

                var viewer = Assert.Single(FindVisualChildren<ScrollViewer>(list));
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
                window.UpdateLayout();
                Pump();
                Assert.True(viewer.VerticalOffset > viewer.ScrollableHeight * 0.5);

                // The pager's own order: the token moves first, the new rows arrive after it.
                ScrollViewerBehavior.SetPageToken(list, 1);
                window.UpdateLayout();
                Pump();

                rows.Clear();
                foreach (var index in Enumerable.Range(100, 80))
                {
                    rows.Add(Row(index));
                }

                window.UpdateLayout();
                Pump();
                window.UpdateLayout();
                Pump();

                Assert.True(
                    viewer.VerticalOffset < 1,
                    $"the new page started at {viewer.VerticalOffset}, not at the top");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ANewPageStartsAtTheTopEvenWhenTheNextButtonHasFocus()
    {
        WpfTestHost.Run(() =>
        {
            var rows = new StackPanel();
            for (var index = 0; index < 80; index++)
            {
                rows.Children.Add(new Border { Height = 40 });
            }

            var next = new Button { Content = "Next", Height = 30 };
            var viewer = new ScrollViewer
            {
                Content = new StackPanel { Children = { rows, next } },
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var window = new Window
            {
                Content = viewer,
                Width = 260,
                Height = 220,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                Pump();

                // Reading the bottom of a page means the Next button is what has focus.
                viewer.ScrollToVerticalOffset(viewer.ScrollableHeight);
                window.UpdateLayout();
                Pump();
                next.Focus();
                window.UpdateLayout();
                Pump();
                Assert.True(viewer.ScrollableHeight > 200, "the page should scroll");

                ScrollViewerBehavior.SetPageToken(viewer, 1);

                window.UpdateLayout();
                Pump();
                window.UpdateLayout();
                Pump();

                Assert.True(
                    viewer.VerticalOffset < 1,
                    $"the new page started at {viewer.VerticalOffset}, not at the top");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void ANewPageStartsAtTheTopOfItsOwnScrollViewer()
    {
        WpfTestHost.Run(() =>
        {
            var panel = new StackPanel();
            for (var index = 0; index < 80; index++)
            {
                panel.Children.Add(new Border { Height = 40 });
            }

            var viewer = new ScrollViewer
            {
                Content = panel,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var window = new Window
            {
                Content = viewer,
                Width = 260,
                Height = 220,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                Pump();

                viewer.ScrollToVerticalOffset(1200);
                window.UpdateLayout();
                Pump();
                Assert.True(viewer.VerticalOffset > 1000);

                ScrollViewerBehavior.SetPageToken(viewer, 1);

                window.UpdateLayout();
                Pump();

                Assert.True(
                    viewer.VerticalOffset < 1,
                    $"the new page started at {viewer.VerticalOffset}, not at the top");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static string Row(int index) => $"Row {index}";

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
