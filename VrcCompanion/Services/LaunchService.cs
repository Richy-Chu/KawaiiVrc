using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VrcCompanion.Services;

/// <summary>How the VRChat client should be started.</summary>
public enum LaunchMode
{
    Vr,
    Desktop
}

/// <summary>
/// Hands a location to the installed VRChat client through its URL scheme.
/// The app never injects into the game process; Windows does the launching.
/// </summary>
public static class LaunchService
{
    public static void Launch(string location, LaunchMode mode = LaunchMode.Vr)
    {
        if (!IsLocation(location))
        {
            throw new ArgumentException("That is not a valid VRChat world location.", nameof(location));
        }

        StartUri(BuildLaunchUri(location), mode);
    }

    /// <summary>
    /// Builds the launch URI with the location left readable. IsLocation guarantees
    /// the instance charset has no &amp;, ?, #, quotes or whitespace, so the id cannot
    /// rewrite this query; escaping it would only mangle the separators VRChat reads.
    /// </summary>
    internal static string BuildLaunchUri(string location)
        => $"vrchat://launch?ref=vrchat.com&id={location}";

    /// <summary>Opens VRChat without joining a world.</summary>
    public static void LaunchClient(LaunchMode mode = LaunchMode.Vr)
        => StartUri("vrchat://launch/", mode);

    /// <summary>
    /// Opens a web link. Only http(s) is allowed: these strings come from VRChat
    /// data, and ShellExecute would run file:, ms-* or any registered handler.
    /// Returns false, without launching anything, for any other scheme.
    /// </summary>
    public static bool OpenUrl(string? url)
    {
        if (!IsWebUrl(url))
        {
            return false;
        }

        Process.Start(new ProcessStartInfo(url!) { UseShellExecute = true });
        return true;
    }

    public static bool IsWebUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
           (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    // wrld_ plus a UUID, then an instance id made of the characters VRChat uses
    // (numbers, ~region(us), ~private(usr_…)) and nothing that could rewrite the
    // launch query or escape into another argument.
    private static readonly Regex WorldIdPattern = new(
        "^wrld_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex InstancePattern = new(
        "^[A-Za-z0-9~()_.-]{1,256}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var separator = value.IndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
        {
            return false;
        }

        return WorldIdPattern.IsMatch(value[..separator]) &&
               InstancePattern.IsMatch(value[(separator + 1)..]);
    }

    public static string? GetWorldIdFromLocation(string? location)
    {
        if (!IsLocation(location))
        {
            return null;
        }

        var separator = location!.IndexOf(':');
        return separator > 0 ? location[..separator] : null;
    }

    public static string? GetInstanceIdFromLocation(string? location)
    {
        if (!IsLocation(location))
        {
            return null;
        }

        var separator = location!.IndexOf(':');
        return separator > 0 && separator + 1 < location.Length ? location[(separator + 1)..] : null;
    }

    /// <summary>Where a finished launch URI goes. Tests replace it to observe launches.</summary>
    internal static Action<string> UriLauncher { get; set; } = DefaultUriLauncher;

    internal static void DefaultUriLauncher(string uri)
        => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    private static void StartUri(string uri, LaunchMode mode)
    {
        // VRChat takes --no-vr to force desktop mode. Windows hands the vrchat://
        // URI to launch.exe, so the option has to travel with the same arguments.
        if (mode == LaunchMode.Desktop)
        {
            if (TryGetHandlerPath(out var handler))
            {
                Process.Start(new ProcessStartInfo(handler)
                {
                    Arguments = $"\"{uri}\" --no-vr",
                    UseShellExecute = false
                });
                return;
            }

            Process.Start(new ProcessStartInfo(uri)
            {
                Arguments = "--no-vr",
                UseShellExecute = true
            });
            return;
        }

        UriLauncher(uri);
    }

    /// <summary>Finds the executable Windows uses for vrchat:// links.</summary>
    private static bool TryGetHandlerPath(out string path)
    {
        path = string.Empty;

        foreach (var root in new[] { @"HKEY_CLASSES_ROOT\vrchat\shell\open\command", @"HKEY_CURRENT_USER\Software\Classes\vrchat\shell\open\command" })
        {
            try
            {
                if (Registry.GetValue(root, string.Empty, null) is not string command)
                {
                    continue;
                }

                command = command.Trim();
                var candidate = command.StartsWith('"')
                    ? command[1..command.IndexOf('"', 1)]
                    : command.Split(' ')[0];

                if (!string.IsNullOrWhiteSpace(candidate) && System.IO.File.Exists(candidate))
                {
                    path = candidate;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        return false;
    }
}
