using System;
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
        DeletePath(Scratch, attempts: 10);
        SweepFinishedRuns(RealTemp);

        // Some files stay held by a child process - a rendered page keeps the temp files it
        // was given - and no amount of waiting in this process frees them. A helper waits for
        // this process to end and deletes what is left, which is the same end state the next
        // run's sweep would reach, only now instead of later.
        if (Directory.Exists(Scratch))
        {
            CleanupDeferred = true;
            ReapAfterExit(Scratch);
        }
    }

    /// <summary>Whether the last cleanup handed its folder to the reaper instead of deleting it.</summary>
    internal static bool CleanupDeferred { get; private set; }

    private static void ReapAfterExit(string folder)
    {
        var script = "$host = " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "; " +
                     "try { Wait-Process -Id $host -Timeout 120 -ErrorAction Stop } catch { }; " +
                     "Remove-Item -LiteralPath '" + folder.Replace("'", "''") + "' -Recurse -Force -ErrorAction SilentlyContinue";

        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch (Exception)
        {
            // The next run's sweep takes the folder anyway; never fail the run over this.
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
