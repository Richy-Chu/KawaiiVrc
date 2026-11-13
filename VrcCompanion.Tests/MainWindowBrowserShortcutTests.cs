using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
                    RaiseFocusShortcut(window, Key.Left, ModifierKeys.Alt));
                Assert.True(browser.CanGoForward, "Alt+Left should leave Forward history.");

                await NavigateAsync(() =>
                    RaiseFocusShortcut(window, Key.Right, ModifierKeys.Alt));
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
                    RaiseFocusShortcut(window, Key.R, ModifierKeys.Control));

                shell.BraveBrowserViewModel.IsFocusMode = false;
                var ignored = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window)!,
                    0,
                    Key.Left)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                using (KeyboardModifierState.Apply(ModifierKeys.Alt))
                {
                    window.RaiseEvent(ignored);
                }

                Assert.False(ignored.Handled, "Shortcuts should be ignored when no browser is focused.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void RaiseFocusShortcut(Window window, Key key, ModifierKeys modifiers)
    {
        var keyEvent = new KeyEventArgs(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!,
            0,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };

        using (KeyboardModifierState.Apply(modifiers))
        {
            window.RaiseEvent(keyEvent);
        }

        Assert.True(
            keyEvent.Handled,
            $"{modifiers}+{key} should be handled by the window preview-key path.");
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

    /// <summary>
    /// WPF reads modifier state from the thread keyboard state, not from
    /// KeyEventArgs. Set and restore it around each synthetic preview-key event.
    /// </summary>
    private sealed class KeyboardModifierState : IDisposable
    {
        private const int VkShift = 0x10;
        private const int VkControl = 0x11;
        private const int VkAlt = 0x12;
        private const int VkLeftShift = 0xA0;
        private const int VkLeftControl = 0xA2;
        private const int VkLeftAlt = 0xA4;
        private const int VkLeftWindows = 0x5B;

        private readonly byte[] _previous;

        private KeyboardModifierState(byte[] previous)
        {
            _previous = previous;
        }

        public static KeyboardModifierState Apply(ModifierKeys modifiers)
        {
            var previous = new byte[256];
            if (!GetKeyboardState(previous))
            {
                throw new InvalidOperationException(
                    $"Could not read keyboard state: {Marshal.GetLastWin32Error()}.");
            }

            var state = (byte[])previous.Clone();
            SetKeyState(state, VkShift, modifiers.HasFlag(ModifierKeys.Shift));
            SetKeyState(state, VkLeftShift, modifiers.HasFlag(ModifierKeys.Shift));
            SetKeyState(state, VkControl, modifiers.HasFlag(ModifierKeys.Control));
            SetKeyState(state, VkLeftControl, modifiers.HasFlag(ModifierKeys.Control));
            SetKeyState(state, VkAlt, modifiers.HasFlag(ModifierKeys.Alt));
            SetKeyState(state, VkLeftAlt, modifiers.HasFlag(ModifierKeys.Alt));
            SetKeyState(state, VkLeftWindows, modifiers.HasFlag(ModifierKeys.Windows));

            if (!SetKeyboardState(state))
            {
                throw new InvalidOperationException(
                    $"Could not set keyboard state: {Marshal.GetLastWin32Error()}.");
            }

            return new KeyboardModifierState(previous);
        }

        public void Dispose()
        {
            if (!SetKeyboardState(_previous))
            {
                throw new InvalidOperationException(
                    $"Could not restore keyboard state: {Marshal.GetLastWin32Error()}.");
            }
        }

        private static void SetKeyState(byte[] state, int virtualKey, bool pressed)
            => state[virtualKey] = (byte)((state[virtualKey] & 0x7F) | (pressed ? 0x80 : 0));

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetKeyboardState(byte[] keyState);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetKeyboardState(byte[] keyState);
    }
}
