using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VrcCompanion.Models;

/// <summary>A VRChat file record. Emoji files are tagged <c>emoji</c>.</summary>
public sealed class VrcFile
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("ownerId")]
    public string OwnerId { get; set; } = string.Empty;

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = new();

    [JsonPropertyName("versions")]
    public List<VrcFileVersion> Versions { get; set; } = new();

    public string FriendlyName =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName!.Trim()
        : !string.IsNullOrWhiteSpace(Name) ? Name!.Trim()
        : "Custom emoji";

    public string ImageUrl =>
        Versions
            .LastOrDefault(version => !string.IsNullOrWhiteSpace(version.File?.Url))
            ?.File?.Url?.Trim() ?? string.Empty;

    /// <summary>
    /// VRChat names avatar images "Avatar - Name - Image - build". VRCX-0 uses
    /// that embedded name for profiles whose avatar id is hidden; custom icons
    /// do not match the pattern and must not be treated as avatars.
    /// </summary>
    public string? AvatarName
    {
        get
        {
            var match = AvatarNamePattern.Match(Name ?? string.Empty);
            if (!match.Success)
            {
                return null;
            }

            var name = RestoreFileNameSymbols(match.Groups["name"].Value.Trim());
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
    }

    /// <summary>
    /// True when this file is an avatar image, not a custom icon. VRChat does
    /// not always embed the avatar name: VRC+ avatar icons can be named
    /// file_..._blob and carry the <c>avatarimage</c> tag instead. VRCX only
    /// rejects the <c>icon</c> tag and otherwise trusts the uploader id.
    /// </summary>
    public bool IsAvatarImage =>
        !string.IsNullOrWhiteSpace(OwnerId) &&
        !Tags.Any(tag => string.Equals(tag, "icon", StringComparison.OrdinalIgnoreCase));

    private static readonly Regex AvatarNamePattern = new(
        @"Avatar - (?<name>.*) - Image -",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // VRChat replaces these characters in uploaded file names. VRCX-0 restores
    // them before showing the embedded avatar name.
    private static readonly Dictionary<char, char> FileNameSymbols = new()
    {
        ['ʻ'] = '\'', ['ʼ'] = '\'', ['ˋ'] = '`', ['˴'] = '\'',
        ['∼'] = '~', ['˸'] = ':', ['‚'] = ',', ['？'] = '?',
        ['ǃ'] = '!', ['＂'] = '"', ['≺'] = '<', ['≻'] = '>',
        ['․'] = '.', ['＾'] = '^', ['｛'] = '{', ['｝'] = '}',
        ['［'] = '[', ['］'] = ']', ['（'] = '(', ['）'] = ')',
        ['｜'] = '|', ['∗'] = '*'
    };

    private static string RestoreFileNameSymbols(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(FileNameSymbols.TryGetValue(character, out var replacement)
                ? replacement
                : character);
        }

        return builder.ToString();
    }
}

public sealed class VrcFileVersion
{
    [JsonPropertyName("file")]
    public VrcFileAsset? File { get; set; }
}

public sealed class VrcFileAsset
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}
