using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using VrcCompanion.Services;

namespace VrcCompanion.Tests;

/// <summary>
/// Runs before any test. A default <see cref="SessionStore"/> must never point
/// at the user's profile: a test that reaches a 401 path would otherwise clear
/// the real saved session and sign the user out.
/// It also gives the run a folder of its own and points the process's temp at it, so every
/// file a test or the app writes through <see cref="Path.GetTempPath"/> lands in that folder
/// and the sweep only ever has run folders to reason about.
/// </summary>
internal static class TestProfile
{
    private const string ScratchPrefix = "vrc-companion-tests-";

    /// <summary>
    /// The WebView2 profile cannot live in the run's folder: its browser process holds the
    /// profile open until the host exits, so a run that used the browser could never delete
    /// its own folder. It gets a folder of its own, named for the same pid and swept by the
    /// same rule once that process is gone.
    /// </summary>
    internal const string BrowserPrefix = "vrc-companion-browser-";

    /// <summary>
    /// Set to 1 to keep this run's scratch when it exits, for a failure whose files need
    /// looking at: everything the run wrote in %TEMP% stays where it is, under the run's own
    /// folder. The next run sweeps it at startup, so nothing is kept for good.
    /// </summary>
    private static readonly bool KeepScratch =
        Environment.GetEnvironmentVariable("VRCC_KEEP_TEST_SCRATCH") == "1";

    /// <summary>
    /// The system's temp folder, read once before the redirect in the static constructor.
    /// The profile guard's files stay here because MSBuild reads them after the run, in a
    /// process this redirect never reaches.
    /// </summary>
    internal static readonly string RealTemp = TrimSeparators(Path.GetTempPath());

