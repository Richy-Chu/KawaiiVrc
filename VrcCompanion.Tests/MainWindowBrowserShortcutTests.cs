using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
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
                    RaiseFocusShortcut(window, Key.Left, ModifierKeys.Alt, asSystemKey: true));
                Assert.True(browser.CanGoForward, "Alt+Left should leave Forward history.");

                await NavigateAsync(() =>
                    RaiseFocusShortcut(window, Key.Right, ModifierKeys.Alt, asSystemKey: true));
                Assert.True(browser.CanGoBack, "Alt+Right should restore Back history.");

                await NavigateAsync(() =>
                {
                    var f5 = CreatePreviewKeyEvent(window, Key.F5);
                    RaisePreviewKey(window, f5, ModifierKeys.None);
                    Assert.True(f5.Handled, "F5 should be handled by the window shortcut path.");
                });
                await NavigateAsync(() =>
                    RaiseFocusShortcut(window, Key.R, ModifierKeys.Control));

                shell.BraveBrowserViewModel.IsFocusMode = false;
                var ignored = CreatePreviewKeyEvent(window, Key.Left);
                RaisePreviewKey(window, ignored, ModifierKeys.Alt);

                Assert.False(ignored.Handled, "Shortcuts should be ignored when no browser is focused.");

                var ignoredAddressShortcut = CreatePreviewKeyEvent(window, Key.L);
                RaisePreviewKey(window, ignoredAddressShortcut, ModifierKeys.Control);
                Assert.False(
                    ignoredAddressShortcut.Handled,
                    "Ctrl+L should be ignored when no browser is focused.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("discord")]
    [InlineData("resources")]
    public void CtrlLFocusesAndSelectsTheFocusAddress(string sectionKey)
    {
        WithFocusWindow(sectionKey, (window, _, _, address) =>
        {
            RaiseFocusShortcut(window, Key.L, ModifierKeys.Control);

            Assert.True(
                address.IsKeyboardFocusWithin,
                $"Ctrl+L should focus the {sectionKey} address box.");
            Assert.Equal(0, address.SelectionStart);
            Assert.Equal(address.Text.Length, address.SelectionLength);
        });
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("discord")]
    [InlineData("resources")]
    public void EscapeCancelsAddressEditBeforeExitingFocus(string sectionKey)
    {
        WithFocusWindow(sectionKey, (window, host, _, address) =>
        {
            address.Focus();
            Keyboard.Focus(address);
            Assert.True(address.IsKeyboardFocusWithin, "The address box should be focused.");
            address.Text = "https://example.com/edited";

            var firstEscape = CreatePreviewKeyEvent(window, Key.Escape);
            RaisePreviewKey(window, firstEscape, ModifierKeys.None);
            Assert.True(firstEscape.Handled, "The first Escape should cancel the address edit.");
            Assert.Equal(FocusUrl, address.Text);
            Assert.True(host.IsFocusMode, "The first Escape should keep focus mode active.");

            var secondEscape = CreatePreviewKeyEvent(window, Key.Escape);
            RaisePreviewKey(window, secondEscape, ModifierKeys.None);
            Assert.True(secondEscape.Handled, "The second Escape should exit focus mode.");
            Assert.False(host.IsFocusMode, "The second Escape should leave focus mode.");
        });
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("discord")]
    [InlineData("resources")]
    public void EscapeCancelingEditedAddressClearsInlineError(string sectionKey)
    {
        WithFocusWindow(sectionKey, (window, host, browser, address) =>
        {
            const string message = "Enter a valid http or https address.";
            var feedback = browser.FindName("FocusAddressError") as TextBlock
                ?? throw new InvalidOperationException("Focus address error was not found.");

            address.Focus();
            Keyboard.Focus(address);
            Assert.True(address.IsKeyboardFocusWithin, "The address box should be focused.");
            address.Text = "https://example.com/edited";
            host.SetBrowserError(message);
            window.UpdateLayout();
            Assert.Equal(message, feedback.Text);
            Assert.True(feedback.IsVisible, "The inline error should be visible before Escape.");

            var escape = CreatePreviewKeyEvent(window, Key.Escape);
            RaisePreviewKey(window, escape, ModifierKeys.None);
            window.UpdateLayout();

            Assert.True(escape.Handled, "Escape should cancel the edited address.");
            Assert.Equal(FocusUrl, address.Text);
            Assert.True(host.IsFocusMode, "Canceling the edit should keep focus mode active.");
            Assert.False(feedback.IsVisible, "Canceling the edit should clear the inline error.");
        });
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("discord")]
    [InlineData("resources")]
    public void FocusModeAnnouncesAllBrowserErrors(string sectionKey)
    {
        WithFocusWindow(sectionKey, (window, host, browser, _) =>
        {
            const string message = "The in-app browser could not start.";
            var feedback = browser.FindName("FocusAddressError") as TextBlock
                ?? throw new InvalidOperationException("Focus address error was not found.");

            host.SetBrowserError(message);
            window.UpdateLayout();

            Assert.Equal(message, feedback.Text);
            Assert.True(
                feedback.IsVisible,
                $"Browser errors should be visible in the {sectionKey} focus toolbar.");
            Assert.Equal(
                AutomationLiveSetting.Assertive,
                AutomationProperties.GetLiveSetting(feedback));
        });
    }

    [Theory]
    [InlineData("brave")]
    [InlineData("discord")]
    [InlineData("resources")]
    public void FocusModeRaisesLiveRegionChangedForBrowserErrors(string sectionKey)
    {
        WithFocusWindowAsync(sectionKey, async (window, host, browser, _) =>
        {
            const string message = "The in-app browser could not start.";
            var feedback = browser.FindName("FocusAddressError") as TextBlock
                ?? throw new InvalidOperationException("Focus address error was not found.");
            var automationId = AutomationProperties.GetAutomationId(feedback);
            var root = AutomationElement.FromHandle(new WindowInteropHelper(window).Handle);
            var announcement = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            AutomationEventHandler handler = (sender, _) =>
            {
                if (sender is AutomationElement element &&
                    element.Current.AutomationId == automationId)
                {
                    announcement.TrySetResult(true);
                }
            };

            Automation.AddAutomationEventHandler(
                AutomationElementIdentifiers.LiveRegionChangedEvent,
                root,
                TreeScope.Subtree,
                handler);
            try
            {
                host.SetBrowserError(message);
                window.UpdateLayout();

                await Task.WhenAny(announcement.Task, Task.Delay(TimeSpan.FromSeconds(5)));
                Assert.True(
                    announcement.Task.IsCompletedSuccessfully,
                    $"Changing the {sectionKey} focus-mode browser error should raise LiveRegionChanged.");
            }
            finally
            {
                Automation.RemoveAutomationEventHandler(
                    AutomationElementIdentifiers.LiveRegionChangedEvent,
                    root,
                    handler);
            }
        });
    }

    private const string FocusUrl = "https://example.com/focus";
    private const string FocusTitle = "Focus page";

    private static void WithFocusWindow(
        string sectionKey,
        Action<MainWindow, IEmbeddedBrowserHost, EmbeddedBrowser, TextBox> test)
    {
        WpfTestHost.Run(() =>
        {
            var (window, host, browser, address) = CreateFocusWindow(sectionKey);
            try
            {
                test(window, host, browser, address);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void WithFocusWindowAsync(
        string sectionKey,
        Func<MainWindow, IEmbeddedBrowserHost, EmbeddedBrowser, TextBox, Task> test)
    {
        WpfTestHost.RunAsync(async () =>
        {
            var (window, host, browser, address) = CreateFocusWindow(sectionKey);
            try
            {
                await test(window, host, browser, address);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static (
        MainWindow Window,
        IEmbeddedBrowserHost Host,
        EmbeddedBrowser Browser,
        TextBox Address) CreateFocusWindow(string sectionKey)
    {
        var shell = new MainViewModel();
        TestHooks.SetLoggedIn(shell);
        var browserSection = shell.Sections.Single(section => section.Key == sectionKey);
        IEmbeddedBrowserHost host = sectionKey switch
        {
            "brave" => shell.BraveBrowserViewModel,
            "discord" => shell.DiscordBrowserViewModel,
            "resources" => shell.CreatorResourcesViewModel,
            _ => throw new ArgumentOutOfRangeException(nameof(sectionKey), sectionKey, null)
        };
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
            window.Activate();
            shell.SelectedSection = browserSection;
            if (host is CreatorResourcesViewModel creatorResources)
            {
                creatorResources.ShowBrowserForLayoutTest();
            }

            host.SetBrowserLocation(FocusUrl, FocusTitle);
            host.IsFocusMode = true;
            window.UpdateLayout();
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

            var browser = FindVisualChildren<EmbeddedBrowser>(window)
                .Single(element => element.IsVisible);
            var address = browser.FindName("FocusAddressBox") as TextBox
                ?? throw new InvalidOperationException("Focus address box was not found.");

            return (window, host, browser, address);
        }
        catch
        {
            window.Close();
            throw;
        }
    }

    private static void RaiseFocusShortcut(
        Window window,
        Key key,
        ModifierKeys modifiers,
        bool asSystemKey = false)
    {
        var keyEvent = CreatePreviewKeyEvent(window, key);
        if (asSystemKey)
        {
            MarkSystemKey(keyEvent);
            Assert.Equal(Key.System, keyEvent.Key);
            Assert.Equal(key, keyEvent.SystemKey);
        }

        RaisePreviewKey(window, keyEvent, modifiers);
        Assert.True(
            keyEvent.Handled,
            $"{modifiers}+{key} should be handled by the window preview-key path.");
    }

    private static KeyEventArgs CreatePreviewKeyEvent(Window window, Key key)
        => new(
            Keyboard.PrimaryDevice,
            PresentationSource.FromVisual(window)!,
            0,
            key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        };

    private static void RaisePreviewKey(
        Window window,
        KeyEventArgs keyEvent,
        ModifierKeys modifiers)
    {
        var previous = new byte[256];
        if (!GetKeyboardState(previous))
        {
            throw new InvalidOperationException(
                $"Could not read keyboard state: {Marshal.GetLastWin32Error()}.");
        }

        try
        {
            var state = new byte[256];
            SetKeyState(state, VkControl, modifiers.HasFlag(ModifierKeys.Control));
            SetKeyState(state, VkLeftControl, modifiers.HasFlag(ModifierKeys.Control));
            SetKeyState(state, VkAlt, modifiers.HasFlag(ModifierKeys.Alt));
            SetKeyState(state, VkLeftAlt, modifiers.HasFlag(ModifierKeys.Alt));

            if (!SetKeyboardState(state))
            {
                throw new InvalidOperationException(
                    $"Could not set keyboard state: {Marshal.GetLastWin32Error()}.");
            }

            AssertModifiersApplied(modifiers, keyEvent.Key);
            window.RaiseEvent(keyEvent);
        }
        finally
        {
            if (!SetKeyboardState(previous))
            {
                throw new InvalidOperationException(
                    $"Could not restore keyboard state: {Marshal.GetLastWin32Error()}.");
            }
        }
    }

    private static void AssertModifiersApplied(ModifierKeys requested, Key key)
    {
        var actual = Keyboard.Modifiers & (ModifierKeys.Alt | ModifierKeys.Control);
        Assert.True(
            actual == requested,
            $"The {requested} modifier state was not applied before {key}; " +
            $"requested={requested}, actual={actual}, full={Keyboard.Modifiers}.");
    }

    private static void MarkSystemKey(KeyEventArgs keyEvent)
    {
        // WPF has no public constructor for the Alt system-key shape. This
        // framework transition turns Key into Key.System and keeps the real key.
        _markSystemMethod ??= typeof(KeyEventArgs).GetMethod(
            "MarkSystem",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(
                "KeyEventArgs.MarkSystem was not found; the system-key test cannot run.");

        _markSystemMethod.Invoke(keyEvent, null);
    }

    private static MethodInfo? _markSystemMethod;

    private const int VkControl = 0x11;
    private const int VkLeftControl = 0xA2;
    private const int VkAlt = 0x12;
    private const int VkLeftAlt = 0xA4;

    private static void SetKeyState(byte[] state, int virtualKey, bool pressed)
        => state[virtualKey] = pressed ? (byte)0x80 : (byte)0;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] keyState);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] keyState);

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
