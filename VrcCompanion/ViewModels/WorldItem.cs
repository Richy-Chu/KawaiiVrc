using System.Globalization;
using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

public sealed class WorldItem : ViewModelBase
{
    private readonly VrcWorld _world;
    private ImageSource? _image;
    private bool _isFavorite;
    private bool _isCurrentWorld;

    public WorldItem(VrcWorld world)
    {
        _world = world;
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (SetProperty(ref _isFavorite, value))
            {
                OnPropertyChanged(nameof(FavoriteGlyph));
            }
        }
    }

    public string FavoriteGlyph => IsFavorite ? "\uE735" : "\uE734";

    /// <summary>The world this account is in right now, marked in search results.</summary>
    public bool IsCurrentWorld
    {
        get => _isCurrentWorld;
        set => SetProperty(ref _isCurrentWorld, value);
    }

    public string CurrentWorldText => "YOU'RE HERE";

    public string Id => _world.Id;

    public string Name => string.IsNullOrWhiteSpace(_world.Name) ? "Untitled world" : _world.Name;

    public string AuthorName => string.IsNullOrWhiteSpace(_world.AuthorName) ? "Unknown creator" : _world.AuthorName;

    public string AuthorText => $"by {AuthorName}";

    public string MetaText => $"{_world.Occupants:N0} online · {_world.Capacity} capacity";

    public string PlatformsText
    {
        get
        {
            var platforms = _world.SupportedPlatforms
                .Select(platform => platform.ToLowerInvariant() switch
                {
                    "standalonewindows" or "standalonewindows64" => "PC",
                    "android" => "Quest",
                    "ios" => "iOS",
                    _ => platform
                })
                .Where(platform => !string.IsNullOrWhiteSpace(platform))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return platforms.Length == 0 ? "PC" : string.Join(" / ", platforms);
        }
    }

    public string TagsText
    {
        get
        {
            var tags = _world.Tags
                .Where(tag => tag.StartsWith("author_tag_", StringComparison.OrdinalIgnoreCase))
                .Select(tag => tag["author_tag_".Length..].Replace('_', ' ').Trim())
                .Where(tag => tag.Length > 0)
                .Select(tag => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tag))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToArray();

            return tags.Length == 0 ? "Community world" : string.Join(" · ", tags);
        }
    }

    public string ThumbnailUrl
        => ImageUrlHelper.PickWorldThumbnailUrl(
            _world.ThumbnailImageUrl,
            _world.ImageUrl,
            256) ?? string.Empty;

    public string WebUrl => $"https://vrchat.com/home/world/{Id}";

    public ImageSource? Image
    {
        get => _image;
        set => SetProperty(ref _image, value);
    }
}
