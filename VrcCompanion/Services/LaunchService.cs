using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VrcCompanion.Services;

/// <summary>How the VRChat client should be started.</summary>
public enum LaunchMode
{
    Vr,
    Desktop
}

/// <summary>What actually happened when a location was opened.</summary>
public enum LaunchOutcome
{
    /// <summary>The running VRChat client accepted the launch URL.</summary>
    AttachedToRunningGame,

    /// <summary>VRChat was not running, so the registered launcher was started.</summary>
    GameStarted,

    /// <summary>VRChat is running but its URL pipe did not accept the launch.</summary>
    AttachFailed
}

/// <summary>
/// Hands a location to VRChat. A running client receives it through VRChat's
/// own URL-launch pipe; a closed client is started through its registered URL
/// handler. The app never injects code into the game process.
/// </summary>
public static class LaunchService
{
    internal const string RunningGamePipeName = "VRChatURLLaunchPipe";

    public static void Launch(
        string location,
        LaunchMode mode = LaunchMode.Vr,
        string? shortName = null,
        string? extraArguments = null)
    {
        if (!IsLocation(location))
        {
            throw new ArgumentException("That is not a valid VRChat world location.", nameof(location));
        }

        StartUri(BuildLaunchUri(location, shortName), mode, extraArguments);
    }

    /// <summary>
    /// Joins an instance without starting a second VRChat process. When the game
    /// is already running the plain launch URL is handed to VRChat's URL pipe;
    /// only a closed game is cold-started through the registered handler. This is
    /// the shared path used by every join in the app.
    /// </summary>
    public static async Task<LaunchOutcome> OpenLocationAsync(
        string location,
        LaunchMode mode = LaunchMode.Vr,
        string? shortName = null,
        string? extraArguments = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsLocation(location))
        {
            throw new ArgumentException("That is not a valid VRChat world location.", nameof(location));
        }

        NormalizeExtraArguments(extraArguments);
        var uri = BuildLaunchUri(location, shortName);
        if (GameRunningProbe())
        {
            // VRChat's native pipe takes the same URI as a normal launch link.
            // Do not add attach=1 here: that is a launch.exe hint, and VRChat
            // treats it as a different request (it opens the Worlds menu rather
            // than joining).
            return await RunningGameLauncher(uri, cancellationToken).ConfigureAwait(false)
                ? LaunchOutcome.AttachedToRunningGame
                : LaunchOutcome.AttachFailed;
        }

