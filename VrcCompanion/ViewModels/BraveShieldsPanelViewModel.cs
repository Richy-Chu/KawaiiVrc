using System;
using System.Globalization;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// State shown by one browser's Brave Shields popup. The popup edits the
/// persisted policy for the current host and reports changes so every browser
/// showing that host stays in sync.
/// </summary>
public sealed class BraveShieldsPanelViewModel : ViewModelBase
{
    private readonly BraveShieldsService _service;
    private BraveShieldSiteSettings _settings;
    private string _siteHost = string.Empty;
    private int _blockedCount;
    private bool _isAdvancedOpen = true;
    private bool _showFilterLists;
    private bool _showGlobalSettings;

    public BraveShieldsPanelViewModel(BraveShieldsService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _settings = _service.GetSettings(string.Empty);
    }

    public event EventHandler? SettingsChanged;

    public string SiteHost => _siteHost;

    public string SiteLabel => _siteHost.Length == 0
        ? "No site open"
        : _siteHost.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? _siteHost[4..]
            : _siteHost;

    public bool HasSite => _siteHost.Length > 0;

    public string StatusText => !HasSite
        ? "Open a site to use Shields"
        : ShieldsEnabled
            ? "Shields are UP for this site"
            : "Shields are DOWN for this site";

    public int BlockedCount => _blockedCount;

    public string BlockedSummary => !HasSite
        ? "No site data yet"
        : $"{_blockedCount.ToString("N0", CultureInfo.CurrentCulture)} trackers, ads and more blocked";

    public bool ShieldsEnabled
    {
        get => _settings.ShieldsEnabled;
        set => UpdateSetting(
            _settings.ShieldsEnabled,
            value,
            () => _settings.ShieldsEnabled = value,
            nameof(ShieldsEnabled),
            nameof(StatusText));
    }

    public BraveAdBlockMode AdBlockMode
    {
        get => _settings.AdBlockMode;
        set => UpdateMode(
            _settings.AdBlockMode,
            value,
            () =>
            {
                _settings.AdBlockMode = value;
                _settings.BlockAdsAndTrackers = value != BraveAdBlockMode.Allow;
            },
            nameof(AdBlockMode),
            nameof(BlockAdsAndTrackers));
    }

    public bool BlockAdsAndTrackers
    {
        get => AdBlockMode != BraveAdBlockMode.Allow;
        set => AdBlockMode = value
            ? BraveAdBlockMode.Standard
            : BraveAdBlockMode.Allow;
    }

    public BraveHttpsUpgradeMode HttpsUpgradeMode
    {
        get => _settings.HttpsUpgradeMode;
        set => UpdateMode(
            _settings.HttpsUpgradeMode,
            value,
            () =>
            {
                _settings.HttpsUpgradeMode = value;
                _settings.UpgradeToHttps = value != BraveHttpsUpgradeMode.Disabled;
            },
            nameof(HttpsUpgradeMode),
            nameof(UpgradeToHttps));
    }

    public bool UpgradeToHttps
    {
        get => HttpsUpgradeMode != BraveHttpsUpgradeMode.Disabled;
        set => HttpsUpgradeMode = value
            ? BraveHttpsUpgradeMode.Standard
            : BraveHttpsUpgradeMode.Disabled;
    }

    public bool BlockScripts
    {
        get => _settings.BlockScripts;
        set => UpdateSetting(
            _settings.BlockScripts,
            value,
            () => _settings.BlockScripts = value,
            nameof(BlockScripts));
    }

    public BraveFingerprintMode FingerprintMode
    {
        get => _settings.FingerprintMode;
        set => UpdateMode(
            _settings.FingerprintMode,
            value,
            () =>
            {
                _settings.FingerprintMode = value;
                _settings.BlockFingerprinting = value != BraveFingerprintMode.Allow;
            },
            nameof(FingerprintMode),
            nameof(BlockFingerprinting));
    }

    public bool BlockFingerprinting
    {
        get => FingerprintMode != BraveFingerprintMode.Allow;
        set => FingerprintMode = value
            ? BraveFingerprintMode.Standard
            : BraveFingerprintMode.Allow;
    }

    public BraveCookieMode CookieMode
    {
        get => _settings.CookieMode;
        set => UpdateMode(
            _settings.CookieMode,
            value,
            () =>
            {
                _settings.CookieMode = value;
                _settings.BlockThirdPartyCookies = value != BraveCookieMode.AllowAll;
            },
            nameof(CookieMode),
            nameof(BlockThirdPartyCookies));
    }

