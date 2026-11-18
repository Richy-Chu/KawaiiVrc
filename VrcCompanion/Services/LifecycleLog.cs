using System.IO;

namespace VrcCompanion.Services;

/// <summary>
/// Small append-only log for application lifecycle events. It is separate from
/// crash.log so a native/forced termination still leaves the last known state.
/// </summary>
internal static class LifecycleLog
{
    private static readonly object Sync = new();
    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion",
        "lifecycle.log");

    private static string? _path;

    /// <summary>
    /// Sends later entries to <paramref name="path"/> instead of the user's log. The
    /// test assembly points this at a temp file so test runs stay out of the real one.
    /// </summary>
    public static void RedirectTo(string path)
    {
        lock (Sync)
        {
            _path = path;
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                var path = _path ?? DefaultPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(
                    path,
                    $"[{DateTimeOffset.Now:O}] pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never affect the app.
        }
    }
}
