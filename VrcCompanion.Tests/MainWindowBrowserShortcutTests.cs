using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using VrcCompanion.Controls;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class MainWindowBrowserShortcutTests
{
    [Fact]
    public void FocusShortcutsDriveTheVisibleBrowser()
    {
        WpfTestHost.RunAsync(async () =>
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
                shell.BraveBrowserViewModel.IsFocusMode = true;
                window.UpdateLayout();

                Assert.True(shell.IsAnyBrowserFocused, "The browser should be in focus mode.");
                var browser = FindVisualChildren<EmbeddedBrowser>(window)
                    .Single(element => element.IsVisible);
                await browser.InitializeForTestAsync();

                var core = browser.Browser.CoreWebView2
                    ?? throw new InvalidOperationException("WebView2 did not initialize.");

                async Task NavigateAsync(Action navigate)
                {
                    var completed = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnNavigationCompleted(
                        object? sender,
                        CoreWebView2NavigationCompletedEventArgs e)
                    {
                        core.NavigationCompleted -= OnNavigationCompleted;
                        completed.TrySetResult(e);
                    }

                    core.NavigationCompleted += OnNavigationCompleted;
                    try
                    {
                        navigate();
                    }
                    catch
                    {
                        core.NavigationCompleted -= OnNavigationCompleted;
                        throw;
                    }

                    var navigation = await completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    Assert.True(
                        navigation.IsSuccess,
                        $"The test page failed to load: {navigation.WebErrorStatus}.");
                }

                await NavigateAsync(() =>
                    core.NavigateToString("<html><body>First page</body></html>"));
                await NavigateAsync(() =>
                    core.NavigateToString("<html><body>Second page</body></html>"));
                Assert.True(browser.CanGoBack, "The second page should have Back history.");

                await NavigateAsync(() =>
                    Assert.True(window.TryHandleBrowserFocusShortcut(Key.Left, ModifierKeys.Alt)));
                Assert.True(browser.CanGoForward, "Alt+Left should leave Forward history.");

                await NavigateAsync(() =>
                    Assert.True(window.TryHandleBrowserFocusShortcut(Key.Right, ModifierKeys.Alt)));
                Assert.True(browser.CanGoBack, "Alt+Right should restore Back history.");

                await NavigateAsync(() =>
                {
                    var f5 = new KeyEventArgs(
                        Keyboard.PrimaryDevice,
                        PresentationSource.FromVisual(window)!,
                        0,
                        Key.F5)
                    {
                        RoutedEvent = Keyboard.PreviewKeyDownEvent
                    };
                    window.RaiseEvent(f5);
                    Assert.True(f5.Handled, "F5 should be handled by the window shortcut path.");
                });
                await NavigateAsync(() =>
                    Assert.True(window.TryHandleBrowserFocusShortcut(Key.R, ModifierKeys.Control)));

                shell.BraveBrowserViewModel.IsFocusMode = false;
                Assert.False(
                    window.TryHandleBrowserFocusShortcut(Key.Left, ModifierKeys.Alt),
                    "Shortcuts should be ignored when no browser is focused.");
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