    public bool BlockThirdPartyCookies
    {
        get => CookieMode != BraveCookieMode.AllowAll;
        set => CookieMode = value
            ? BraveCookieMode.BlockThirdParty
            : BraveCookieMode.AllowAll;
    }

    public bool ForgetMeOnClose
    {
        get => _settings.ForgetMeOnClose;
        set => UpdateSetting(
            _settings.ForgetMeOnClose,
            value,
            () => _settings.ForgetMeOnClose = value,
            nameof(ForgetMeOnClose));
    }

    public bool IsAdvancedOpen
    {
        get => _isAdvancedOpen;
        set
        {
            if (SetProperty(ref _isAdvancedOpen, value))
            {
                if (value)
                {
                    ShowFilterLists = false;
                    ShowGlobalSettings = false;
                }
            }
        }
    }

    public bool ShowFilterLists
    {
        get => _showFilterLists;
        set
        {
            if (!SetProperty(ref _showFilterLists, value))
            {
                return;
            }

            if (value)
            {
                ShowGlobalSettings = false;
            }
        }
    }

    public bool ShowGlobalSettings
    {
        get => _showGlobalSettings;
        set
        {
            if (!SetProperty(ref _showGlobalSettings, value))
            {
                return;
            }

            if (value)
            {
                ShowFilterLists = false;
            }
        }
    }

    public void SetHost(string? host)
    {
        var normalized = BraveShieldsService.NormalizeHost(host);
        if (string.Equals(_siteHost, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _siteHost = normalized;
        _settings = _service.GetSettings(normalized);
        _blockedCount = 0;
        OnAllPropertiesChanged();
    }

    public void ReloadSettings()
    {
        _settings = _service.GetSettings(_siteHost);
        OnAllPropertiesChanged();
    }

    public void IncrementBlocked()
    {
        _blockedCount++;
        OnPropertyChanged(nameof(BlockedCount));
        OnPropertyChanged(nameof(BlockedSummary));
    }

    public void ResetSiteToDefaults()
    {
        if (!HasSite)
        {
            return;
        }

        _service.ResetSite(_siteHost);
        ReloadSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool ShouldBlockTrackersAndAds => ShieldsEnabled && AdBlockMode != BraveAdBlockMode.Allow;

    public bool ShouldUpgradeToHttps => ShieldsEnabled && HttpsUpgradeMode != BraveHttpsUpgradeMode.Disabled;

    public bool ShouldBlockScripts => ShieldsEnabled && BlockScripts;

    public bool ShouldBlockFingerprinting => ShieldsEnabled && FingerprintMode != BraveFingerprintMode.Allow;

    public bool ShouldBlockThirdPartyCookies => ShieldsEnabled && CookieMode != BraveCookieMode.AllowAll;

    public bool ShouldBlockAllCookies => ShieldsEnabled && CookieMode == BraveCookieMode.BlockAll;

    public bool ShouldForgetOnLeave => ShieldsEnabled && ForgetMeOnClose;

    private void UpdateSetting(
        bool current,
        bool value,
        Action apply,
        params string[] changedProperties)
    {
        if (current == value)
        {
            return;
        }

        apply();
        foreach (var property in changedProperties)
        {
            OnPropertyChanged(property);
        }

        _service.SaveSettings(_siteHost, _settings);
        OnPropertyChanged(nameof(ShouldBlockTrackersAndAds));
        OnPropertyChanged(nameof(ShouldUpgradeToHttps));
        OnPropertyChanged(nameof(ShouldBlockScripts));
        OnPropertyChanged(nameof(ShouldBlockFingerprinting));
        OnPropertyChanged(nameof(ShouldBlockThirdPartyCookies));
        OnPropertyChanged(nameof(ShouldBlockAllCookies));
        OnPropertyChanged(nameof(ShouldForgetOnLeave));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateMode<T>(
        T current,
        T value,
        Action apply,
        params string[] changedProperties)
        where T : struct, Enum
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return;
        }

        apply();
        foreach (var property in changedProperties)
        {
            OnPropertyChanged(property);
        }

        _service.SaveSettings(_siteHost, _settings);
        OnPropertyChanged(nameof(ShouldBlockTrackersAndAds));
        OnPropertyChanged(nameof(ShouldUpgradeToHttps));
        OnPropertyChanged(nameof(ShouldBlockScripts));
        OnPropertyChanged(nameof(ShouldBlockFingerprinting));
        OnPropertyChanged(nameof(ShouldBlockThirdPartyCookies));
        OnPropertyChanged(nameof(ShouldBlockAllCookies));
        OnPropertyChanged(nameof(ShouldForgetOnLeave));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }
}
