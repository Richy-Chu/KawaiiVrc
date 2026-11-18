using System.IO;

namespace VrcCompanion.Services;

/// <summary>
/// Small append-only log for application lifecycle events. It is separate from
/// crash.log so a native/forced termination still leaves the last known state.
/// </summary>
internal static class LifecycleLog
{
    private static readonly object Sync = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "lifecycle.log");

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(
                    LogPath,
                    $"[{DateTimeOffset.Now:O}] pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never affect the app.
        }
    }
}
