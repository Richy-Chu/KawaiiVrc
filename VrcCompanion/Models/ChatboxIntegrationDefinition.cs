namespace VrcCompanion.Models;

/// <summary>
/// The complete integration catalog. Keys and ordering mirror MagicChatBox;
/// a provider is only shown as live when its module actually has data.
/// </summary>
public sealed record ChatboxIntegrationDefinition(
    string Key,
    string DisplayName,
    string Category,
    string Description,
    string SetupHint,
    int Priority,
    bool NeedsSetup = false,
    bool IsAvailable = true,
    string UnavailableReason = "");

public static class ChatboxIntegrationCatalog
{
    public static IReadOnlyList<ChatboxIntegrationDefinition> All { get; } =
        new ChatboxIntegrationDefinition[]
        {
            new("Status", "Personal status", "You",
                "Saved messages, groups and AFK. The automatic identity line.",
                "Add at least one saved status.", 10),

            new("Media", "Music display", "Audio",
                "Whatever is playing on your PC: Spotify, YouTube, browsers or a local player.",
                "Play something in any Windows media app.", 20),

            new("Lyrics", "Lyrics", "Audio",
                "The current synced lyric line from LRCLIB.",
                "Play a track with a matching LRCLIB entry.", 22),

            new("Spotify", "Spotify", "Audio",
                "Spotify playback details from the local Windows Spotify session.",
                "Start playback in the Spotify desktop app.", 25),

            new("Window", "Window activity", "You",
                "VR or desktop, and the app you are focused on. Local only.",
                "Focus another app after enabling this.", 30),

            new("VrcRadar", "VRChat Radar", "You",
                "World, instance and player counts read from VRChat's local log.",
                "Join a VRChat world with logging enabled.", 35),

            new("HeartRate", "Heart rate", "You",
                "Live heart rate from Pulsoid in the chatbox.",
                "Add a Pulsoid access token under Connections.", 40, true),

            new("Discord", "Discord", "Social",
                "Your voice channel and who is speaking, read from the Discord page in this app.",
                "Open Discord in this app, sign in, and join a voice channel.", 45, true),

            new("Twitch", "Twitch", "Social",
                "Live status, viewers, category and title from the Twitch API.",
                "Add a Twitch Client ID, access token and channel.", 50, true),

            new("TikTokLive", "TikTok", "Social",
                "Public TikTok follower count. LIVE event details are not available.",
                "Enter a public TikTok profile name under Connections.", 52),

            new("TrackerBattery", "VR gear battery", "VR & hardware",
                "Battery for the headset, controllers and all physical SlimeVR trackers, read from SteamVR and the local SlimeVR server.",
                "Start SteamVR and the SlimeVR server, then wake the controllers or trackers you want to see.", 60, true),

            new("VrPerformance", "VR performance", "VR & hardware",
                "Frame rate and dropped frames from SteamVR's compositor.",
                "Start SteamVR to read compositor frame timing.", 65, true),

            new("ComponentStats", "Component stats", "VR & hardware",
                "CPU, GPU, dedicated VRAM and memory load from the local machine.",
                "Uses Windows performance counters; GPU temperature needs vendor support.", 70),

            new("Soundpad", "Soundpad", "Audio",
                "The clip you just played, read from Soundpad's local remote-control pipe.",
                "Enable Soundpad remote control; it is read locally when the app is running.", 75),

            new("Voicemod", "Voicemod", "Audio",
                "Voicemod Control API state is not available in this build.",
                "Unavailable: needs a Voicemod Control API client key.", 80, false, false,
                "Unavailable — no Voicemod Control API bridge is configured."),

            new("Network", "Network stats", "VR & hardware",
                "Live download and upload rates for the active network.",
                "Uses local network counters; no setup.", 80),

            new("Weather", "Weather", "You",
                "Current conditions next to your local time.",
                "Set your city under Connections; no API key.", 85),

            new("Time", "Time", "You",
                "Your local time, zone and date.",
                "Local clock; no setup.", 90)
        };

    public static IReadOnlyList<string> Keys { get; } =
        All.Select(item => item.Key).ToList();

    public static ChatboxIntegrationDefinition Get(string key) =>
        All.FirstOrDefault(item => item.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
        ?? new ChatboxIntegrationDefinition(key, key, "Other", string.Empty, string.Empty, 100);

    public static IReadOnlyList<string> Categories { get; } =
        All.Select(item => item.Category).Distinct().ToList();
}
