namespace VrcCompanion.ViewModels;

internal static class UserPresentation
{
    public static string TrustLabel(IEnumerable<string>? tags)
    {
        var set = tags ?? Enumerable.Empty<string>();

        if (set.Contains("system_trust_legend", StringComparer.OrdinalIgnoreCase)) return "Trusted Legend";
        if (set.Contains("system_trust_veteran", StringComparer.OrdinalIgnoreCase)) return "Trusted User";
        if (set.Contains("system_trust_trusted", StringComparer.OrdinalIgnoreCase)) return "Known User";
        if (set.Contains("system_trust_known", StringComparer.OrdinalIgnoreCase)) return "User";
        if (set.Contains("system_trust_basic", StringComparer.OrdinalIgnoreCase)) return "New User";
        if (set.Contains("system_trust_visitor", StringComparer.OrdinalIgnoreCase)) return "Visitor";
        return "User";
    }
}
