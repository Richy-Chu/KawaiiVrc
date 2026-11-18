using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// Brave Shields choices. Defaults apply to new sites; each visited host gets
/// its own copy so one site's privacy choices never change another site.
/// </summary>
public sealed class BraveShieldsPreferences
{
    [JsonPropertyName("initialized")]
    public bool Initialized { get; set; }

    [JsonPropertyName("modesInitialized")]
    public bool ModesInitialized { get; set; }

    [JsonPropertyName("defaults")]
    public BraveShieldSiteSettings Defaults { get; set; } = new();

    [JsonPropertyName("sites")]
    public Dictionary<string, BraveShieldSiteSettings> Sites { get; set; } =
        new(System.StringComparer.OrdinalIgnoreCase);
}

/// <summary>Per-site Brave Shields state, including every advanced control.</summary>
public sealed class BraveShieldSiteSettings
{
    [JsonPropertyName("shieldsEnabled")]
    public bool ShieldsEnabled { get; set; } = true;

    [JsonPropertyName("blockAdsAndTrackers")]
    public bool BlockAdsAndTrackers { get; set; } = true;

    [JsonPropertyName("upgradeToHttps")]
    public bool UpgradeToHttps { get; set; } = true;

    [JsonPropertyName("blockScripts")]
    public bool BlockScripts { get; set; }

    [JsonPropertyName("blockFingerprinting")]
    public bool BlockFingerprinting { get; set; } = true;

    [JsonPropertyName("blockThirdPartyCookies")]
    public bool BlockThirdPartyCookies { get; set; } = true;

    [JsonPropertyName("forgetMeOnClose")]
    public bool ForgetMeOnClose { get; set; }

    [JsonPropertyName("adBlockMode")]
    public BraveAdBlockMode AdBlockMode { get; set; } = BraveAdBlockMode.Standard;

    [JsonPropertyName("httpsUpgradeMode")]
    public BraveHttpsUpgradeMode HttpsUpgradeMode { get; set; } = BraveHttpsUpgradeMode.Standard;

    [JsonPropertyName("fingerprintMode")]
    public BraveFingerprintMode FingerprintMode { get; set; } = BraveFingerprintMode.Standard;

    [JsonPropertyName("cookieMode")]
    public BraveCookieMode CookieMode { get; set; } = BraveCookieMode.BlockThirdParty;

    public BraveShieldSiteSettings Clone()
        => new()
        {
            ShieldsEnabled = ShieldsEnabled,
            BlockAdsAndTrackers = BlockAdsAndTrackers,
            UpgradeToHttps = UpgradeToHttps,
            BlockScripts = BlockScripts,
            BlockFingerprinting = BlockFingerprinting,
            BlockThirdPartyCookies = BlockThirdPartyCookies,
            ForgetMeOnClose = ForgetMeOnClose,
            AdBlockMode = AdBlockMode,
            HttpsUpgradeMode = HttpsUpgradeMode,
            FingerprintMode = FingerprintMode,
            CookieMode = CookieMode
        };

    public void CopyFrom(BraveShieldSiteSettings other)
    {
        ShieldsEnabled = other.ShieldsEnabled;
        BlockAdsAndTrackers = other.BlockAdsAndTrackers;
        UpgradeToHttps = other.UpgradeToHttps;
        BlockScripts = other.BlockScripts;
        BlockFingerprinting = other.BlockFingerprinting;
        BlockThirdPartyCookies = other.BlockThirdPartyCookies;
        ForgetMeOnClose = other.ForgetMeOnClose;
        AdBlockMode = other.AdBlockMode;
        HttpsUpgradeMode = other.HttpsUpgradeMode;
        FingerprintMode = other.FingerprintMode;
        CookieMode = other.CookieMode;
    }
}