        StartUri(uri, mode, extraArguments);
        return LaunchOutcome.GameStarted;
    }

    /// <summary>True when a VRChat process is already running on this PC.</summary>
    public static bool IsGameRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("VRChat");
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
        catch (Exception ex)
        {
            Debug.WriteLine($"VRChat process check: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Closes the running VRChat client so a launch URL can cold-start into an
    /// instance. VRChat refuses to deep-link into the instance it is already
    /// in, and shows its Worlds page instead, so re-entering that instance needs
    /// a clean client restart. The client is asked to close first and is only
    /// terminated if it does not exit in time.
    /// </summary>
    public static async Task<bool> CloseGameAsync(
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var processes = Process.GetProcessesByName("VRChat");
        if (processes.Length == 0)
        {
            return true;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.CloseMainWindow();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"close VRChat: {ex}");
                }
            }

            var exitTasks = processes
                .Select(process => process.WaitForExitAsync(cancellationToken))
                .ToArray();
            var allExited = Task.WhenAll(exitTasks);
            var completed = await Task.WhenAny(
                allExited,
                Task.Delay(timeout ?? TimeSpan.FromSeconds(8), cancellationToken))
                .ConfigureAwait(false);
            if (completed != allExited)
            {
                foreach (var process in processes)
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(entireProcessTree: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"stop VRChat: {ex}");
                    }
                }

                await allExited.ConfigureAwait(false);
            }

            return processes.All(process => process.HasExited);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"close VRChat: {ex}");
            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    /// <summary>
    /// VRChat exposes a named pipe for exactly this purpose. The payload is the
    /// plain launch URI; VRChat's client performs the join itself.
    /// </summary>
    internal static async Task<bool> SendToRunningGameAsync(
        string launchUri,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                RunningGamePipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1500, cancellationToken).ConfigureAwait(false);

            var payload = Encoding.UTF8.GetBytes(launchUri);
            await pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);

            var response = new byte[1];
            var read = await pipe.ReadAsync(response, cancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
            return read == 1 && response[0] == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Debug.WriteLine($"VRChat URL pipe: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Builds the launch URI with the location left readable. IsLocation guarantees
    /// the instance charset has no &amp;, ?, #, quotes or whitespace, so the id cannot
    /// rewrite this query; escaping it would only mangle the separators VRChat reads.
    /// </summary>
    internal static string BuildLaunchUri(string location, string? shortName = null)
    {
        var uri = $"vrchat://launch?ref=vrchat.com&id={location}";
        if (!string.IsNullOrWhiteSpace(shortName))
        {
            uri += $"&shortName={Uri.EscapeDataString(shortName.Trim())}";
        }
        return uri;
    }

    /// <summary>Opens VRChat without joining a world.</summary>
    public static void LaunchClient(LaunchMode mode = LaunchMode.Vr, string? extraArguments = null)
        => StartUri("vrchat://launch/", mode, extraArguments);

    /// <summary>
    /// Returns an error for arguments that cannot be safely appended to the
    /// launch command. Valid arguments are intentionally not restricted to a
    /// fixed list: VRChat and Unity add options over time, and this is the
    /// advanced surface. Only command-breaking values are rejected.
    /// </summary>
    public static string? ValidateExtraArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        var text = arguments.Trim();
        if (text.Length > 1024)
        {
            return "Launch arguments are limited to 1024 characters.";
        }

        if (text.Contains('\r') || text.Contains('\n') || text.Contains('\0'))
        {
            return "Launch arguments must stay on one line.";
        }

        if (text.Contains("vrchat://", StringComparison.OrdinalIgnoreCase))
        {
            return "Launch arguments cannot contain another vrchat:// link.";
        }

        if (text.Count(character => character == '"') % 2 != 0)
        {
            return "Launch arguments contain an unclosed quote.";
        }

        return null;
    }

    internal static string NormalizeExtraArguments(string? arguments)
    {
        var error = ValidateExtraArguments(arguments);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(arguments));
        }

        return arguments?.Trim() ?? string.Empty;
    }

    internal static string BuildHandlerArguments(
        string uri,
        LaunchMode mode,
        string? extraArguments)
    {
        var extra = NormalizeExtraArguments(extraArguments);
        var arguments = new StringBuilder($"\"{uri}\"");
        if (mode == LaunchMode.Desktop && !HasNoVrArgument(extra))
        {
            arguments.Append(" --no-vr");
        }

        if (extra.Length > 0)
        {
            arguments.Append(' ').Append(extra);
        }

        return arguments.ToString();
    }

    internal static string BuildShellArguments(LaunchMode mode, string? extraArguments)
    {
        var extra = NormalizeExtraArguments(extraArguments);
        var arguments = new StringBuilder();
        if (mode == LaunchMode.Desktop && !HasNoVrArgument(extra))
        {
            arguments.Append("--no-vr");
        }

        if (extra.Length > 0)
        {
            if (arguments.Length > 0)
            {
                arguments.Append(' ');
            }

            arguments.Append(extra);
        }

        return arguments.ToString();
    }

    private static bool HasNoVrArgument(string arguments)
        => Regex.IsMatch(arguments, @"(^|\s)--no-vr(?=\s|$)", RegexOptions.IgnoreCase);

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

    /// <summary>
    /// Opens Explorer with the downloaded file selected. If the file was moved or
    /// deleted, opens its containing folder instead.
    /// </summary>
    public static bool OpenFolder(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        if (File.Exists(filePath))
        {
            FolderLauncher($"/select,\"{filePath}\"");
            return true;
        }

        var folder = Path.GetDirectoryName(filePath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return false;
        }

        FolderLauncher($"\"{folder}\"");
        return true;
    }

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

    /// <summary>
    /// Private, hidden and non-public group instances need VRChat's short name
    /// to open the exact instance or send a working self-invite.
    /// </summary>
    public static bool NeedsShortName(string? location)
    {
        var instanceId = GetInstanceIdFromLocation(location);
        if (instanceId is null)
        {
            return false;
        }

        return instanceId.Contains("~private(", StringComparison.OrdinalIgnoreCase) ||
               instanceId.Contains("~hidden(", StringComparison.OrdinalIgnoreCase) ||
               (instanceId.Contains("~group(", StringComparison.OrdinalIgnoreCase) &&
                !instanceId.Contains("~groupAccessType(public)", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Where an Explorer target goes. Tests replace it to observe the selection.</summary>
    internal static Action<string> FolderLauncher { get; set; } = DefaultFolderLauncher;

    internal static void DefaultFolderLauncher(string arguments)
        => Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });

    /// <summary>Where a finished launch URI goes. Tests replace it to observe launches.</summary>
    internal static Action<string> UriLauncher { get; set; } = DefaultUriLauncher;

    internal static void DefaultUriLauncher(string uri)
        => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

    /// <summary>
    /// The registered vrchat:// executable. Tests replace it to avoid depending
    /// on the machine's installed VRChat copy.
    /// </summary>
    internal static Func<string?> HandlerPathProvider { get; set; } = FindHandlerPath;

    /// <summary>Where a direct handler launch goes. Tests replace it to observe launches.</summary>
    internal static Action<string, string> HandlerLauncher { get; set; } = DefaultHandlerLauncher;

    /// <summary>Tests replace process detection so they never depend on a running game.</summary>
    internal static Func<bool> GameRunningProbe { get; set; } = IsGameRunning;

    /// <summary>Tests observe the attach path without opening a real named pipe.</summary>
    internal static Func<string, CancellationToken, Task<bool>> RunningGameLauncher { get; set; }
        = SendToRunningGameAsync;

    internal static void DefaultHandlerLauncher(string handler, string arguments)
        => Process.Start(new ProcessStartInfo(handler)
        {
            Arguments = arguments,
            UseShellExecute = false
        });

    private static void StartUri(string uri, LaunchMode mode, string? extraArguments = null)
    {
        // VRChat takes --no-vr to force desktop mode and has no opposite flag.
        // Some installs register the vrchat:// handler with --no-vr baked in
        // (for example "launch.exe" "%1" --no-vr), so invoking the URI through
        // Windows always starts desktop even when VR was picked. Calling the
        // registered executable directly with only the URI bypasses that flag;
        // desktop adds --no-vr explicitly. Saved launch profiles add their
        // arguments after the URI on the same command line.
        var arguments = BuildHandlerArguments(uri, mode, extraArguments);

        if (TryGetHandlerPath(out var handler))
        {
            HandlerLauncher(handler, arguments);
            return;
        }

        var shellArguments = BuildShellArguments(mode, extraArguments);
        if (shellArguments.Length > 0)
        {
            Process.Start(new ProcessStartInfo(uri)
            {
                Arguments = shellArguments,
                UseShellExecute = true
            });
            return;
        }

        UriLauncher(uri);
    }

    private static bool TryGetHandlerPath(out string path)
    {
        path = HandlerPathProvider() ?? string.Empty;
        return path.Length > 0;
    }

    /// <summary>Finds the executable Windows uses for vrchat:// links.</summary>
    internal static string? FindHandlerPath()
    {
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

                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        return null;
    }
}