    /// <summary>
    /// The folder every temp write of this process resolves to, so a second suite running at
    /// the same time writes elsewhere entirely. Named for the process that owns it: a folder
    /// whose process is gone is the only litter the sweep has to find.
    /// </summary>
    internal static readonly string Scratch = Path.Combine(
        RealTemp,
        ScratchPrefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    /// <summary>The WebView2 profile folder for this run; see <see cref="BrowserPrefix"/>.</summary>
    internal static readonly string BrowserScratch = Path.Combine(
        RealTemp,
        BrowserPrefix + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    private static readonly string[] ScratchFolders = { ScratchPrefix, BrowserPrefix };

    static TestProfile()
    {
        // Path.GetTempPath reads TMP then TEMP on every call, so this one assignment moves
        // every temp write in the process - the tests', the stores', WebView2's - into the
        // run's own folder. Nothing else has to know where it is.
        Directory.CreateDirectory(Scratch);
        Environment.SetEnvironmentVariable("TMP", Scratch);
        Environment.SetEnvironmentVariable("TEMP", Scratch);

        // A backstop only: the suite calls CleanUp itself from its last collection, because a
        // test host that reports results need not reach its exit handlers. One CI run passed
        // every test and left its whole scratch behind, so nothing may depend on this running.
        if (!KeepScratch)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanUp();
        }
    }

    [ModuleInitializer]
    internal static void Initialize()
    {
        // First, before any seam moves: the real profile in the state the run found it.
        RealProfileWatch.EnsureBaseline();

        UserDataPaths.UseDirectoryForTests(Scratch);

        var logs = Path.Combine(Scratch, "vrchat-logs");
        Directory.CreateDirectory(logs);
        VrcLogPaths.UseDirectoryForTests(logs);

        // Runs that were killed or crashed never reached their own cleanup, and a browser
        // profile from an earlier run was still locked when that run ended.
        SweepFinishedRuns(RealTemp);
    }

    /// <summary>
    /// Deletes this run's scratch while the run is still alive, from the suite's last
    /// collection. The exit handler calls it again on the way out; by then there is nothing
    /// left to delete. A deliberate keep skips both.
    /// </summary>
    internal static void CleanUp()
    {
        if (KeepScratch)
        {
            return;
        }

        // A store or enumerator still holding a file keeps the run's folder undeletable, so let
        // the finalizers that close those handles run, then retry briefly.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        CleanupDeferred = false;
        DeferredReason = null;

        // What another process still holds open is what keeps the folder after the delete, so
        // read it first: the note has to say exactly what was in the way.
        var held = HeldFiles(Scratch);
        DeletePath(Scratch, attempts: 10);
        SweepFinishedRuns(RealTemp);

        if (!Directory.Exists(Scratch))
        {
            return;
        }

        // Chromium's own temp files stay open in the browser process, which lives as long as
        // this host, so no retry here can close them. The folder stays with a note saying what
        // was still held: the next run's sweep takes it, and the check that runs after the
        // suite accepts it only when everything named there is an empty .tmp file.
        CleanupDeferred = true;
        DeferredReason = held.Count == 0
            ? "no file it could name was still held"
            : held.Count + " file(s) still held: " +
              string.Join(", ", held.Select(file => Path.GetFileName(file.Name)));
        RecordDeferral(Scratch, held);
    }

    /// <summary>Whether the run had to leave its folder for the next run's sweep.</summary>
    internal static bool CleanupDeferred { get; private set; }

    /// <summary>
    /// What the folder still held when the cleanup gave up, or null when it deleted the folder.
    /// </summary>
    internal static string? DeferredReason { get; private set; }

    /// <summary>
    /// The note a deferred folder carries. The post-run check lets a leftover folder stand only
    /// when this file names that folder's own pid; a folder without it is a run whose cleanup
    /// never ran, which is what the check is there to catch.
    /// </summary>
    internal const string DeferralRecord = "cleanup-deferred.txt";

    /// <summary>
    /// The files another process still holds open - what stops the folder from being deleted.
    /// An exclusive open fails on such a file whether the other side reads it or has already
    /// asked for its delete, and the size is read now, while it can still be read.
    /// </summary>
    private static List<(string Name, long Size)> HeldFiles(string folder)
    {
        var held = new List<(string Name, long Size)>();
        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return held;
        }

        foreach (var path in files)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception)
            {
                held.Add((Path.GetRelativePath(folder, path), Size(path)));
            }
        }

        return held;
    }

    private static long Size(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            // The check refuses a size it cannot read, so an unreadable one must not look empty.
            return -1;
        }
    }

    private static void RecordDeferral(string folder, List<(string Name, long Size)> held)
    {
        try
        {
            var lines = new List<string>
            {
                "host pid: " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "utc: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            foreach (var file in held)
            {
                lines.Add("held: " + file.Name + " " + file.Size.ToString(CultureInfo.InvariantCulture));
            }

            File.WriteAllLines(Path.Combine(folder, DeferralRecord), lines);
        }
        catch (Exception)
        {
            // No note means the post-run check fails the run, which is the right verdict: a
            // folder the run cannot even describe should not pass as a deferral.
        }
    }

    /// <summary>
    /// Deletes a run's folders under <paramref name="temp"/> once that run's process is gone:
    /// the folder everything it wrote lived in, and the browser profile it could not delete
    /// itself. Nothing else is touched, and nothing here has to tell one run's files from
    /// another's, so two suites at once never reach into each other.
    /// The folders are passed in rather than read from %TEMP% so a test can sweep a sandbox.
    /// </summary>
    internal static void SweepFinishedRuns(string temp)
    {
        var own = Environment.ProcessId;

        foreach (var entry in Entries(temp))
        {
            var name = Path.GetFileName(entry);
            var prefix = ScratchFolders.FirstOrDefault(
                candidate => name.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));

            if (prefix is null ||
                !int.TryParse(name.AsSpan(prefix.Length), out var owner) ||
                owner == own)
            {
                continue;
            }

            // A live process keeps its folder - including a run this one did not start - unless
            // the folder has gone untouched for far longer than any run lasts, which also
            // retires a folder whose pid Windows has since handed to an unrelated process.
            if (IsRunning(owner) && !IsAbandoned(entry))
            {
                continue;
            }

            DeletePath(entry, attempts: 2);
        }
    }

    /// <summary>Whether this run keeps its scratch on purpose (<c>VRCC_KEEP_TEST_SCRATCH=1</c>).</summary>
    internal static bool KeepsScratch => KeepScratch;

    private static string TrimSeparators(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string[] Entries(string folder)
    {
        try
        {
            return Directory.GetFileSystemEntries(folder);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }


    private static bool IsAbandoned(string folder)
    {
        try
        {
            return DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > TimeSpan.FromHours(1);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            // No such process, or one this run may not inspect: either way not ours to wait on.
            return false;
        }
    }

    private static void DeletePath(string path, int attempts = 5, int delayMilliseconds = 100)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else
                {
                    File.Delete(path);
                }

                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception)
            {
                Thread.Sleep(delayMilliseconds);
            }
        }
    }
}
