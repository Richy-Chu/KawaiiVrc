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

        DeletePath(Scratch, attempts: 10);
        SweepFinishedRuns(RealTemp);

        if (!Directory.Exists(Scratch))
        {
            return;
        }

        // A recursive delete stops at the first file it cannot take and leaves the rest of the
        // folder where it was, so the folder is not only what was held. Take every file that
        // will go now: what remains is what actually kept it, which is what the note has to
        // describe and what the post-run check holds it to.
        DeleteDeletableFiles(Scratch);
        if (!Directory.Exists(Scratch))
        {
            return;
        }

        // Read the folder after the trim, not before the delete: the note names what is here.
        var held = HeldFiles(Scratch, out var unlisted);

        // Chromium's own temp files stay open in the browser process, which lives as long as
        // this host, so no retry here can close them. The folder stays with a note saying what
        // was still held: the next run's sweep takes it, and the check that runs after the
        // suite accepts it only when everything named there is an empty .tmp file.
        CleanupDeferred = true;
        DeferredReason = unlisted is not null
            ? "the folder could not be listed: " + unlisted
            : held.Count == 0
                ? "no file it could name was still held"
                : held.Count + " file(s) still held: " +
                  string.Join(", ", held.Select(file => Path.GetFileName(file.Name)));
        RecordDeferral(Scratch, held, unlisted);
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
    /// The probe asks for a share mode no other handle can hold, so it fails on a held file
    /// whether the other side reads it or has already asked for its delete. It opens for read,
    /// not write: a read-only file is not held, and asking to write it would fail on the
    /// attribute alone and report it as one.
    /// </summary>
    internal static List<(string Name, long Size)> HeldFiles(string folder, out string? unlisted)
    {
        var held = new List<(string Name, long Size)>();
        unlisted = null;

        string[] files;
        try
        {
            files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch (Exception error)
        {
            // A run that could not look is not a run that found nothing: the note has to say
            // which happened, or the check reads it as a deferral that proves nothing.
            unlisted = error.GetType().Name + ": " + error.Message;
            return held;
        }

        foreach (var path in files)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (FileNotFoundException)
            {
                continue;
            }
            catch (DirectoryNotFoundException)
            {
                continue;
            }
            catch (IOException)
            {
                // A sharing violation: another handle is on the file, which is what keeps the
                // folder from being deleted.
                held.Add((Path.GetRelativePath(folder, path), Size(path)));
            }
            catch (UnauthorizedAccessException)
            {
                // Not something this probe can attribute to a lock, so it names nothing: a
                // deferral that cannot explain the folder fails the post-run check instead.
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

    internal static void RecordDeferral(
        string folder, List<(string Name, long Size)> held, string? unlisted)
    {
        try
        {
            var lines = new List<string>
            {
                "host pid: " + OwnerOf(folder),
                "utc: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            if (unlisted is not null)
            {
                // One line: the note is read line by line, and an exception message is not.
                lines.Add("unlisted: " + unlisted.Replace('\r', ' ').Replace('\n', ' '));
            }

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

    /// <summary>
    /// The pid the folder is named for. The note describes that folder, so it names the folder's
    /// own process even when it is written for another run's folder - a test sandbox has to reach
    /// the same self-consistency the post-run check asks for.
    /// </summary>
    private static string OwnerOf(string folder)
    {
        var name = Path.GetFileName(TrimSeparators(folder));
        if (name.StartsWith(ScratchPrefix, StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(
                name.AsSpan(ScratchPrefix.Length),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var owner))
        {
            return owner.ToString(CultureInfo.InvariantCulture);
        }

        return Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
    }

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

    /// <summary>
    /// Deletes every file in <paramref name="folder"/> that will go, then the directories that
    /// end up empty, so what is left is what actually kept it. The note and the post-run check
    /// both judge the folder by what remains, so the two have to be the same thing: a delete that
    /// stopped at one held file would otherwise leave a store file the note never names.
    /// </summary>
    internal static void DeleteDeletableFiles(string folder)
    {
        foreach (var file in Entries(folder, Directory.GetFiles))
        {
            try
            {
                // A read-only attribute is not a lock - the probe reads straight through it - so
                // it is no reason to keep a file, and keeping one would leave the note short.
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
            }
            catch (Exception)
            {
                // Locked, or not this run's to delete: it stays, and the note has to name it.
            }
        }

        foreach (var directory in Entries(folder, Directory.GetDirectories)
                     .OrderByDescending(path => path.Length))
        {
            try
            {
                Directory.Delete(directory);
            }
            catch (Exception)
            {
                // Still holds something: it goes when that file does.
            }
        }

        try
        {
            Directory.Delete(folder);
        }
        catch (Exception)
        {
            // Still holds a file that would not go, which is why the folder is deferred.
        }
    }

    private static string[] Entries(string folder, Func<string, string, SearchOption, string[]> list)
    {
        try
        {
            return list(folder, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return Array.Empty<string>();
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
