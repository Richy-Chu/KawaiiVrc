using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class AvatarSortTests
{
    [Fact]
    public void UploadedAvatarSortModesUseVrchatMetadata()
    {
        var settings = SettingsStore.Shared.Settings;
        var previousUploadedSort = settings.UploadedAvatarSort;
        var previousFavoriteSort = settings.FavoriteAvatarSort;
        var previousRecent = settings.RecentAvatarIds.ToList();
        try
        {
            using var api = new VrcApiClient();
            var vm = new AvatarViewModel(api);
            var older = Avatar("avtr_older", "Zeta", "2020-01-01", "2024-06-01", "Excellent");
            var newer = Avatar("avtr_newer", "Alpha", "2023-01-01", "2023-06-01", "Very Poor");
            var middle = Avatar("avtr_middle", "Beta", "2024-01-01", "2022-06-01", "Good");

            vm.SeedLibraryForTests(new[] { older, newer, middle });

            SelectSort(vm, "date-added");
            Assert.Equal(new[] { "avtr_middle", "avtr_newer", "avtr_older" }, Ids(vm));

            SelectSort(vm, "last-updated");
            Assert.Equal(new[] { "avtr_older", "avtr_newer", "avtr_middle" }, Ids(vm));

            SelectSort(vm, "name");
            Assert.Equal(new[] { "avtr_newer", "avtr_middle", "avtr_older" }, Ids(vm));

            settings.RecentAvatarIds = new List<string> { "avtr_older", "avtr_middle" };
            SelectSort(vm, "recently-used");
            Assert.Equal(new[] { "avtr_older", "avtr_middle", "avtr_newer" }, Ids(vm));

            SelectSort(vm, "performance");
            Assert.Equal(new[] { "avtr_older", "avtr_middle", "avtr_newer" }, Ids(vm));

            vm.SeedFavoritesForTests(new[] { older, newer, middle });
            vm.SelectedFavoriteSort = vm.AvatarSortOptions.Single(option => option.Key == "name");
            Assert.Equal(
                new[] { "Alpha", "Beta", "Zeta" },
                vm.FavoriteAvatars.Select(avatar => avatar.DisplayName).ToArray());

            var reopened = new AvatarViewModel(api);
            Assert.Equal("performance", reopened.SelectedAvatarSort.Key);
            Assert.Equal("name", reopened.SelectedFavoriteSort.Key);
        }
        finally
        {
            settings.UploadedAvatarSort = previousUploadedSort;
            settings.FavoriteAvatarSort = previousFavoriteSort;
            settings.RecentAvatarIds = previousRecent;
            SettingsStore.Shared.Save();
        }
    }

    [Fact]
    public void SortMenusRenderTheFiveGameOptions()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            shell.SelectedSection = shell.Sections.Single(section => section.Key == "avatars");
            var vm = new AvatarViewModel(new VrcApiClient());
            TestHooks.AttachAvatarViewModel(shell, vm);
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

                var uploadSort = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.ToolTip, "Sort uploaded avatars"));
                Assert.True(uploadSort.IsVisible);
                uploadSort.Command.Execute(uploadSort.CommandParameter);
                Pump();
                window.UpdateLayout();
                Assert.True(vm.IsAvatarSortOpen);
                AssertSortMenu(window, "Date Added");
                vm.IsAvatarSortOpen = false;

                shell.SelectedSection = shell.Sections.Single(section => section.Key == "favorites");
                window.UpdateLayout();
                var favoriteSort = FindVisualChildren<Button>(window)
                    .Single(button => Equals(button.ToolTip, "Sort favourite avatars"));
                Assert.True(favoriteSort.IsVisible);
                favoriteSort.Command.Execute(favoriteSort.CommandParameter);
                Pump();
                window.UpdateLayout();
                Assert.True(vm.IsFavoriteSortOpen);
                AssertSortMenu(window, "Date Added");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void UploadedAvatarCardShowsVisibilityAndPlatformChips()
    {
        WpfTestHost.Run(() =>
        {
            using var shell = new MainViewModel();
            TestHooks.SetLoggedIn(shell);
            shell.SelectedSection = shell.Sections.Single(section => section.Key == "avatars");
            var vm = new AvatarViewModel(new VrcApiClient());
            TestHooks.AttachAvatarViewModel(shell, vm);
            var avatar = new Avatar
            {
                Id = "avtr_card_private_pc",
                Name = "Private PC Avatar",
                ReleaseStatus = "private",
                Tags = { "content_sex", "content_adult" },
                UnityPackages =
                {
                    new AvatarUnityPackage { Platform = "standalonewindows" }
                }
            };
            vm.SeedLibraryForTests(new[] { avatar });

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

                var cardText = FindVisualChildren<TextBlock>(window)
                    .Where(block => ReferenceEquals(block.DataContext, avatar))
                    .Select(block => block.Text)
                    .ToList();
                Assert.Contains("PRIVATE", cardText);
                Assert.Contains("PC", cardText);
                var chipText = FindVisualChildren<TextBlock>(window)
                    .Select(block => block.Text)
                    .ToList();
                Assert.Contains("Sexual content", chipText);
                Assert.Contains("Adult content", chipText);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static void Pump()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void AssertSortMenu(Window window, string selectedLabel)
    {
        var popup = FindVisualChildren<Popup>(window).Single(candidate => candidate.IsOpen);
        var child = Assert.IsType<Border>(popup.Child);
        var list = FindVisualChildren<ListBox>(child).Single();
        Assert.Equal(
            new[] { "Date Added", "Last Updated", "Name", "Recently Used", "Performance" },
            list.Items.Cast<FilterOption>().Select(option => option.Label).ToArray());
        Assert.Equal(selectedLabel, Assert.IsType<FilterOption>(list.SelectedItem).Label);
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

    private static void SelectSort(AvatarViewModel vm, string key)
    {
        vm.SelectedAvatarSort = vm.AvatarSortOptions.Single(option => option.Key == key);
    }

    private static string[] Ids(AvatarViewModel vm)
    {
        return vm.Avatars.Select(avatar => avatar.Id).ToArray();
    }

    private static Avatar Avatar(
        string id,
        string name,
        string createdAt,
        string updatedAt,
        string performance)
    {
        return new Avatar
        {
            Id = id,
            Name = name,
            CreatedAt = DateTimeOffset.Parse(createdAt),
            UpdatedAt = DateTimeOffset.Parse(updatedAt),
            UnityPackages = new List<AvatarUnityPackage>
            {
                new()
                {
                    Platform = "standalonewindows",
                    PerformanceRating = performance
                }
            }
        };
    }
}
