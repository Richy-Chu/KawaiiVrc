using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Builds the shipped window, opens the real profile popup and reads the
/// ON VRCHAT SINCE card out of the live visual tree. A binding that points at a
/// missing property, or a view model that forgets to raise PropertyChanged,
/// fails here instead of in the installed app.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public class MemberSinceCardWindowTests
{
    private const string UserId = "usr_member_since_window";
    private const string CardLabel = "ON VRCHAT SINCE";

    [Fact]
    public void TheOpenProfilePopupShowsTheJoinMonthCard()
    {
        WpfTestHost.Run(() =>
        {
            var joined = new DateTimeOffset(2023, 3, 14, 9, 30, 0, TimeSpan.Zero);

            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            using var api = new VrcApiClient();
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Member Since",
                    Username = "member",
                    IsFriend = true,
                    DateJoined = joined.ToString("O", CultureInfo.InvariantCulture)
                }),
                api,
                ImageLoader.Shared);
            TestHooks.AttachFriendDetail(shell, profile);
            var window = CreateWindow(shell);

            try
            {
                window.Show();
                Pump(window);

                var card = MemberSinceCard(window);
                Assert.Equal(Visibility.Visible, card.Visibility);
                Assert.True(card.IsVisible, "The card should be visible inside the open profile popup.");
                Assert.Equal(ExpectedMonth(joined), CardValue(card).Text);

                var tooltip = Assert.IsType<string>(card.ToolTip);
                Assert.Contains("2023", tooltip);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheOpenProfilePopupHidesTheCardWithoutAJoinDate()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            using var api = new VrcApiClient();
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "No Date",
                    Username = "nodate",
                    IsFriend = true
                }),
                api,
                ImageLoader.Shared);
            TestHooks.AttachFriendDetail(shell, profile);
            var window = CreateWindow(shell);

            try
            {
                window.Show();
                Pump(window);

                var card = MemberSinceCard(window);
                Assert.Equal(Visibility.Collapsed, card.Visibility);
                Assert.False(card.IsVisible, "No join date should leave the card hidden.");
                Assert.DoesNotContain(
                    FindVisualChildren<TextBlock>(card),
                    text => text.Text.StartsWith("Member since", StringComparison.Ordinal));
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void TheCardAppearsWhenAStrangersLiveRecordArrives()
    {
        WpfTestHost.Run(() =>
        {
            var joined = new DateTimeOffset(2019, 8, 14, 4, 5, 0, TimeSpan.Zero);

            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            using var api = new VrcApiClient();

            // A stranger's card starts from a stub user whose /users/{id} record
            // has not arrived yet, exactly as OpenUserProfile builds it.
            var profile = new FriendDetailViewModel(
                new FriendItem(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Not A Friend",
                    Username = "stranger"
                }),
                api,
                ImageLoader.Shared);
            TestHooks.AttachFriendDetail(shell, profile);
            var window = CreateWindow(shell);

            try
            {
                window.Show();
                Pump(window);
                Assert.Equal(Visibility.Collapsed, MemberSinceCard(window).Visibility);

                profile.ApplyUser(new VrcUser
                {
                    Id = UserId,
                    DisplayName = "Not A Friend",
                    DateJoined = joined.ToString("O", CultureInfo.InvariantCulture),
                    Status = "offline",
                    Location = "offline"
                });
                Pump(window);

                var card = MemberSinceCard(window);
                Assert.Equal(Visibility.Visible, card.Visibility);
                Assert.True(card.IsVisible, "The live record should reveal the card.");
                Assert.Equal(ExpectedMonth(joined), CardValue(card).Text);
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>Same format the card uses, so the assertion does not depend on the test machine's culture.</summary>
    private static string ExpectedMonth(DateTimeOffset joined)
        => "Member since " + joined.ToLocalTime().ToString("MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>
    /// The popup's card, found through its label inside the profile overlay so the
    /// same-labelled dashboard card can never stand in for a broken popup binding.
    /// </summary>
    private static Border MemberSinceCard(Window window)
    {
        var overlay = FindVisualChildren<Grid>(window)
            .Single(grid => AutomationProperties.GetAutomationId(grid) == "FriendProfileOverlay");
        Assert.True(overlay.IsVisible, "The profile overlay should be open.");

        var label = FindVisualChildren<TextBlock>(overlay)
            .Single(text => text.Text == CardLabel);

        // label -> text column -> icon/text row -> the card Border.
        for (var ancestor = VisualTreeHelper.GetParent(label);
             ancestor is not null;
             ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is Border border)
            {
                return border;
            }
        }

        throw new InvalidOperationException("The ON VRCHAT SINCE label has no card Border around it.");
    }

    private static TextBlock CardValue(Border card)
        => FindVisualChildren<TextBlock>(card)
            .Single(text => text.Text.StartsWith("Member since", StringComparison.Ordinal));

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
