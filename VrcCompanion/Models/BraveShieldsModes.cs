namespace VrcCompanion.Models;

/// <summary>
/// Mirrors Brave's <c>AdBlockMode</c> mojom enum: aggressive, standard, allow.
/// </summary>
public enum BraveAdBlockMode
{
    Aggressive,
    Standard,
    Allow
}

/// <summary>
/// Mirrors Brave's <c>HttpsUpgradeMode</c> mojom enum: strict, standard, disabled.
/// </summary>
public enum BraveHttpsUpgradeMode
{
    Strict,
    Standard,
    Disabled
}

/// <summary>
/// Mirrors Brave's <c>FingerprintMode</c> mojom enum: strict, standard, allow.
/// </summary>
public enum BraveFingerprintMode
{
    Strict,
    Standard,
    Allow
}

/// <summary>
/// Mirrors Brave's <c>CookieBlockMode</c> mojom enum. The UI order is
/// block all, block third-party, allow all.
/// </summary>
public enum BraveCookieMode
{
    BlockAll,
    BlockThirdParty,
    AllowAll
}
