using System.Diagnostics;
using System.Globalization;
using System.Text;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// MagicChatBox routes providers by VR state (SteamVR/OVRServer presence).
/// The result is cached briefly because process enumeration is not free.
/// </summary>
public static class VrModeDetector
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private static readonly object Sync = new();
    private static DateTime _checkedAt = DateTime.MinValue;
    private static bool _running;

    /// <summary>Tests stand in for SteamVR/OVRServer presence; production probes processes.</summary>
    internal static Func<bool> VrRunningProbe { get; set; } = DetectVrRunning;

    internal static void ClearCacheForTests()
    {
        lock (Sync)
        {
            _checkedAt = DateTime.MinValue;
        }
    }

    internal static void ResetForTests()
    {
        VrRunningProbe = DetectVrRunning;
        lock (Sync)
        {
            _checkedAt = DateTime.MinValue;
            _running = false;
        }
    }

    internal static bool DetectVrRunning()
        => IsProcessRunning("vrmonitor") || IsProcessRunning("OVRServer_x64");

    public static bool IsVrRunning()
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            if (now - _checkedAt < CacheFor)
            {
                return _running;
            }

            _checkedAt = now;
            _running = VrRunningProbe();
            return _running;
        }
    }

    private static bool IsProcessRunning(string name)
    {
        try
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var process in processes)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// One independently enabled source of a chatbox segment. This mirrors
/// MagicChatBox's provider design: a provider never knows about the others, it
/// only returns its own text or null, and the builder decides what fits.
/// </summary>
public interface IChatboxProvider
{
    string Key { get; }

    string DisplayName { get; }

    int Priority { get; }

    bool IsEnabled(ChatboxProviderContext context);

    string? Build(ChatboxProviderContext context);
}

public sealed record ChatboxProviderContext
{
    public bool IsVR { get; init; } = true;
    public DateTime Now { get; init; } = DateTime.Now;
    public int Limit { get; init; } = OscMessage.ChatboxLimit;
    public bool IncludeStatus { get; init; }
    public bool IncludeMedia { get; init; }
    public bool IncludeLyrics { get; init; }
    public bool IncludeWindow { get; init; }
    public bool IncludeTime { get; init; }
    public string ActiveStatus { get; init; } = string.Empty;
    public bool ShowStatusIcon { get; init; }
    public string StatusIcon { get; init; } = "💭";
    public NowPlayingInfo? Track { get; init; }
    public string Lyric { get; init; } = string.Empty;
    public WindowActivityInfo? Window { get; init; }
    public string MusicFormat { get; init; } = DefaultMusicFormat;
    public string LyricsFormat { get; init; } = DefaultLyricsFormat;
    public string WindowFormat { get; init; } = "🪟 {app}";
    public string TimeFormat { get; init; } = "{time}";
    public IReadOnlyDictionary<string, bool> EnabledIntegrations { get; init; } =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, string> IntegrationTexts { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public bool SuppressSpotifyInMedia { get; init; }

    public const string DefaultMusicFormat = "{state} {display}{source}";
    public const string DefaultLyricsFormat = "♪ {lyric}";
}

public sealed record ChatboxSegment(string ProviderKey, string Text, int Priority);

public sealed record ChatboxBuildResult(
    string Message,
    IReadOnlyList<string> IncludedProviders,
    IReadOnlyList<string> TrimmedProviders,
    IReadOnlyDictionary<string, int> SegmentLengths)
{
    public int Length => Message.Length;

    public bool ExceededLimit => TrimmedProviders.Count > 0;
}

/// <summary>
/// Composes the one line VRChat receives. Providers are evaluated in the saved
/// order; when the line overflows, the provider with the highest priority
/// number is removed first. If one provider still cannot fit, it is shortened
/// rather than the prefix/suffix being lost.
/// </summary>
public sealed class ChatboxOutputBuilder
{
    public const int MaxLength = OscMessage.ChatboxLimit;

