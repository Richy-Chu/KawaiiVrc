using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace VrcCompanion.Tests;

/// <summary>
/// The source scans only prove that no file builds the real profile path directly;
/// they say nothing about a run that already reached it through reflection, a
/// cached path, or a store added later. This records the real profile - every file
/// with its size and write time - before the first test runs and compares it again
/// when the last one finishes, so a run that touches real data fails at the end
/// instead of quietly editing it.
/// </summary>
internal static class RealProfileWatch
{
    /// <summary>
    /// Built from LocalApplicationData directly, never from the seams: the point is
    /// to look at the folder the installed app uses, whether or not a test redirected it.
    /// </summary>
    internal static readonly string RealRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion");

    internal static string LogPath => Path.Combine(
        Path.GetTempPath(),
        "VrcCompanionTests",
        "logs",
        "profile-guard.log");

    /// <summary>
    /// Where an inconclusive verdict is left for the test project to print. The test
    /// runner keeps a passing test's output in its own buffers and drops it, so MSBuild
    /// echoes this file after the VSTest target instead, where a plain `dotnet test` run
    /// shows it. Cleared at the start of every run so a stale note cannot print.
    /// </summary>
    internal static string NotePath => Path.Combine(
        Path.GetTempPath(),
        "VrcCompanionTests",
        "logs",
        "profile-guard-note.txt");

    private static readonly object Gate = new();
    private static ProfileSnapshot? _baseline;
    private static IReadOnlyList<string> _appAtBaseline = Array.Empty<string>();

    internal static bool HasBaseline
    {
        get
        {
            lock (Gate)
            {
                return _baseline is not null;
            }
        }
    }

    internal static int BaselineFileCount
    {
        get
        {
            lock (Gate)
            {
                return _baseline?.Files.Count ?? 0;
            }
        }
    }

    /// <summary>
    /// Captures the real profile once. Later calls keep the first snapshot, so the
    /// module initializer and the guard test can both ask for it safely.
    /// </summary>
    internal static void EnsureBaseline()
    {
        lock (Gate)
        {
            if (_baseline is not null)
            {
                return;
            }

            _appAtBaseline = RunningAppProcesses();
            _baseline = Capture(RealRoot);

            try
            {
                File.Delete(NotePath);
            }
            catch (Exception)
            {
                // A stale note only prints an extra line; never fail the baseline for it.
            }
        }
    }

    internal static IReadOnlyList<string> ChangesSinceBaseline()
    {
        ProfileSnapshot baseline;
        lock (Gate)
        {
            baseline = _baseline ?? throw new InvalidOperationException(
                "The real profile was never recorded before the tests started.");
        }

        return DescribeChanges(baseline, Capture(RealRoot));
    }

    /// <summary>
    /// Compares the real profile against the baseline taken before the first test.
    /// Returns null when it is unchanged. When it changed while the installed app was
    /// running, the caller gets a note instead: the change cannot be attributed to a
    /// test, so the run reports inconclusive rather than failing on the app's own writes.
    /// Otherwise it throws, which fails the run - no app process was writing, so the
    /// change came from the tests.
    /// </summary>
    internal static string? Evaluate()
    {
        var changes = ChangesSinceBaseline();
        if (changes.Count == 0)
        {
            return null;
        }

        var running = new List<string>(_appAtBaseline);
        foreach (var process in RunningAppProcesses())
        {
            if (!running.Contains(process))
            {
                running.Add(process);
            }
        }

        var inconclusive = running.Count > 0;
        var message = Describe(changes, running);
        Record((inconclusive ? "INCONCLUSIVE " : "FAILED ") + message);

        if (inconclusive)
        {
            WriteNote(message);
            return message;
        }

        throw new InvalidOperationException(message);
    }

    internal static ProfileSnapshot Capture(string root)
    {
        var snapshot = new ProfileSnapshot();
        var directory = new DirectoryInfo(root);
        if (!directory.Exists)
        {
            return snapshot;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,

            // Hidden and system files are part of the profile (the WebView2 folder is
            // full of them), but a reparse point is skipped so the walk can neither
            // loop nor leave the profile through a link.
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            var relative = Path.GetRelativePath(root, entry.FullName);
            if (entry is FileInfo file)
            {
                snapshot.Files[relative] = new ProfileEntry(file.Length, file.LastWriteTimeUtc.Ticks);
            }
            else
            {
                snapshot.Directories[relative] = new ProfileEntry(0, entry.LastWriteTimeUtc.Ticks);
            }
        }

        return snapshot;
    }

