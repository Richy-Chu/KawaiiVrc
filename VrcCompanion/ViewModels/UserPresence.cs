using System.Windows;
using System.Windows.Media;

namespace VrcCompanion.ViewModels;

internal enum UserPresenceState
{
    Offline,
    Active,
    Online
}

/// <summary>
/// One presence rule set for every player/user surface, following VRCX's
/// distinction between a user who is in-game ("online"), a user who is active
/// on the web/mobile client ("active"), and a user who is offline.
///
/// The profile status is a mode (Active, Join Me, Ask Me, Busy), not proof of
/// connection. The API state and live location decide whether the person is
/// present at all.
/// </summary>
internal static class UserPresence
{
    // VRChat's official status palette, as used by VRCX:
    // online green, join me blue, ask me orange, busy red, offline gray.
    internal const string OnlineColor = "#2ED319";
    internal const string JoinMeColor = "#00B8FF";
    internal const string AskMeColor = "#E97C03";
    internal const string BusyColor = "#C80928";
    internal const string OfflineColor = "#737F8D";

    internal static readonly Brush OnlineBrush = MakeBrush(OnlineColor);
    internal static readonly Brush JoinMeBrush = MakeBrush(JoinMeColor);
    internal static readonly Brush AskMeBrush = MakeBrush(AskMeColor);
    internal static readonly Brush BusyBrush = MakeBrush(BusyColor);
    internal static readonly Brush OfflineBrush = MakeBrush(OfflineColor);

    private static readonly Brush OnlineDotBrush = MakeRingBrush(OnlineColor);
    private static readonly Brush JoinMeDotBrush = MakeRingBrush(JoinMeColor);
    private static readonly Brush AskMeDotBrush = MakeRingBrush(AskMeColor);
    private static readonly Brush BusyDotBrush = MakeRingBrush(BusyColor);

