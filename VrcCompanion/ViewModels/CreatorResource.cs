using System;

namespace VrcCompanion.ViewModels;

/// <summary>A creator marketplace or tool that opens outside the app.</summary>
public sealed class CreatorResource
{
    public CreatorResource(
        string name,
        string category,
        string initial,
        string description,
        string siteUrl,
        string? searchUrlFormat = null)
    {
        Name = name;
        Category = category;
        Initial = initial;
        Description = description;
        SiteUrl = siteUrl;
        SearchUrlFormat = searchUrlFormat;
    }

    public string Name { get; }

    public string Category { get; }

    public string Initial { get; }

    public string Description { get; }

    public string SiteUrl { get; }

    public string? SearchUrlFormat { get; }

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