    public ChatboxBuildResult Build(
        IEnumerable<IChatboxProvider> providers,
        ChatboxProviderContext context,
        string separator,
        bool separateWithEnters,
        string prefix,
        string suffix)
    {
        var joiner = separateWithEnters
            ? "\n"
            : string.IsNullOrWhiteSpace(separator) ? " ┆ " : separator;
        prefix = ExpandNewlines(prefix ?? string.Empty);
        suffix = ExpandNewlines(suffix ?? string.Empty);
        var limit = Math.Clamp(context.Limit, 0, MaxLength);

        var collected = new List<ChatboxSegment>();
        foreach (var provider in providers)
        {
            if (!provider.IsEnabled(context))
            {
                continue;
            }

            var text = provider.Build(context);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            collected.Add(new ChatboxSegment(
                provider.Key,
                ExpandNewlines(text).Trim(),
                provider.Priority));
        }

        var lengths = collected.ToDictionary(
            segment => segment.ProviderKey,
            segment => segment.Text.Length,
            StringComparer.OrdinalIgnoreCase);
        var trimmed = new List<string>();

        while (collected.Count > 1 && Measure(collected, joiner, prefix, suffix) > limit)
        {
            var worst = 0;
            for (var i = 1; i < collected.Count; i++)
            {
                if (collected[i].Priority > collected[worst].Priority)
                {
                    worst = i;
                }
            }

            trimmed.Add(collected[worst].ProviderKey);
            collected.RemoveAt(worst);
        }

        if (collected.Count == 1)
        {
            var survivor = collected[0];
            var budget = limit - prefix.Length - suffix.Length;
            var fitted = Fit(survivor.Text, budget);
            if (fitted.Length != survivor.Text.Length)
            {
                lengths[survivor.ProviderKey] = fitted.Length;
                if (fitted.Length == 0)
                {
                    trimmed.Add(survivor.ProviderKey);
                    collected.Clear();
                }
                else
                {
                    collected[0] = survivor with { Text = fitted };
                }
            }
        }

        var message = collected.Count == 0
            ? string.Empty
            : prefix + string.Join(joiner, collected.Select(segment => segment.Text)) + suffix;
        message = Clamp(message, limit);

        return new ChatboxBuildResult(
            message,
            collected.Select(segment => segment.ProviderKey).ToList(),
            trimmed,
            lengths);
    }

    public string? BuildPreview(IChatboxProvider provider, ChatboxProviderContext context)
        => provider.IsEnabled(context) ? provider.Build(context)?.Trim() : null;

    internal static string Fit(string text, int budget)
    {
        if (budget <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= budget)
        {
            return text;
        }

        var mark = budget >= 2;
        var cut = mark ? budget - 1 : budget;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return mark ? text[..cut] + "…" : text[..cut];
    }

    private static int Measure(
        IReadOnlyList<ChatboxSegment> segments,
        string joiner,
        string prefix,
        string suffix)
    {
        var length = prefix.Length + suffix.Length + segments.Sum(segment => segment.Text.Length);
        if (segments.Count > 1)
        {
            length += joiner.Length * (segments.Count - 1);
        }

        return length;
    }

    private static string Clamp(string message, int limit)
    {
        if (message.Length <= limit)
        {
            return message;
        }

        var cut = limit;
        if (cut <= 0)
        {
            return string.Empty;
        }

        if (char.IsHighSurrogate(message[cut - 1]))
        {
            cut--;
        }

        return message[..cut];
    }

    private static string ExpandNewlines(string value)
        => value.Replace("\\n", "\n", StringComparison.Ordinal);
}

public sealed class StatusChatboxProvider : IChatboxProvider
{
    public string Key => "Status";
    public string DisplayName => "Personal status";
    public int Priority => 10;

    public bool IsEnabled(ChatboxProviderContext context)
        => context.IncludeStatus && !string.IsNullOrWhiteSpace(context.ActiveStatus);

    public string? Build(ChatboxProviderContext context)
    {
        if (!IsEnabled(context))
        {
            return null;
        }

        var message = context.ActiveStatus.Trim();
        return context.ShowStatusIcon && !string.IsNullOrWhiteSpace(context.StatusIcon)
            ? $"{context.StatusIcon.Trim()} {message}"
            : message;
    }
}

public sealed class MediaChatboxProvider : IChatboxProvider
{
    public string Key => "Media";
    public string DisplayName => "Music display";
    public int Priority => 20;

    public bool IsEnabled(ChatboxProviderContext context)
        => context.IncludeMedia && context.Track is not null;

