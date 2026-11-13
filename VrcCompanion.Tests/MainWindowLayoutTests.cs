using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VrcCompanion.Controls;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class MainWindowLayoutTests
{
    [Fact]
    public void SidebarNavScrollsSettingsIntoViewAtConstrainedHeight()
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var window = new MainWindow(shell, initializeOnLoad: false)
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

            try
            {
                window.Show();
                window.UpdateLayout();

                var settings = shell.Sections.Single(section => section.Key == "settings");
                var nav = FindVisualChildren<ListBox>(window)
                    .Single(list => list.Items.Count == shell.Sections.Count &&
                                    list.Items.Contains(settings));

                nav.ScrollIntoView(settings);
                nav.UpdateLayout();

                var scroll = FindVisualChildren<ScrollViewer>(nav).First();
                Assert.Equal(ScrollBarVisibility.Auto, scroll.VerticalScrollBarVisibility);
                Assert.True(scroll.ScrollableHeight > 0, "The constrained sidebar should be scrollable.");
                Assert.True(scroll.VerticalOffset > 0, "Settings should be below the initial sidebar viewport.");

                var settingsItem = Assert.IsType<ListBoxItem>(
                    nav.ItemContainerGenerator.ContainerFromItem(settings));
                Assert.True(settingsItem.ActualHeight > 0, "The Settings row should have been laid out.");

                var bounds = settingsItem
                    .TransformToAncestor(scroll)
                    .TransformBounds(new Rect(0, 0, settingsItem.ActualWidth, settingsItem.ActualHeight));
                Assert.True(bounds.Top >= -1, "Settings should not be clipped above the sidebar viewport.");
                Assert.True(
                    bounds.Bottom <= scroll.ActualHeight + 1,
                    $"Settings should be fully visible after scrolling the sidebar. " +
                    $"Bounds={bounds}, actual={scroll.ActualHeight}, viewport={scroll.ViewportHeight}, " +
                    $"offset={scroll.VerticalOffset}, scrollable={scroll.ScrollableHeight}.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("brave", "BraveBrowserPane", "BraveFocusToggle")]
    [InlineData("discord", "DiscordBrowserPane", "DiscordFocusToggle")]
    [InlineData("resources", "CreatorBrowserPane", "CreatorFocusToggle")]
    public void FocusModeGivesTheBrowserTheWholePane(
        string sectionKey,
        string paneTag,
        string toggleTag)
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var browserSection = shell.Sections.Single(section => section.Key == sectionKey);
            var profileSection = shell.Sections.Single(section => section.Key == "profile");
            var window = new MainWindow(shell, initializeOnLoad: false)
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

            try
            {
                window.Show();
                window.UpdateLayout();

                shell.SelectedSection = browserSection;
                window.UpdateLayout();

                if (sectionKey == "resources")
                {
                    shell.CreatorResourcesViewModel.ShowBrowserForLayoutTest();
                    window.UpdateLayout();
                }

                var pane = FindVisualChildren<Border>(window)
                    .Single(element => Equals(element.Tag, paneTag));
                var toggle = FindVisualChildren<ToggleButton>(window)
                    .Single(element => Equals(element.Tag, toggleTag));
                var focusToolbar = FindVisualChildren<Border>(window)
                    .Single(element => Equals(element.Tag, "BrowserFocusToolbar"));
                var normalToolbar = FindVisualChildren<StackPanel>(window)
                    .Single(element => Equals(element.Tag, "BrowserNormalToolbar"));
                var expander = FindVisualChildren<Expander>(window).SingleOrDefault();
                var appHeader = (Border)window.FindName("AppHeader");
                var appFooter = (Border)window.FindName("AppFooter");

                void AssertChrome(Visibility expected)
                {
                    Assert.Equal(expected, appHeader.Visibility);
                    Assert.Equal(expected, appFooter.Visibility);
                }

                var normalHeight = pane.ActualHeight;
                toggle.IsChecked = true;
                window.UpdateLayout();

                Assert.True(
                    pane.ActualHeight > normalHeight + 20,
                    $"Focus mode should give {sectionKey} more height. " +
                    $"Before={normalHeight:F1}, after={pane.ActualHeight:F1}.");
                if (expander is not null)
                {
                    Assert.Equal(Visibility.Collapsed, expander.Visibility);
                }
                AssertChrome(Visibility.Collapsed);
                Assert.Equal(Visibility.Visible, focusToolbar.Visibility);
                Assert.Equal(Visibility.Collapsed, normalToolbar.Visibility);

                shell.SelectedSection = profileSection;
                window.UpdateLayout();
                AssertChrome(Visibility.Visible);

                shell.SelectedSection = browserSection;
                window.UpdateLayout();
                Assert.True(toggle.IsChecked == true, "Focus should remain checked when returning to the browser.");
                Assert.True(
                    pane.ActualHeight > normalHeight + 20,
                    $"Focus mode should reapply {sectionKey} height after returning. " +
                    $"Before={normalHeight:F1}, after={pane.ActualHeight:F1}.");
                AssertChrome(Visibility.Collapsed);

                var escape = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                window.RaiseEvent(escape);
                window.UpdateLayout();
                Assert.True(escape.Handled, "Escape should exit focus mode.");
                Assert.False(toggle.IsChecked == true, "Escape should clear the focus toggle.");
                AssertChrome(Visibility.Visible);
                Assert.Equal(Visibility.Collapsed, focusToolbar.Visibility);
                Assert.Equal(Visibility.Visible, normalToolbar.Visibility);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void FocusToolbarShowsPageContextAndNavigationAvailability()
    {
        WpfTestHost.Run(() =>
        {
            var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            var browserSection = shell.Sections.Single(section => section.Key == "brave");
            var window = new MainWindow(shell, initializeOnLoad: false)
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

            try
            {
                window.Show();
                shell.SelectedSection = browserSection;
                shell.BraveBrowserViewModel.SetBrowserLocation(
                    "https://example.com/focus",
                    "Focus page");
                shell.BraveBrowserViewModel.IsFocusMode = true;
                window.UpdateLayout();

                var focusToolbar = FindVisualChildren<Border>(window)
                    .Single(element => Equals(element.Tag, "BrowserFocusToolbar"));
                var labels = FindVisualChildren<TextBlock>(focusToolbar)
                    .Select(text => text.Text)
                    .ToArray();
                Assert.Contains("Focus page", labels);
                var address = FindVisualChildren<TextBox>(focusToolbar)
                    .Single(textBox =>
                        AutomationProperties.GetAutomationId(textBox) == "BrowserFocusAddressBox");
                Assert.Equal("https://example.com/focus", address.Text);

                var back = FindVisualChildren<Button>(focusToolbar)
                    .Single(button =>
                        AutomationProperties.GetAutomationId(button) == "BrowserFocusBackButton");
                var forward = FindVisualChildren<Button>(focusToolbar)
                    .Single(button =>
                        AutomationProperties.GetAutomationId(button) == "BrowserFocusForwardButton");
                Assert.False(back.IsEnabled);
                Assert.False(forward.IsEnabled);

                var browser = FindVisualChildren<EmbeddedBrowser>(window)
                    .Single(element => element.IsVisible);
                browser.SetValue(EmbeddedBrowser.CanGoBackProperty, true);
                browser.SetValue(EmbeddedBrowser.CanGoForwardProperty, true);
                window.UpdateLayout();

                Assert.True(back.IsEnabled);
                Assert.True(forward.IsEnabled);
            }
            finally
            {
                window.Close();
            }
        });
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
