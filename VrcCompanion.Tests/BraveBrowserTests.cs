using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class BraveBrowserTests
{
    [Theory]
    [InlineData("spotify", "https://search.brave.com/search?q=spotify")]
    [InlineData("cat ears & tails", "https://search.brave.com/search?q=cat%20ears%20%26%20tails")]
    [InlineData("https://open.spotify.com/", "https://open.spotify.com/")]
    [InlineData("http://example.com/watch?v=1", "http://example.com/watch?v=1")]
    [InlineData("youtube.com", "https://youtube.com")]
    public void BuildDestination_RoutesSearchTermsAndAddresses(string query, string expected)
    {
        Assert.Equal(expected, BraveBrowserViewModel.BuildDestination(query));
    }

    [Fact]
    public void Browser_StartsClosedUntilNavigationIsRequested()
    {
        var viewModel = new BraveBrowserViewModel();

        Assert.False(viewModel.IsBrowserOpen);
        Assert.Equal(BraveBrowserViewModel.HomeUrl, viewModel.BrowserUrl);
    }

    [Fact]
    public void FocusMode_WaitsForAnOpenBrowser()
    {
        var viewModel = new BraveBrowserViewModel();

        viewModel.IsFocusMode = true;

        Assert.False(viewModel.IsFocusMode);

        viewModel.ShowBrowserForLayoutTest();
        viewModel.IsFocusMode = true;

        Assert.True(viewModel.IsFocusMode);
    }

    [Fact]
    public void SearchCommand_PublishesBraveSearchNavigation()
    {
        var viewModel = new BraveBrowserViewModel();
        string? navigated = null;
        viewModel.BrowserNavigationRequested += url => navigated = url;

        viewModel.SearchQuery = "spotify";
        Assert.True(viewModel.SearchCommand.CanExecute(null));
        viewModel.SearchCommand.Execute(null);

        Assert.Equal("https://search.brave.com/search?q=spotify", navigated);
        Assert.Equal(navigated, viewModel.BrowserUrl);
        Assert.Equal("Brave Browser", viewModel.BrowserTitle);
        Assert.True(viewModel.IsBrowserOpen);
    }

    [Fact]
    public void BlankSearch_DoesNotNavigate()
    {
        var viewModel = new BraveBrowserViewModel();
        string? navigated = null;
        viewModel.BrowserNavigationRequested += url => navigated = url;

        viewModel.SearchQuery = "   ";
        Assert.False(viewModel.SearchCommand.CanExecute(null));
        viewModel.SearchCommand.Execute(null);

        Assert.Null(navigated);
    }

    [Fact]
    public void HomeCommand_ReturnsToBraveSearch()
    {
        var viewModel = new BraveBrowserViewModel();
        string? navigated = null;
        viewModel.BrowserNavigationRequested += url => navigated = url;

        viewModel.SetBrowserLocation("https://open.spotify.com/", "Spotify");
        viewModel.HomeCommand.Execute(null);

        Assert.Equal(BraveBrowserViewModel.HomeUrl, navigated);
        Assert.Equal("Brave Search", viewModel.BrowserTitle);
    }

    [Fact]
    public void SetBrowserLocation_TracksTheCurrentPage()
    {
        var viewModel = new BraveBrowserViewModel();

        viewModel.SetBrowserLocation("https://open.spotify.com/", "Spotify");

        Assert.Equal("https://open.spotify.com/", viewModel.BrowserUrl);
        Assert.Equal("Spotify", viewModel.BrowserTitle);
    }
}