    public string? Build(ChatboxProviderContext context)
    {
        var track = context.Track;
        if (!IsEnabled(context) || track is null)
        {
            return null;
        }

        if (context.SuppressSpotifyInMedia &&
            track.Source.Contains("spotify", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var source = string.IsNullOrWhiteSpace(track.Source) ? string.Empty : $" · {track.Source}";
        var state = track.IsPlaying ? "▶" : "⏸";
        var text = ChatboxTemplate
            .Format(context.MusicFormat, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["display"] = track.DisplayText,
                ["title"] = track.Title,
                ["artist"] = track.Artist,
                ["sourceName"] = track.Source,
                ["source"] = source,
                ["state"] = state,
                ["elapsed"] = FormatClock(track.Position),
                ["remaining"] = FormatClock(track.Duration - track.Position, roundUp: true),
                ["duration"] = FormatClock(track.Duration),
                ["progress"] = ProgressBar(track.Position, track.Duration)
            });

        return string.IsNullOrWhiteSpace(text) ? track.ChatboxText : text;
    }

    internal static string FormatClock(TimeSpan value, bool roundUp = false)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        if (roundUp && value > TimeSpan.Zero && value.TotalSeconds % 1 > 0)
        {
            value = TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));
        }

        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    internal static string ProgressBar(TimeSpan position, TimeSpan duration, int cells = 10)
    {
        if (duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        var ratio = Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
        var filled = Math.Clamp((int)Math.Round(ratio * cells), 0, cells);
        return "▰" + new string('▬', filled) + new string('▱', cells - filled) + "▮";
    }
}

public sealed class LyricsChatboxProvider : IChatboxProvider
{
    public string Key => "Lyrics";
    public string DisplayName => "Synced lyrics";
    public int Priority => 22;

    public bool IsEnabled(ChatboxProviderContext context)
        => context.IncludeLyrics && !string.IsNullOrWhiteSpace(context.Lyric);

    public string? Build(ChatboxProviderContext context)
    {
        if (!IsEnabled(context))
        {
            return null;
        }

        return ChatboxTemplate.Format(
            context.LyricsFormat,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["lyric"] = context.Lyric.Trim(),
                ["title"] = context.Track?.Title ?? string.Empty,
                ["artist"] = context.Track?.Artist ?? string.Empty
            });
    }
}

public sealed class WindowChatboxProvider : IChatboxProvider
{
    public string Key => "Window";
    public string DisplayName => "Window activity";
    public int Priority => 30;

    public bool IsEnabled(ChatboxProviderContext context)
        => context.IncludeWindow && context.Window is not null;

    public string? Build(ChatboxProviderContext context)
    {
        var window = context.Window;
        if (!IsEnabled(context) || window is null)
        {
            return null;
        }

        var heading = context.IsVR ? "In VR" : "On desktop";
        return ChatboxTemplate.Format(
            context.WindowFormat,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["app"] = window.App,
                ["title"] = window.Title,
                ["window"] = window.DisplayText,
                ["heading"] = heading,
                ["mode"] = context.IsVR ? "VR" : "Desktop"
            });
    }
}

public sealed class SnapshotChatboxProvider : IChatboxProvider
{
    public SnapshotChatboxProvider(string key, string displayName, int priority)
    {
        Key = key;
        DisplayName = displayName;
        Priority = priority;
    }

    public string Key { get; }

    public string DisplayName { get; }

    public int Priority { get; }

    public bool IsEnabled(ChatboxProviderContext context)
        => context.EnabledIntegrations.TryGetValue(Key, out var enabled) &&
           enabled &&
           context.IntegrationTexts.TryGetValue(Key, out var text) &&
           !string.IsNullOrWhiteSpace(text);

    public string? Build(ChatboxProviderContext context)
        => IsEnabled(context) ? context.IntegrationTexts[Key].Trim() : null;
}

public sealed class TimeChatboxProvider : IChatboxProvider
{
    public string Key => "Time";
    public string DisplayName => "Local time";
    public int Priority => 90;

    public bool IsEnabled(ChatboxProviderContext context) => context.IncludeTime;

    public string? Build(ChatboxProviderContext context)
    {
        if (!context.IncludeTime)
        {
            return null;
        }

        var now = context.Now;
        return ChatboxTemplate.Format(
            context.TimeFormat,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["time"] = now.ToString("h:mm tt", CultureInfo.CurrentCulture),
                ["time12"] = now.ToString("h:mm tt", CultureInfo.CurrentCulture),
                ["zone"] = TimeZoneInfo.Local.StandardName,
                ["date"] = now.ToString("ddd d MMM", CultureInfo.CurrentCulture),
                ["day"] = now.ToString("dddd", CultureInfo.CurrentCulture)
            });
    }
}

internal static class ChatboxTemplate
{
    public static string Format(string template, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(template))
        {
            return string.Empty;
        }

        var text = template;
        foreach (var pair in values)
        {
            text = text.Replace("{" + pair.Key + "}", pair.Value, StringComparison.OrdinalIgnoreCase);
        }

        return text.Trim();
    }
}
