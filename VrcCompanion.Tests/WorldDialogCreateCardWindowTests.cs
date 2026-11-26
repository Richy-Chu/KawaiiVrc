using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The create card hides the age-gate checkbox, shows its explanation and swaps the access
/// list by binding alone. A renamed property or a mistyped path parses without complaint and
/// simply draws the wrong card, so these cases read the realised window instead.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class WorldDialogCreateCardWindowTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd";

    [Fact]
    public void TheCreateCardBindsItsAgeGateAndPersonalInstanceControls()
    {
        WpfTestHost.Run(() =>
        {
            using var handler = new CreateCardHandler();
            using var api = new VrcApiClient(null, handler);
            TestHooks.SetCurrentUser(api, new VrcUser { Id = "usr_me", DisplayName = "Me" });
            using var shell = new MainViewModel(api);
            TestHooks.SetLoggedIn(shell);

            var window = CreateWindow(shell);
            window.Show();
            try
            {
                shell.OpenWorldCommand.Execute(new WorldItem(new VrcWorld
                {
                    Id = WorldId,
                    Name = "Popcorn Palace"
                }));

                Settle.Until(
                    () => shell.WorldDialog is { } open &&
                          !open.IsLoadingOwners &&
                          open.Owners.Count(owner => owner.IsGroup) > 1,
                    Settle.PumpBackground,
                    "the world dialog never listed its groups");
                var dialog = shell.WorldDialog!;
                Pump(window);

                var card = window.FindName("WorldDialogOverlay") as DependencyObject;
                Assert.True(card is not null, "The world dialog overlay was not in the window.");

                var checkbox = FindVisualChildren<CheckBox>(card!)
                    .SingleOrDefault(box => Equals(box.Content, "Age gated (18+)"));
                var explanation = FindVisualChildren<TextBlock>(card!)
                    .SingleOrDefault(text =>
                        text.Text.Contains("can't create age-gated instances", StringComparison.Ordinal));
                var back = FindVisualChildren<Button>(card!)
                    .SingleOrDefault(button =>
                        ReferenceEquals(button.Command, dialog.UsePersonalInstanceCommand));
                Assert.True(checkbox is not null, "The create card never drew the age-gate checkbox.");
                Assert.True(explanation is not null, "The create card never drew the age-gate explanation.");
                Assert.True(back is not null, "The create card never drew the way back to a personal instance.");

                // The account always keeps the option.
                Assert.True(checkbox!.IsVisible, "A personal instance should offer the age-gate checkbox.");
                Assert.False(explanation!.IsVisible, "A personal instance should not show the explanation.");
                Assert.False(back!.IsVisible, "A personal instance needs no way back.");

                // A group whose role carries the permission keeps the checkbox.
                dialog.SelectedOwner = dialog.Owners.Single(owner => owner.Label == "Test Group");
                Pump(window);
                Assert.True(checkbox.IsVisible, "A permitted group should offer the age-gate checkbox.");
                Assert.False(explanation.IsVisible, "A permitted group should not show the explanation.");
                Assert.True(back.IsVisible, "A group host should offer the way back.");

                // A group whose role lacks it hides the checkbox and says why.
                dialog.SelectedOwner = dialog.Owners.Single(owner => owner.Label == "No Age Gate Group");
                Pump(window);
                Assert.False(checkbox.IsVisible, "An unpermitted group must not show the age-gate checkbox.");
                Assert.True(explanation.IsVisible, "An unpermitted group should explain why the option is off.");
                Assert.Contains("permission", explanation.Text, StringComparison.OrdinalIgnoreCase);
                Assert.True(back.IsVisible);

                // The button the card drew is the one that goes back.
                back.Command.Execute(back.CommandParameter);
                Pump(window);

                Assert.Same(dialog.Owners[0], dialog.SelectedOwner);
                Assert.False(dialog.IsGroupOwner);
                Assert.True(checkbox.IsVisible, "The account should offer the age-gate checkbox again.");
                Assert.False(explanation.IsVisible, "Going back should hide the explanation.");
                Assert.False(back.IsVisible, "There is nothing left to go back from.");

                var access = FindVisualChildren<ListBox>(card!)
                    .SingleOrDefault(list => ReferenceEquals(list.ItemsSource, dialog.InstanceTypes));
                Assert.True(access is not null, "The create card never drew the access list.");
                Assert.Contains(
                    access!.Items.Cast<InstanceTypeOption>(),
                    type => type.Label == "Public");
                Assert.Equal("Create instance", dialog.CreateButtonText);
            }
            finally
            {
                shell.CloseWorldDialogCommand.Execute(null);
                window.Close();
            }
        });
    }

    private static MainWindow CreateWindow(MainViewModel shell)
        => new(shell, initializeOnLoad: false)
        {
            Width = 1400,
            Height = 900,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 100,
            Top = 100
        };

    private static void Pump(Window window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
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

    private sealed class CreateCardHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;

            if (path.Contains("/users/", StringComparison.Ordinal) &&
                path.EndsWith("/groups", StringComparison.Ordinal))
            {
                return await Json("""
                    [
                      {
                        "id": "grp_allowed",
                        "name": "Test Group",
                        "membershipStatus": "member",
                        "myMember": {
                          "permissions": ["group-instance-public-create", "group-instance-age-gated-create"]
                        }
                      },
                      {
                        "id": "grp_restricted",
                        "name": "No Age Gate Group",
                        "membershipStatus": "member",
                        "myMember": { "permissions": ["group-instance-public-create"] }
                      }
                    ]
                    """);
            }

            if (path.EndsWith($"/worlds/{WorldId}", StringComparison.Ordinal))
            {
                return await Json($$"""
                    {
                      "id": "{{WorldId}}",
                      "name": "Popcorn Palace",
                      "authorName": "spacebaar",
                      "slimInstances": []
                    }
                    """);
            }

            return await Json("[]");
        }

        private static async Task<HttpResponseMessage> Json(
            string json,
            HttpStatusCode status = HttpStatusCode.OK)
        {
            await Task.Yield();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }
    }
}
