using System.Diagnostics;
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

        StartUri($"vrchat://launch?ref=vrchat.com&id={location}", mode);
    }

    /// <summary>Opens VRChat without joining a world.</summary>
    public static void LaunchClient(LaunchMode mode = LaunchMode.Vr)
        => StartUri("vrchat://launch/", mode);

    public static void OpenUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public static bool IsLocation(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.StartsWith("wrld_", StringComparison.Ordinal) &&
           value.Contains(':');

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

        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
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
