using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class DiscordBrowserTests
{
    [Fact]
    public void StartsOnLoginPageAndUnlocksAfterDiscordLoads()
    {
        var viewModel = new DiscordBrowserViewModel();
        var automation = new FakeAutomation();

        Assert.True(viewModel.IsBrowserOpen);
        Assert.True(viewModel.IsLoginPage);
        Assert.Equal(DiscordBrowserViewModel.LoginUrl, viewModel.BrowserUrl);

        viewModel.Automation = automation;
        viewModel.SearchQuery = "vrchat";
        Assert.False(viewModel.SearchCommand.CanExecute(null));

        viewModel.SetBrowserLocation(DiscordBrowserViewModel.FriendsUrl, "Discord");

        Assert.False(viewModel.IsLoginPage);
        Assert.True(viewModel.SearchCommand.CanExecute(null));
        Assert.True(viewModel.FriendsCommand.CanExecute(null));
        Assert.Contains("ready", viewModel.ActionStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SearchOpensQuickSwitcherAndLeavesTheResultsVisible()
    {
        var (viewModel, automation) = Ready();
        viewModel.SearchQuery = "vrchat friends";

        viewModel.SearchCommand.Execute(null);

        Settle.Until(() => automation.Text.Count == 1);
        Assert.Contains(automation.Keys, key => key.Key == "k" && key.Control);
        Assert.Equal("vrchat friends", automation.Text[0]);
        Assert.DoesNotContain(automation.Keys, key => key.Key == "Enter");
    }

    [Fact]
    public void SearchDoesNothingUntilSignInFinishes()
    {
        var viewModel = new DiscordBrowserViewModel
        {
            Automation = new FakeAutomation(),
            SearchQuery = "vrchat"
        };

        Assert.False(viewModel.SearchCommand.CanExecute(null));
        viewModel.SearchCommand.Execute(null);

        Assert.Empty(((FakeAutomation)viewModel.Automation!).Keys);
        Assert.Contains("Sign in", viewModel.ActionStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FriendsButtonUsesDiscordHomeRoute()
    {
        var (viewModel, _) = Ready();
        string? navigated = null;
        viewModel.BrowserNavigationRequested += url => navigated = url;

        viewModel.FriendsCommand.Execute(null);

        Settle.Until(() => navigated is not null);
        Assert.Equal(DiscordBrowserViewModel.FriendsUrl, navigated);
    }

    [Fact]
    public void AddFriendOpensFriendsThenClicksDiscordAddFriend()
    {
        var (viewModel, automation) = Ready();
        automation.ScriptResult = "clicked";
        string? navigated = null;
        viewModel.BrowserNavigationRequested += url => navigated = url;

        viewModel.AddFriendCommand.Execute(null);

        Settle.Until(() => automation.Scripts.Count == 1);
        Assert.Equal(DiscordBrowserViewModel.FriendsUrl, navigated);
        Assert.Contains("add friend", automation.Scripts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Add Friend opened", viewModel.ActionStatus);
    }

    [Fact]
    public void SettingsUsesDiscordShortcutAndFillsSettingsSearch()
    {
        var (viewModel, automation) = Ready();
        viewModel.SearchQuery = "theme";
        automation.ScriptResult = "filled";

        viewModel.SettingsCommand.Execute(null);

        Settle.Until(() => automation.Scripts.Count == 1);
        Assert.Contains(automation.Keys, key => key.Key == "," && key.Control);
        Assert.Contains("theme", automation.Scripts[0]);
        Assert.Contains("search filled in", viewModel.ActionStatus);
    }

    [Fact]
    public void LocationChangesKeepTheBrowserTitleAndLoginGateInSync()
    {
        var viewModel = new DiscordBrowserViewModel();

        viewModel.SetBrowserLocation(DiscordBrowserViewModel.FriendsUrl, "Discord | Friends");

        Assert.Equal(DiscordBrowserViewModel.FriendsUrl, viewModel.BrowserUrl);
        Assert.Equal("Discord | Friends", viewModel.BrowserTitle);
        Assert.False(viewModel.IsLoginPage);

        viewModel.SetBrowserLocation(DiscordBrowserViewModel.LoginUrl, "Discord | Login");

        Assert.True(viewModel.IsLoginPage);
        Assert.Equal("Discord | Login", viewModel.BrowserTitle);
    }

    private static (DiscordBrowserViewModel ViewModel, FakeAutomation Automation) Ready()
    {
        var viewModel = new DiscordBrowserViewModel();
        var automation = new FakeAutomation();
        viewModel.Automation = automation;
        viewModel.SetBrowserLocation(DiscordBrowserViewModel.FriendsUrl, "Discord");
        return (viewModel, automation);
    }

    private sealed class FakeAutomation : IEmbeddedBrowserAutomation
    {
        public List<KeyPress> Keys { get; } = new();

        public List<string> Text { get; } = new();

        public List<string> Scripts { get; } = new();

        public string ScriptResult { get; set; } = "not-found";

        public Task SendKeyAsync(
            string key,
            string code,
            int virtualKeyCode,
            bool control = false,
            bool shift = false)
        {
            Keys.Add(new KeyPress(key, code, virtualKeyCode, control, shift));
            return Task.CompletedTask;
        }

        public Task InsertTextAsync(string text)
        {
            Text.Add(text);
            return Task.CompletedTask;
        }

        public Task<string> ExecuteScriptAsync(string script)
        {
            Scripts.Add(script);
            return Task.FromResult(ScriptResult);
        }

    }

    private sealed record KeyPress(
        string Key,
        string Code,
        int VirtualKeyCode,
        bool Control,
        bool Shift);
}