    public static string Normalize(string? status)
        => (status ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// VRCX's presence classification. State is authoritative for the
    /// web/mobile "active" case. Location remains authoritative for in-game
    /// presence, except for the stale private-location case that VRCX resolves
    /// against the current user's online/active friend sets.
    /// </summary>
    public static UserPresenceState Classify(
        string? location,
        string? status,
        string? state = null,
        string? platform = null,
        bool forceOnline = false,
        bool inOnlineFriends = false,
        bool inActiveFriends = false)
    {
        if (forceOnline)
        {
            return UserPresenceState.Online;
        }

        var normalizedState = Normalize(state);
        if (normalizedState == "active")
        {
            return UserPresenceState.Active;
        }

        var normalizedStatus = Normalize(status);
        // An explicit online state is newer than the offline status left on the
        // record by a previous session or a friend-offline event.
        if (normalizedStatus == "offline" && normalizedState != "online")
        {
            return UserPresenceState.Offline;
        }

        var normalizedLocation = (location ?? string.Empty).Trim();
        var isWebPlatform = string.Equals(platform, "web", StringComparison.OrdinalIgnoreCase);

        // A real instance or traveling state means the client is in-game. A
        // stale "offline" state from a profile update must not hide that.
        if (normalizedLocation.StartsWith("traveling", StringComparison.OrdinalIgnoreCase) ||
            normalizedLocation.StartsWith("wrld_", StringComparison.OrdinalIgnoreCase))
        {
            return UserPresenceState.Online;
        }

        if (normalizedState == "offline")
        {
            return UserPresenceState.Offline;
        }

        if (normalizedLocation.Length == 0)
        {
            return isWebPlatform ? UserPresenceState.Active : UserPresenceState.Offline;
        }

        if (string.Equals(normalizedLocation, "offline", StringComparison.OrdinalIgnoreCase))
        {
            return isWebPlatform ? UserPresenceState.Active : UserPresenceState.Offline;
        }

        if (string.Equals(normalizedLocation, "private", StringComparison.OrdinalIgnoreCase) &&
            normalizedState.Length == 0 &&
            normalizedStatus != "active" &&
            !inOnlineFriends)
        {
            // VRChat returns location "private" for hidden/offline states. VRCX
            // resolves this with currentUser.onlineFriends/activeFriends. A user
            // in the online bucket is genuinely in a private instance; one in
            // only the active bucket is on the web/mobile client.
            return inActiveFriends || isWebPlatform
                ? UserPresenceState.Active
                : UserPresenceState.Offline;
        }

        if (string.Equals(normalizedLocation, "web", StringComparison.OrdinalIgnoreCase))
        {
            return UserPresenceState.Active;
        }

        return UserPresenceState.Online;
    }

    public static bool IsOnline(
        string? location,
        string? status,
        string? state = null,
        string? platform = null,
        bool forceOnline = false,
        bool inOnlineFriends = false,
        bool inActiveFriends = false)
        => Classify(location, status, state, platform, forceOnline, inOnlineFriends, inActiveFriends) ==
           UserPresenceState.Online;

    public static bool IsActive(
        string? location,
        string? status,
        string? state = null,
        string? platform = null,
        bool inOnlineFriends = false,
        bool inActiveFriends = false)
        => Classify(location, status, state, platform, false, inOnlineFriends, inActiveFriends) ==
           UserPresenceState.Active;

    public static bool IsPresent(
        string? location,
        string? status,
        string? state = null,
        string? platform = null,
        bool forceOnline = false,
        bool inOnlineFriends = false,
        bool inActiveFriends = false)
        => Classify(location, status, state, platform, forceOnline, inOnlineFriends, inActiveFriends) !=
           UserPresenceState.Offline;

    public static string Label(string? status, bool isOnline)
        => Label(status, isOnline ? UserPresenceState.Online : UserPresenceState.Offline);

    public static string Label(string? status, UserPresenceState presence)
    {
        if (presence == UserPresenceState.Offline)
        {
            return "Offline";
        }

        var mode = ModeLabel(status);
        return presence == UserPresenceState.Active
            ? mode == "Online" ? "Active" : $"Active ({mode})"
            : mode;
    }

    public static Brush Brush(string? status, bool isOnline)
        => Brush(status, isOnline ? UserPresenceState.Online : UserPresenceState.Offline);

    public static Brush Brush(string? status, UserPresenceState presence)
    {
        if (presence == UserPresenceState.Offline)
        {
            return OfflineBrush;
        }

        return Normalize(status) switch
        {
            "join me" => JoinMeBrush,
            "ask me" => AskMeBrush,
            "busy" => BusyBrush,
            _ => OnlineBrush
        };
    }

    /// <summary>
    /// The compact avatar dot. VRCX draws an active web/mobile user as a ring
    /// instead of a filled dot, so the same status color carries both pieces of
    /// information: the mode and the kind of connection.
    /// </summary>
    public static Brush DotBrush(string? status, UserPresenceState presence)
    {
        if (presence == UserPresenceState.Offline)
        {
            return OfflineBrush;
        }

        if (presence == UserPresenceState.Active)
        {
            return Normalize(status) switch
            {
                "join me" => JoinMeDotBrush,
                "ask me" => AskMeDotBrush,
                "busy" => BusyDotBrush,
                _ => OnlineDotBrush
            };
        }

        return Brush(status, presence);
    }

    private static string ModeLabel(string? status)
    {
        return Normalize(status) switch
        {
            "active" => "Online",
            "join me" => "Join Me",
            "ask me" => "Ask Me",
            "busy" => "Busy",
            _ => "Online"
        };
    }

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static Brush MakeRingBrush(string color)
    {
        var ring = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        ring.Freeze();

        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawEllipse(null, new Pen(ring, 2.2), new Point(5, 5), 3.4, 3.4);
        }

        var brush = new DrawingBrush(drawing)
        {
            Stretch = Stretch.Uniform
        };
        brush.Freeze();
        return brush;
    }
}
