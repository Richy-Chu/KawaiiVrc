using System;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Reads and writes Brave Shields policy. The app shares one policy per host
/// across all three embedded browsers, while each new host inherits the saved
/// defaults. Settings are persisted with the app's normal settings file.
/// </summary>
public sealed class BraveShieldsService
{
    public static BraveShieldsService Shared { get; } = new(SettingsStore.Shared);

    private readonly SettingsStore _store;

    public BraveShieldsService(SettingsStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Raised after a host's policy changes. The argument is the normalized host.</summary>
    public event EventHandler<string>? Changed;

    public BraveShieldSiteSettings GetSettings(string? host)
    {
        var preferences = GetPreferences();
        var key = NormalizeHost(host);
        return key.Length > 0 && preferences.Sites.TryGetValue(key, out var settings)
            ? settings.Clone()
            : preferences.Defaults.Clone();
    }

    public BraveShieldSiteSettings GetDefaults() => GetPreferences().Defaults.Clone();

    public void SaveSettings(string? host, BraveShieldSiteSettings settings)
    {
        if (settings is null)
        {
            return;
        }

        var preferences = GetPreferences();
        var key = NormalizeHost(host);
        if (key.Length == 0)
        {
            preferences.Defaults.CopyFrom(settings);
        }
        else
        {
            preferences.Sites[key] = settings.Clone();
        }

        _store.Save();
        Changed?.Invoke(this, key);
    }

    public void ResetSite(string? host)
    {
        var preferences = GetPreferences();
        var key = NormalizeHost(host);
        if (key.Length == 0 || !preferences.Sites.Remove(key))
        {
            return;
        }

        _store.Save();
        Changed?.Invoke(this, key);
    }

    public static string NormalizeHost(string? host)
        => string.IsNullOrWhiteSpace(host) ? string.Empty : host.Trim().ToLowerInvariant();

    private static void ApplyLegacyModes(BraveShieldSiteSettings settings)
    {
        settings.AdBlockMode = settings.BlockAdsAndTrackers
            ? BraveAdBlockMode.Standard
            : BraveAdBlockMode.Allow;
        settings.HttpsUpgradeMode = settings.UpgradeToHttps
            ? BraveHttpsUpgradeMode.Standard
            : BraveHttpsUpgradeMode.Disabled;
        settings.FingerprintMode = settings.BlockFingerprinting
            ? BraveFingerprintMode.Standard
            : BraveFingerprintMode.Allow;
        settings.CookieMode = settings.BlockThirdPartyCookies
            ? BraveCookieMode.BlockThirdParty
            : BraveCookieMode.AllowAll;
    }

    private BraveShieldsPreferences GetPreferences()
    {
        var settings = _store.Settings;
        settings.BraveShields ??= new BraveShieldsPreferences();
        var preferences = settings.BraveShields;
        preferences.Defaults ??= new BraveShieldSiteSettings();
        preferences.Sites ??= new System.Collections.Generic.Dictionary<string, BraveShieldSiteSettings>(
            StringComparer.OrdinalIgnoreCase);

        if (!preferences.Initialized)
        {
            // Preserve the old single "Block ads" preference for existing users.
            preferences.Defaults.BlockAdsAndTrackers = settings.BrowserAdBlockEnabled;
            preferences.Initialized = true;
            _store.Save();
        }

        if (!preferences.ModesInitialized)
        {
            // Upgrading from the boolean Shields panel: map each old switch to
            // the corresponding Brave mode before the UI exposes the dropdowns.
            ApplyLegacyModes(preferences.Defaults);
            foreach (var site in preferences.Sites.Values)
            {
                ApplyLegacyModes(site);
            }

            preferences.ModesInitialized = true;
            _store.Save();
        }

        return preferences;
    }
}
