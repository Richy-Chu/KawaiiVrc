using System.Windows.Media;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// VRChat's trust rank and profile-theme presentation, matching VRCX's defaults
/// and tag rules. Every player surface uses this so a rank never has a different
/// label or colour in another list.
/// </summary>
internal static class UserPresentation
{
    internal static readonly Color UntrustedColor = Color.FromRgb(0xCC, 0xCC, 0xCC);
    internal static readonly Color BasicColor = Color.FromRgb(0x17, 0x78, 0xFF);
    internal static readonly Color KnownColor = Color.FromRgb(0x2B, 0xCF, 0x5C);
    internal static readonly Color TrustedColor = Color.FromRgb(0xFF, 0x7B, 0x42);
    internal static readonly Color VeteranColor = Color.FromRgb(0xB1, 0x8F, 0xFF);
    internal static readonly Color VipColor = Color.FromRgb(0xFF, 0x26, 0x26);
    internal static readonly Color TrollColor = Color.FromRgb(0x78, 0x2F, 0x2F);

    private static readonly Brush UntrustedBrush = MakeBrush(UntrustedColor);
    private static readonly Brush BasicBrush = MakeBrush(BasicColor);
    private static readonly Brush KnownBrush = MakeBrush(KnownColor);
    private static readonly Brush TrustedBrush = MakeBrush(TrustedColor);
    private static readonly Brush VeteranBrush = MakeBrush(VeteranColor);
    private static readonly Brush VipBrush = MakeBrush(VipColor);
    private static readonly Brush TrollBrush = MakeBrush(TrollColor);
    private static readonly Brush DefaultThemeIconBrush = MakeBrush("#F2F4FA");
    private static readonly Brush DefaultThemeButtonBrush = MakeBrush("#8B5CF6");
    private static readonly Brush DefaultThemeSubtextBrush = MakeBrush("#9AA4BA");
    internal static readonly Brush DefaultNameBrush = MakeBrush("#F0F0F8");

    internal static TrustInfo Trust(IEnumerable<string>? tags, string? developerType = null)
    {
        var set = new HashSet<string>(
            (tags ?? Enumerable.Empty<string>())
                .Where(tag => !string.IsNullOrWhiteSpace(tag)),
            StringComparer.OrdinalIgnoreCase);

        var isModerator =
            set.Contains("admin_moderator") ||
            (!string.IsNullOrWhiteSpace(developerType) &&
             !string.Equals(developerType, "none", StringComparison.OrdinalIgnoreCase));
        var isTroll = set.Contains("system_troll");
        var isProbableTroll = set.Contains("system_probable_troll") && !isTroll;

        string label;
        string colorKey;
        int sort;
        Color color;

        // VRCX checks ranks highest-first. Keep the same precedence so a user
        // carrying several trust tags always shows their highest rank.
        if (set.Contains("system_trust_legend"))
        {
            label = "Trusted Legend";
            colorKey = "veteran";
            color = VeteranColor;
            sort = 6;
        }
        else if (set.Contains("system_trust_veteran"))
        {
            label = "Trusted User";
            colorKey = "veteran";
            color = VeteranColor;
            sort = 5;
        }
        else if (set.Contains("system_trust_trusted"))
        {
            label = "Known User";
            colorKey = "trusted";
            color = TrustedColor;
            sort = 4;
        }
        else if (set.Contains("system_trust_known"))
        {
            label = "User";
            colorKey = "known";
            color = KnownColor;
            sort = 3;
        }
        else if (set.Contains("system_trust_basic"))
        {
            label = "New User";
            colorKey = "basic";
            color = BasicColor;
            sort = 2;
        }
        else
        {
            label = "Visitor";
            colorKey = "untrusted";
            color = UntrustedColor;
            sort = 1;
        }

        if (isTroll || isProbableTroll)
        {
            colorKey = "troll";
            color = TrollColor;
        }

        if (isModerator)
        {
            colorKey = "vip";
            color = VipColor;
        }

        return new TrustInfo(
            label,
            colorKey,
            color,
            sort + (isTroll || isProbableTroll ? 0.1 : 0) + (isModerator ? 0.3 : 0),
            isModerator,
            isTroll,
            isProbableTroll);
    }

    public static string TrustLabel(IEnumerable<string>? tags, string? developerType = null)
        => Trust(tags, developerType).Label;

    internal static Brush TrustBrush(IEnumerable<string>? tags, string? developerType = null)
        => BrushFor(Trust(tags, developerType).ColorKey);

