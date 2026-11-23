using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace VrcCompanion.Tests;

/// <summary>
/// The source scans only prove that no file builds the real profile path directly;
/// they say nothing about a run that already reached it through reflection, a
/// cached path, or a store added later. This records the real profile - every file
/// with its size and write time - before the first test runs and compares it again
/// when the last one finishes.
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

    /// <summary>
    /// The one place a verdict is reported. The test runner keeps a passing test's output
    /// in its own buffers and drops it, so the project prints this file after VSTest.
    /// Written only for an inconclusive run and deleted at the start of every run, so a
    /// clean run prints nothing and a stale note can never appear.
    /// </summary>
    internal static string NotePath => Path.Combine(
        Path.GetTempPath(),
        "VrcCompanionTests",
        "logs",
        "profile-guard-note.txt");

    /// <summary>
    /// The same record, on disk. A filtered test pass leaves the guard out of its filter, so
    /// the project runs the guard afterwards in a second process, which cannot see the first
    /// one's memory and has to read the profile as the run found it.
    /// </summary>
    internal static string BaselinePath => Path.Combine(
        Path.GetTempPath(),
        "VrcCompanionTests",
        "logs",
        "profile-baseline.txt");

    /// <summary>
    /// Set for the guard-only pass that follows a filtered run: take the recorded baseline
    /// instead of recording a new one, which would be taken after the tests had already run.
    /// </summary>
    internal const string ReplayVariable = "VRCC_PROFILE_GUARD_REPLAY";

    private static ProfileSnapshot? _baseline;
    private static IReadOnlyList<string> _appAtBaseline = Array.Empty<string>();

    internal static bool HasBaseline => _baseline is not null;

    internal static int BaselineFileCount => _baseline?.Files.Count ?? 0;

    /// <summary>
    /// Captures the real profile once, from the module initializer before any seam moves.
    /// </summary>
    internal static void EnsureBaseline()
    {
        if (_baseline is not null)
        {
            return;
        }

        if (TryReplayBaseline())
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

        PersistBaseline();
    }

    private static bool TryReplayBaseline()
    {
        if (Environment.GetEnvironmentVariable(ReplayVariable) != "1" || !File.Exists(BaselinePath))
        {
            return false;
        }

        var (snapshot, apps) = LoadBaselineFile(BaselinePath);
        _appAtBaseline = apps;
        _baseline = snapshot;
        return true;
    }

    private static void PersistBaseline()
    {
        if (_baseline is null)
        {
            return;
        }

        try
        {
            WriteBaselineFile(BaselinePath, _baseline, _appAtBaseline);
        }
        catch (Exception)
        {
            // Only the filtered-run hand-off needs the file; this run still guards itself.
        }
    }

    /// <summary>
    /// One line per entry, prefixed by kind: <c>app|...</c>, <c>f|length|ticks|path</c>,
    /// <c>d|ticks|path</c>. No relative path in a Windows profile holds a pipe.
    /// </summary>
    internal static void WriteBaselineFile(
        string path,
        ProfileSnapshot snapshot,
        IReadOnlyList<string> running)
    {
        var lines = new List<string>(snapshot.Files.Count + snapshot.Directories.Count + running.Count);

        foreach (var app in running)
        {
            lines.Add("app|" + app);
        }

        foreach (var (relative, entry) in snapshot.Files)
        {
            lines.Add("f|" + entry.Length.ToString(CultureInfo.InvariantCulture) +
                      "|" + entry.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture) +
                      "|" + relative);
        }

        foreach (var (relative, entry) in snapshot.Directories)
        {
            lines.Add("d|" + entry.LastWriteUtcTicks.ToString(CultureInfo.InvariantCulture) + "|" + relative);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    internal static (ProfileSnapshot Snapshot, IReadOnlyList<string> Running) LoadBaselineFile(string path)
    {
        var snapshot = new ProfileSnapshot();
        var running = new List<string>();

        foreach (var line in File.ReadLines(path))
        {
            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                continue;
            }

            switch (parts[0])
            {
                case "app":
                    running.Add(string.Join("|", parts, 1, parts.Length - 1));
                    break;

                case "f" when parts.Length >= 4:
                    snapshot.Files[parts[3]] = new ProfileEntry(
                        long.Parse(parts[1], CultureInfo.InvariantCulture),
                        long.Parse(parts[2], CultureInfo.InvariantCulture));
                    break;

                case "d" when parts.Length >= 3:
                    snapshot.Directories[parts[2]] = new ProfileEntry(
                        0,
                        long.Parse(parts[1], CultureInfo.InvariantCulture));
                    break;
            }
        }

        return (snapshot, running);
    }

    /// <summary>
    /// Compares the real profile against the baseline. Returns null when it is unchanged.
    /// When it changed while the installed app was running, the change cannot be pinned on
    /// a test, so the verdict is written for the project to print and the run stays green.
    /// Otherwise it throws: no app process was writing, so the change came from the tests.
    /// </summary>
    internal static string? Evaluate()
    {
        var changes = DescribeChanges(Baseline(), Capture(RealRoot));
        if (changes.Count == 0)
        {
            return null;
        }

        var running = _appAtBaseline
            .Union(RunningAppProcesses(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        var message = Describe(changes, running);

        if (running.Count == 0)
        {
            throw new InvalidOperationException(message);
        }

        WriteNote(message);
        return message;
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
            else if (old != entry)
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
            else if (old != entry && !churnedParents.Contains(path) && Moved(old, entry))
            {
                changes.Add("modified directory " + path);
            }
        }

        changes.Sort(StringComparer.Ordinal);
        return changes;
    }

    /// <summary>
    /// The system reports a directory's write time lazily: a directory written just before
    /// the baseline can still report its older time there and the newer one a moment later,
    /// which would blame the run for nothing. A real write moves the time well past that
    /// window, so anything smaller is not evidence.
    /// </summary>
    private static bool Moved(ProfileEntry before, ProfileEntry after)
        => Math.Abs(after.LastWriteUtcTicks - before.LastWriteUtcTicks) > TimeSpan.FromSeconds(2).Ticks;

    private static ProfileSnapshot Baseline()
        => _baseline ?? throw new InvalidOperationException(
            "The real profile was never recorded before the tests started.");

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
            // The verdict already passed; nothing here may turn it red.
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
