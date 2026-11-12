using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class MainViewModelSectionTests
{
    [Fact]
    public void SwitchingSectionsTogglesBrowseTabVisibility()
    {
        RunOnSta(() =>
        {
            var shell = new MainViewModel();
            var avatar = new AvatarViewModel(new VrcApiClient());
            shell.AvatarViewModel = avatar;

            // The shell starts on Profile by assigning the field, not the setter.
            Assert.False(avatar.IsBrowseTabActive);

            shell.SelectedSection = Section(shell, "avatarsearch");
            Assert.True(avatar.IsBrowseTabActive);

            shell.SelectedSection = Section(shell, "friends");
            Assert.False(avatar.IsBrowseTabActive);

            shell.SelectedSection = Section(shell, "avatarsearch");
            Assert.True(avatar.IsBrowseTabActive);
        });
    }

    private static NavItem Section(MainViewModel shell, string key)
        => shell.Sections.Single(section => section.Key == key);

    /// <summary>The shell builds WPF collection views and a dispatcher timer.</summary>
    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("MainViewModel section test did not finish.");
        }

        failure?.Throw();
    }
}
