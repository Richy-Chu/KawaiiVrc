using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Controls;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A popup's backdrop fills the window, so it appears under the pointer that
/// opened the popup. These cases pin the guard that keeps the opening gesture's
/// second press from closing the popup, and pin that every backdrop has it.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class OverlayBackdropGuardTests
{
    [Fact]
    public void AFreshBackdropIgnoresThePressThatOpenedThePopup()
    {
        WpfTestHost.Run(() =>
        {
            var now = 100_000L;
            OverlayBackdropGuard.Clock = () => now;

            var backdrop = new Button();
            OverlayBackdropGuard.SetProtect(backdrop, true);
            OverlayBackdropGuard.MarkOpened(backdrop);

            Assert.True(OverlayBackdropGuard.IsOpeningPress(backdrop, now + 50));

            var press = LeftPress();
            backdrop.RaiseEvent(press);
            Assert.True(
                press.Handled,
                "The press that belongs to the gesture that opened the popup must not reach the backdrop.");
        });
    }

    [Fact]
    public void ADeliberateBackdropClickStillClosesOnceThePopupHasSettled()
    {
        WpfTestHost.Run(() =>
        {
            var now = 200_000L;
            OverlayBackdropGuard.Clock = () => now;

            var backdrop = new Button();
            OverlayBackdropGuard.SetProtect(backdrop, true);
            OverlayBackdropGuard.MarkOpened(backdrop);

            now += OverlayBackdropGuard.GuardMilliseconds + 100;

            var press = LeftPress();
            backdrop.RaiseEvent(press);
            Assert.False(
                press.Handled,
                "A click on a popup that has been open for a while must still close it.");
        });
    }

    [Fact]
    public void EveryPopupBackdropIsProtected()
    {
        var files = new[]
        {
            SourceFile("VrcCompanion", "MainWindow.xaml"),
            SourceFile("VrcCompanion", "Controls", "ContinuityView.xaml")
        };

        var backdrops = files.SelectMany(BackdropElements).ToList();
        Assert.NotEmpty(backdrops);

        var unprotected = backdrops
            .Where(element => !element.Contains(
                "OverlayBackdropGuard.Protect=\"True\"",
                StringComparison.Ordinal))
            .ToList();

        Assert.True(
            unprotected.Count == 0,
            "These popup backdrops would close the popup that just opened: " + string.Join(
                " | ",
                unprotected.Select(element => element.Replace("\r", " ").Replace("\n", " "))));
    }

    [Fact]
    public void TheOpenProfileBackdropSwallowsTheOpeningPress()
    {
        WpfTestHost.Run(() =>
        {
            var now = 300_000L;
            OverlayBackdropGuard.Clock = () => now;

            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            using var api = new VrcApiClient();
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = "usr_backdrop_guard",
                    DisplayName = "Backdrop Guard",
                    Username = "guard",
                    IsFriend = true
                }),
                api,
                ImageLoader.Shared);
            TestHooks.AttachFriendDetail(shell, profile);

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
                Pump(window);

                var overlay = FindVisualChildren<Grid>(window)
                    .Single(grid => AutomationProperties.GetAutomationId(grid) == "FriendProfileOverlay");
                var backdrop = FindVisualChildren<Button>(overlay)
                    .Single(button => button.Content is null && button.Command is not null);

                Assert.True(OverlayBackdropGuard.GetProtect(backdrop));

                var press = LeftPress();
                backdrop.RaiseEvent(press);
                Assert.True(press.Handled, "The click that opened the profile must not close it.");

                now += OverlayBackdropGuard.GuardMilliseconds + 100;
                var settled = LeftPress();
                backdrop.RaiseEvent(settled);
                Assert.False(settled.Handled, "A later click on the backdrop still closes the profile.");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static MouseButtonEventArgs LeftPress()
        => new(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Mouse.PreviewMouseDownEvent
        };

    /// <summary>Each element in the files that carries the backdrop's arrow cursor.</summary>
    private static IEnumerable<string> BackdropElements(string path)
    {
        var text = File.ReadAllText(path);
        const string marker = "Cursor=\"Arrow\"";
        for (var index = text.IndexOf(marker, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal))
        {
            var start = text.LastIndexOf('<', index);
            var end = text.IndexOf('>', index);
            if (start >= 0 && end > start)
            {
                yield return text[start..(end + 1)];
            }
        }
    }

    private static string SourceFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not find {string.Join('/', parts)} above {AppContext.BaseDirectory}.");
    }

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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