    internal static Brush BrushFor(string colorKey) => colorKey switch
    {
        "basic" => BasicBrush,
        "known" => KnownBrush,
        "trusted" => TrustedBrush,
        "veteran" => VeteranBrush,
        "vip" => VipBrush,
        "troll" => TrollBrush,
        _ => UntrustedBrush
    };

    internal static string? IconFrameUrl(string? iconFrame)
        => CosmeticCatalog.DisplayUrl(CosmeticCatalog.IconFrame, iconFrame);

    internal static string? IconFrameIntroUrl(string? iconFrame)
        => CosmeticCatalog.IntroUrl(CosmeticCatalog.IconFrame, iconFrame);

    internal static string? ProfileEffectUrl(string? profileEffect)
        => CosmeticCatalog.DisplayUrl(CosmeticCatalog.ProfileEffect, profileEffect);

    internal static string? ProfileEffectIntroUrl(string? profileEffect)
        => CosmeticCatalog.IntroUrl(CosmeticCatalog.ProfileEffect, profileEffect);

    internal static string? NameplateEffectUrl(string? nameplateEffect)
        => CosmeticCatalog.DisplayUrl(CosmeticCatalog.NameplateEffect, nameplateEffect);

    internal static string? NameplateEffectIntroUrl(string? nameplateEffect)
        => CosmeticCatalog.IntroUrl(CosmeticCatalog.NameplateEffect, nameplateEffect);

    /// <summary>
    /// VRCX lets a profile choose its icon/button/subtext colours and nudges
    /// unusable extremes so text stays legible on its dark profile surface.
    /// </summary>
    internal static ProfileThemeColors Theme(
        string? iconColor,
        string? buttonColor,
        string? subtextColor)
    {
        return new ProfileThemeColors(
            ReadableProfileColor(iconColor, DefaultThemeIconBrush),
            ReadableProfileColor(buttonColor, DefaultThemeButtonBrush),
            ReadableProfileColor(subtextColor, DefaultThemeSubtextBrush));
    }

    private static Brush ReadableProfileColor(string? value, Brush fallback)
    {
        if (!TryParseHex(value, out var color))
        {
            return fallback;
        }

        // Match VRCX's readability adjustment: very dark colours become a
        // lighter tint, and near-white colours do not glare against dark cards.
        var luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        if (luminance < 42)
        {
            color = Blend(color, Colors.White, 0.42);
        }
        else if (luminance > 236)
        {
            color = Blend(color, Colors.Black, 0.12);
        }

        return MakeBrush(color);
    }

    internal static Brush? NameplateGradient(string? nameplateEffect)
    {
        var cosmetic = CosmeticCatalog.Find(CosmeticCatalog.NameplateEffect, nameplateEffect);
        if (!TryParseHex(cosmetic?.Metadata?.GradientStart, out var start) ||
            !TryParseHex(cosmetic?.Metadata?.GradientEnd, out var end))
        {
            return null;
        }

        var brush = new LinearGradientBrush(start, end, new System.Windows.Point(0, 0.5), new System.Windows.Point(1, 0.5));
        brush.Freeze();
        return brush;
    }

    /// <summary>Black or white text for a solid profile button colour.</summary>
    internal static Brush ButtonText(Brush background)
    {
        if (background is not SolidColorBrush solid)
        {
            return Brushes.White;
        }

        var color = solid.Color;
        var luminance = (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);
        return luminance > 150 ? DarkButtonTextBrush : Brushes.White;
    }

    internal static bool TryParseHex(string? value, out Color color)
    {
        color = default;
        var text = value?.Trim().TrimStart('#');
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (text.Length == 3)
        {
            text = string.Concat(text.Select(c => new string(c, 2)));
        }

        if (text.Length != 6 ||
            !uint.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
        {
            return false;
        }

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a + ((b - a) * amount));
        return Color.FromRgb(Mix(from.R, to.R), Mix(from.G, to.G), Mix(from.B, to.B));
    }

    private static Brush MakeBrush(string color) => MakeBrush((Color)ColorConverter.ConvertFromString(color));

    private static Brush MakeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static readonly Brush DarkButtonTextBrush = MakeBrush(Color.FromRgb(0x0B, 0x0D, 0x12));
}

internal readonly record struct TrustInfo(
    string Label,
    string ColorKey,
    Color Color,
    double Sort,
    bool IsModerator,
    bool IsTroll,
    bool IsProbableTroll);

internal readonly record struct ProfileThemeColors(
    Brush Icon,
    Brush Button,
    Brush Subtext);