    internal static IReadOnlyList<string> DescribeChanges(ProfileSnapshot before, ProfileSnapshot after)
    {
        var changes = new List<string>();
        var churnedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void RecordFileChange(string path, string kind)
        {
            changes.Add(kind + " file " + path);

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                churnedParents.Add(parent);
            }
        }

        foreach (var (path, entry) in after.Files)
        {
            if (!before.Files.TryGetValue(path, out var old))
            {
                RecordFileChange(path, "added");
            }
            else if (old.Length != entry.Length || old.LastWriteUtcTicks != entry.LastWriteUtcTicks)
            {
                changes.Add("modified file " + path);
            }
        }

        foreach (var path in before.Files.Keys)
        {
            if (!after.Files.ContainsKey(path))
            {
                RecordFileChange(path, "removed");
            }
        }

        foreach (var path in before.Directories.Keys)
        {
            if (!after.Directories.ContainsKey(path))
            {
                changes.Add("removed directory " + path);
            }
        }

        // A directory's write time also moves when a file is created and deleted inside
        // it, which no file diff would show. Report that only when nothing else already
        // explains the change, so one write does not produce two lines.
        foreach (var (path, entry) in after.Directories)
        {
            if (!before.Directories.TryGetValue(path, out var old))
            {
                changes.Add("added directory " + path);
            }
            else if (old.LastWriteUtcTicks != entry.LastWriteUtcTicks && !churnedParents.Contains(path))
            {
                changes.Add("modified directory " + path);
            }
        }

        changes.Sort(StringComparer.Ordinal);
        return changes;
    }

    private static string Describe(IReadOnlyList<string> changes, IReadOnlyList<string> running)
    {
        const int shown = 25;

        var lines = changes.Take(shown).Select(change => "  " + change).ToList();
        if (changes.Count > shown)
        {
            lines.Add("  ... and " + (changes.Count - shown) + " more");
        }

        var verdict = running.Count == 0
            ? "No KawaiiVrc process was running during the run, so every change above came from the tests:"
            : "This run is inconclusive. The installed app was running (" + string.Join(", ", running) + ") and it" +
              Environment.NewLine +
              "writes these same files, so a change cannot be pinned on a test. Close it and run the suite" +
              Environment.NewLine +
              "again to get a real answer.";

        return "The real VrcCompanion profile at " + RealRoot + " changed during the test run." +
               Environment.NewLine +
               "Tests must persist through UserDataPaths (and read VRChat logs through VrcLogPaths) so they" +
               Environment.NewLine +
               "never reach this folder." +
               Environment.NewLine +
               verdict +
               Environment.NewLine +
               changes.Count + " change(s):" +
               Environment.NewLine +
               string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<string> RunningAppProcesses()
    {
        var running = new List<string>();

        foreach (var name in new[] { "KawaiiVrc", "VrcCompanion" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(name);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                using (process)
                {
                    running.Add(name + " (pid " + process.Id + ")");
                }
            }
        }

        return running;
    }

    private static void WriteNote(string message)
    {
        var lines = new[]
        {
            "Real-profile guard INCONCLUSIVE: this run cannot tell a test write from the app's own."
        }.Concat(message.Split(Environment.NewLine));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(NotePath)!);
            File.WriteAllLines(NotePath, lines);
        }
        catch (Exception)
        {
            // The verdict itself already passed; the log still has the details.
        }
    }

    private static void Record(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(
                LogPath,
                Environment.NewLine + "[" + DateTime.UtcNow.ToString("O") + "] " + message + Environment.NewLine);
        }
        catch (Exception)
        {
            // The failure the caller raises is what matters; a log we cannot write must
            // not replace it.
        }
    }
}

internal sealed class ProfileSnapshot
{
    internal Dictionary<string, ProfileEntry> Files { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    internal Dictionary<string, ProfileEntry> Directories { get; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal readonly record struct ProfileEntry(long Length, long LastWriteUtcTicks);
