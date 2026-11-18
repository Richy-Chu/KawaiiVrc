using System;
using System.IO;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class BraveShieldsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "VrcCompanionBraveShieldsTests",
        Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void SiteSettingsArePersistedAndDoNotChangeOtherSites()
    {
        var store = new SettingsStore(SettingsPath);
        var service = new BraveShieldsService(store);
        var site = service.GetSettings("booth.pm");
        site.ShieldsEnabled = false;
        site.BlockScripts = true;
        service.SaveSettings("booth.pm", site);

        var reloaded = new BraveShieldsService(new SettingsStore(SettingsPath));
        var saved = reloaded.GetSettings("booth.pm");
        Assert.False(saved.ShieldsEnabled);
        Assert.True(saved.BlockScripts);

        var other = reloaded.GetSettings("github.com");
        Assert.True(other.ShieldsEnabled);
        Assert.False(other.BlockScripts);
    }

    [Fact]
    public void LegacyAdBlockSettingSeedsFirstRunDefaults()
    {
        var store = new SettingsStore(SettingsPath);
        store.Settings.BrowserAdBlockEnabled = false;
        store.Save();

        var service = new BraveShieldsService(new SettingsStore(SettingsPath));
        var defaults = service.GetSettings("example.com");
        Assert.False(defaults.BlockAdsAndTrackers);
        Assert.Equal(
            VrcCompanion.Models.BraveAdBlockMode.Allow,
            defaults.AdBlockMode);
    }

    [Fact]
    public void PanelModesUseBraveValuesAndPersistPerSite()
    {
        var service = new BraveShieldsService(new SettingsStore(SettingsPath));
        var panel = new BraveShieldsPanelViewModel(service);
        panel.SetHost("booth.pm");

        panel.AdBlockMode = VrcCompanion.Models.BraveAdBlockMode.Aggressive;
        panel.HttpsUpgradeMode = VrcCompanion.Models.BraveHttpsUpgradeMode.Strict;
        panel.FingerprintMode = VrcCompanion.Models.BraveFingerprintMode.Strict;
        panel.CookieMode = VrcCompanion.Models.BraveCookieMode.BlockAll;

        var reloaded = new BraveShieldsService(new SettingsStore(SettingsPath));
        var saved = reloaded.GetSettings("booth.pm");
        Assert.Equal(VrcCompanion.Models.BraveAdBlockMode.Aggressive, saved.AdBlockMode);
        Assert.Equal(VrcCompanion.Models.BraveHttpsUpgradeMode.Strict, saved.HttpsUpgradeMode);
        Assert.Equal(VrcCompanion.Models.BraveFingerprintMode.Strict, saved.FingerprintMode);
        Assert.Equal(VrcCompanion.Models.BraveCookieMode.BlockAll, saved.CookieMode);
    }

    [Fact]
    public void PanelResetsBlockedCountWhenTheSiteChanges()
    {
        var service = new BraveShieldsService(new SettingsStore(SettingsPath));
        var panel = new BraveShieldsPanelViewModel(service);
        panel.SetHost("booth.pm");
        panel.IncrementBlocked();
        panel.IncrementBlocked();

        Assert.Equal(2, panel.BlockedCount);
        Assert.Contains("2", panel.BlockedSummary);

        panel.SetHost("github.com");
        Assert.Equal(0, panel.BlockedCount);
        Assert.Contains("0", panel.BlockedSummary);
    }

    [Fact]
    public void PanelSavesShieldsAndResetRemovesSiteOverride()
    {
        var service = new BraveShieldsService(new SettingsStore(SettingsPath));
        var panel = new BraveShieldsPanelViewModel(service);
        panel.SetHost("booth.pm");
        panel.ShieldsEnabled = false;

        Assert.False(service.GetSettings("booth.pm").ShieldsEnabled);

        panel.ResetSiteToDefaults();
        Assert.True(panel.ShieldsEnabled);
        Assert.True(service.GetSettings("booth.pm").ShieldsEnabled);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch
        {
            // Test cleanup must not mask the assertion result.
        }
    }
}
