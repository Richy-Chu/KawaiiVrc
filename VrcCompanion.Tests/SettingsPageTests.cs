using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Controls;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class SettingsPageTests
{
    [Fact]
    public void SettingsPageIsSplitIntoCategories()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);

            try
            {
                window.Show();
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "settings");
                window.UpdateLayout();
                Pump();

                var tabs = FindVisualChildren<TabControl>(window)
                    .Single(control => control.Items.OfType<TabItem>().Any());
                var headers = tabs.Items
                    .OfType<TabItem>()
                    .Select(item => item.Header as string)
                    .ToList();

                Assert.Equal(
                    new[] { "System", "Interface", "Social", "Browser", "Advanced" },
                    headers);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TextSelectionToggleIsOnTheInterfaceTabAndPersists()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var window = CreateWindow(shell);

            try
            {
                window.Show();
                shell.SelectedSection = shell.Sections.Single(section => section.Key == "settings");
                window.UpdateLayout();
                Pump();

                var tabs = FindVisualChildren<TabControl>(window)
                    .Single(control => control.Items.OfType<TabItem>().Any());
                tabs.SelectedItem = tabs.Items
                    .OfType<TabItem>()
                    .Single(item => Equals(item.Header, "Interface"));
                window.UpdateLayout();
                Pump();

                var toggle = FindVisualChildren<CheckBox>(window)
                    .Single(box => Equals(box.Content, "Let me highlight and copy text across the app"));
                Assert.False(Assert.IsType<bool>(toggle.IsChecked));
                Assert.False(TextSelection.IsEnabled);

                toggle.IsChecked = true;
                window.UpdateLayout();

                Assert.True(shell.EnableTextSelection);
                Assert.True(TextSelection.IsEnabled);
            }
            finally
            {
                shell.EnableTextSelection = false;
                window.Close();
            }
        });
    }

    [Fact]
    public void SelectionMapsPointsToCharactersAndCopiesTheRange()
    {
        WpfTestHost.Run(() =>
        {
            var block = new TextBlock
            {
                Text = "Booped you with Exclamation.",
                FontSize = 12,
                Width = 240,
                TextWrapping = TextWrapping.Wrap
            };
            var window = new Window
            {
                Width = 320,
                Height = 200,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = 100,
                Top = 100,
                Content = new Grid { Children = { block } }
            };

            try
            {
                window.Show();
                window.UpdateLayout();
                Pump();

                var layout = new TextLayoutMirror(block, TextSelection.GetText(block));
                Assert.Equal(0, layout.CaretIndexAt(new Point(-4, 6)));
                Assert.True(layout.CaretIndexAt(new Point(200, 6)) > 10);
                Assert.Equal((0, 6), layout.WordAt(1));

                TextSelection.IsEnabled = true;
                Assert.Equal("Booped", TextSelection.SelectRange(block, 0, 6));
                Assert.Equal("Booped", TextSelection.SelectedText);

                TextSelection.ClearSelection();
                Assert.Null(TextSelection.SelectedText);
            }
            finally
            {
                TextSelection.IsEnabled = false;
                window.Close();
            }
        });
    }

    private static MainWindow CreateWindow(MainViewModel shell)
        => new(shell, initializeOnLoad: false)
        {
            Width = 1100,
            Height = 720,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 100,
            Top = 100
        };

    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(
            DispatcherPriority.Background,
            new Action(() => { }));
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
