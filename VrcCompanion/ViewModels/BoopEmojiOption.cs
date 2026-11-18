namespace VrcCompanion.ViewModels;

/// <summary>
/// One selectable boop emoji. The ids and previews match VRChat's built-in
/// emoji catalog; VRCX-0 sends the id as <c>emojiId</c> for these.
/// </summary>
public sealed class BoopEmojiOption
{
    public BoopEmojiOption(
        string id,
        string name,
        string imageUrl,
        string? inventoryItemId = null)
    {
        Id = id;
        Name = name;
        ImageUrl = imageUrl;
        InventoryItemId = inventoryItemId;
    }

    public string Id { get; }

    public string Name { get; }

    public string ImageUrl { get; }

    /// <summary>Set for inventory emoji; sent as <c>inventoryItemId</c>.</summary>
    public string? InventoryItemId { get; }

    /// <summary>The default emoji list, in the same order VRCX-0 displays it.</summary>
    public static IReadOnlyList<BoopEmojiOption> DefaultCatalog { get; } = BuildCatalog();

    /// <summary>
    /// Finds the built-in emoji behind a received boop. VRChat's constants do
    /// not always use the same separators as the display names (for example
    /// default_handwave vs. Hand Wave), so compare normalized keys.
    /// </summary>
    public static BoopEmojiOption? FindById(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var direct = DefaultCatalog.FirstOrDefault(option =>
            string.Equals(option.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        var key = NormalizeKey(id);
        if (Aliases.TryGetValue(key, out var alias))
        {
            key = alias;
        }

        return DefaultCatalog.FirstOrDefault(option =>
            string.Equals(NormalizeKey(option.Id), key, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NormalizeKey(option.Name), key, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["like"] = "thumbsup",
        ["dislike"] = "thumbsdown",
        ["accessibilityarrow"] = "arrowpoint",
        ["accessibilityblind"] = "cantsee",
        ["accessibilitydeafened"] = "noheadphones",
        ["accessibilitymuted"] = "nomic"
    };

    private static string NormalizeKey(string value)
    {
        var key = new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
        return key.StartsWith("default", StringComparison.Ordinal)
            ? key["default".Length..]
            : key;
    }

    private static IReadOnlyList<BoopEmojiOption> BuildCatalog()
    {
        var entries = new (string Name, string File)[]
        {
            ("Angry", "Angry.webp"),
            ("Blushing", "Blush.webp"),
            ("Crying", "Crying.webp"),
            ("Frown", "Frown.webp"),
            ("Hand Wave", "Handwave.webp"),
            ("Hang Ten", "Summer_Hangten.webp"),
            ("In Love", "Inlove.webp"),
            ("Jack O Lantern", "Fall_Jackolantern.webp"),
            ("Kiss", "Kiss.webp"),
            ("Laugh", "Laugh.webp"),
            ("Skull", "Fall_Skull.webp"),
            ("Smile", "Smile.webp"),
            ("Spooky Ghost", "Fall_Ghost.webp"),
            ("Stoic", "Stoic.webp"),
            ("Sunglasses", "Sunglasses.webp"),
            ("Thinking", "Thinking.webp"),
            ("Thumbs Down", "Dislike.webp"),
            ("Thumbs Up", "Like.webp"),
            ("Tongue Out", "Tongue.webp"),
            ("Wow", "Wow.webp"),
            ("Arrow Point", "Accessibility_Arrow.webp"),
            ("Can't see", "Accessibility_Blind.webp"),
            ("Hourglass", "Accessibility_Hourglass.webp"),
            ("Keyboard", "Accessibility_Keyboard.webp"),
            ("No Headphones", "Accessibility_Deafened.webp"),
            ("No Mic", "Accessibility_Muted.webp"),
            ("Portal", "Accessibility_Portal.webp"),
            ("Shush", "Accessibility_Shush.webp"),
            ("Bats", "Fall_Bat.webp"),
            ("Cloud", "Cloud.webp"),
            ("Fire", "Fire.webp"),
            ("Snow Fall", "Winter_Snowflake.webp"),
            ("Snowball", "Snowball_-_emoji_animation_type.gif"),
            ("Splash", "Summer_Splash.webp"),
            ("Web", "Fall_Web.webp"),
            ("Beer", "Beer.webp"),
            ("Candy", "Fall_Candy.webp"),
            ("Candy Cane", "Winter_Candycane.webp"),
            ("Candy Corn", "Fall_CandyCorn.webp"),
            ("Champagne", "Winter_Champagneclink.webp"),
            ("Drink", "Summer_Coconut_Drink.webp"),
            ("Gingerbread", "Winter_Gingerbreadman.webp"),
            ("Ice Cream", "Summer_Icecream.webp"),
            ("Pineapple", "Summer_Pineapple.webp"),
            ("Pizza", "Pizza.webp"),
            ("Tomato", "Tomato.webp"),
            ("Beachball", "Summer_Beachball.webp"),
            ("Coal", "Winter_Coal.webp"),
            ("Confetti", "Winter_ConfettiPopper.webp"),
            ("Gift", "Gift.webp"),
            ("Gifts", "Winter_Gifts.webp"),
            ("Life Ring", "Lifering.webp"),
            ("Mistletoe", "Winter_Mistletoe.webp"),
            ("Money", "Money.webp"),
            ("Neon Shades", "Summer_Neonshades.webp"),
            ("Sun Lotion", "Summer_Sunlotion.webp"),
            ("Boo", "Fall_BOO.webp"),
            ("Broken Heart", "Brokenheart.webp"),
            ("Exclamation", "Exclaim.webp"),
            ("Go", "Go.webp"),
            ("Heart", "Love.webp"),
            ("Music Note", "Music.webp"),
            ("Question", "Question.webp"),
            ("Stop", "Stop.webp"),
            ("Zzz", "ZZZZ.webp")
        };

        return entries
            .Select(entry => new BoopEmojiOption(
                $"default_{entry.Name.Replace(" ", "_").ToLowerInvariant()}",
                entry.Name,
                $"https://wiki-files.vrchat.com/{entry.File}"))
            .ToArray();
    }
}
