namespace VrcCompanion.Services;

/// <summary>
/// Parses the common ways people share VRChat instances: launch URLs, short
/// links, full locations, and bare instance IDs.
/// </summary>
public static class InstanceLinkParser
{
    public sealed record Parsed(string? Location, string? ShortName);

    public static bool TryParse(string? input, string? fallbackWorldId, out Parsed parsed, out string error)
    {
        parsed = new Parsed(null, null);
        error = string.Empty;

        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            error = "Paste a VRChat instance link, short code, or instance ID.";
            return false;
        }

        // A full world:instance location copied from the game or another app.
        if (LaunchService.IsLocation(text))
        {
            parsed = new Parsed(text, null);
            return true;
        }

        // A bare instance ID, joined to the world currently open in the dialog.
        if (!text.Contains("://", StringComparison.Ordinal) &&
            !text.Contains('/', StringComparison.Ordinal) &&
            text.Contains('~', StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(fallbackWorldId))
        {
            parsed = new Parsed($"{fallbackWorldId}:{text}", null);
            return true;
        }

        if (text.StartsWith("vrchat://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("vrch.at/", StringComparison.OrdinalIgnoreCase))
        {
            var url = text.StartsWith("vrch.at/", StringComparison.OrdinalIgnoreCase)
                ? "https://" + text
                : text;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                error = "That link could not be read.";
                return false;
            }

            var query = ParseQuery(uri.Query);
            if (query.TryGetValue("id", out var id) && LaunchService.IsLocation(id))
            {
                parsed = new Parsed(id, null);
                return true;
            }

            if (query.TryGetValue("worldId", out var worldId) &&
                query.TryGetValue("instanceId", out var instanceId) &&
                worldId.StartsWith("wrld_", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(instanceId))
            {
                parsed = new Parsed($"{worldId}:{instanceId}", null);
                return true;
            }

            if (query.TryGetValue("shortName", out var shortFromQuery) &&
                !string.IsNullOrWhiteSpace(shortFromQuery))
            {
                parsed = new Parsed(null, shortFromQuery);
                return true;
            }

            var path = uri.AbsolutePath.Trim('/');
            if (path.StartsWith("i/", StringComparison.OrdinalIgnoreCase))
            {
                parsed = new Parsed(null, path[2..]);
                return true;
            }

            // https://vrch.at/<shortName>
            if (!string.IsNullOrWhiteSpace(uri.Host) &&
                uri.Host.Contains("vrch.at", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(path) &&
                !path.Contains('/', StringComparison.Ordinal))
            {
                parsed = new Parsed(null, path);
                return true;
            }

            error = "That link does not point at a VRChat instance.";
            return false;
        }

        // Anything else is treated as an instance short code and resolved by
        // the API in the caller.
        if (!text.Contains(' ', StringComparison.Ordinal) &&
            !text.Contains('~', StringComparison.Ordinal) &&
            text.Length <= 32)
        {
            parsed = new Parsed(null, text);
            return true;
        }

        error = "That does not look like a VRChat instance link or short code.";
        return false;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query))
        {
            return values;
        }

        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var value = Uri.UnescapeDataString(pair[(separator + 1)..]);
            if (!string.IsNullOrWhiteSpace(key))
            {
                values[key] = value;
            }
        }

        return values;
    }
}
