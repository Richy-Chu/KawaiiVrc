using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A row's template is where a style from the wrong scope shows up: a StaticResource
/// that only exists inside the friend profile parses fine until the first real row is
/// measured, and then the whole window fails with XamlParseException. This renders one.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class FriendLocationsRowTests
{
    private const string Location = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~region(us)";

    [Fact]
    public void AFriendLocationsRowRealisesItsDetailsButton()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel(IsolatedApi());
            TestHooks.SetLoggedIn(shell);
            shell.FriendLocationsPage.SetSource(
                new[]
                {
                    new FriendItem(new VrcUser
                    {
                        Id = "usr_friend",
                        DisplayName = "Tree Bear",
                        Location = Location,
                        State = "online",
                        Status = "active"
                    })
                },
                resetPage: true);

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

            window.Show();
            try
            {
                shell.OpenFriendLocationsCommand.Execute(null);
                window.UpdateLayout();

                var details = FindDetailsButton(window);
                Assert.NotNull(details);
                Assert.Equal(Location, details!.CommandParameter);
                Assert.True(
                    details.Command?.CanExecute(details.CommandParameter),
                    "the row's Details button should open that world's instance");
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static Button? FindDetailsButton(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is Button { Content: "Details" } button)
            {
                return button;
            }

            if (FindDetailsButton(child) is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    private static VrcApiClient IsolatedApi()
        => new(new SessionStore(Path.Combine(
            Path.GetTempPath(),
            "vrc-friend-locations-" + Guid.NewGuid().ToString("N"))));
}
