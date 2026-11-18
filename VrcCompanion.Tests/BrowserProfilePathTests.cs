using System.IO;
using VrcCompanion.Controls;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// WebView2's default user data folder sits beside the executable, which is the build output
/// locally and a fresh versioned folder per Velopack update — the reason Discord and the other
/// browser tabs asked for a new sign-in after every build. The profile is pinned to the app's
/// data folder instead, so cookies and logins survive rebuilds and updates.
/// </summary>
public class BrowserProfilePathTests
{
    [Fact]
    public void ProfileLivesInTheAppsDataFolder()
        => Assert.Equal(
            Path.Combine(@"C:\Users\me\AppData\Local", "VrcCompanion", "Browser"),
            EmbeddedBrowser.ResolveUserDataFolder(null, @"C:\Users\me\AppData\Local"));

    [Fact]
    public void OverrideWins()
        => Assert.Equal(
            @"C:\temp\browser-profile",
            EmbeddedBrowser.ResolveUserDataFolder(@"C:\temp\browser-profile", @"C:\Users\me\AppData\Local"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOverrideFallsBackToTheDataFolder(string overridePath)
        => Assert.Equal(
            Path.Combine("local", "VrcCompanion", "Browser"),
            EmbeddedBrowser.ResolveUserDataFolder(overridePath, "local"));
}
