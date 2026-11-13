using System;

namespace VrcCompanion.ViewModels;

/// <summary>A creator marketplace or tool that opens outside the app.</summary>
public sealed record CreatorResource(
    string Name,
    string Category,
    string Initial,
    string Description,
    string SiteUrl,
    string? SearchUrlFormat = null)
{
    public bool CanSearch => !string.IsNullOrWhiteSpace(SearchUrlFormat);

    /// <summary>Builds a search link, or null when the site has no search page.</summary>
    public string? BuildSearchUrl(string? query)
    {
        var trimmed = query?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || string.IsNullOrWhiteSpace(SearchUrlFormat))
        {
            return null;
        }

        return string.Format(SearchUrlFormat, Uri.EscapeDataString(trimmed));
    }
}
