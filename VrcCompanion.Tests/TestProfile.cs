using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using VrcCompanion.Services;

namespace VrcCompanion.Tests;

/// <summary>
/// Runs before any test. A default <see cref="SessionStore"/> must never point
/// at the user's profile: a test that reaches a 401 path would otherwise clear
/// the real saved session and sign the user out.
/// The run's own scratch gets the same treatment: everything it writes to %TEMP% is removed
/// before the tests start, for runs that died, and again when this one exits.
/// </summary>
internal static class TestProfile
{
    private const string ScratchPrefix = "vrc-companion-tests-";

    /// <summary>
    /// Set to 1 to keep this run's scratch when it exits, for a failure whose files need
    /// looking at: the store folders, media caches and %TEMP% files stay where the tests left
    /// them, and the run's own folder stays under %TEMP%\vrc-companion-tests-&lt;pid&gt;. The
    /// next run sweeps at startup, so nothing is kept for good.
    /// </summary>
    private static readonly bool KeepScratch =
        Environment.GetEnvironmentVariable("VRCC_KEEP_TEST_SCRATCH") == "1";

    /// <summary>
    /// Under the scratch roots but not scratch: the log folder the guard's verdict is read
    /// from after the run, and the WebView2 profile.
    /// </summary>
    private static readonly HashSet<string> Kept =
        new(StringComparer.OrdinalIgnoreCase) { "logs", "shared", "browser" };

    /// <summary>
    /// Tests also drop single files straight into %TEMP%, named for what they test: store
    /// databases, chatbox settings, cosmetic gifs.
    /// </summary>
    private static readonly string[] ScratchPrefixes =
    {
        "vrccompanion-",
        "vrc-"
    };

    /// <summary>
    /// Every test names its file after the GUID it made for it, so a name without one only
    /// starts like a test's and belongs to something else: another tool's vrc-*.log stays.
    /// </summary>
    private static readonly Regex GuidInName = new(
        "[0-9a-fA-F]{32}|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    [ModuleInitializer]
    internal static void Initialize()
    {
        // First, before any seam moves: the real profile in the state the run found it.
        RealProfileWatch.EnsureBaseline();

        var directory = Path.Combine(
            Path.GetTempPath(),
            ScratchPrefix + Environment.ProcessId);
        _scratch = directory;
        UserDataPaths.UseDirectoryForTests(directory);

        var logs = Path.Combine(directory, "vrchat-logs");
        Directory.CreateDirectory(logs);
        VrcLogPaths.UseDirectoryForTests(logs);

        // A backstop only: the suite calls CleanUp itself from its last collection, because a
        // test host that reports results need not reach its exit handlers. One CI run passed
        // every test and left its whole scratch behind, so nothing may depend on this running.
        if (!KeepScratch)
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanUp();
        }

        // Runs that were killed or crashed never reached their own cleanup.
        ClearScratch(Path.GetTempPath(), directory, attempts: 2);
    }

    /// <summary>
    /// Deletes this run's scratch while the run is still alive, from the suite's last
    /// collection. The exit handler calls it again on the way out; by then there is nothing
    /// left to delete. A deliberate keep skips both.
    /// </summary>
    internal static void CleanUp()
    {
        var directory = _scratch;
        if (KeepScratch || directory is null)
        {
            return;
        }

        // A store still holding a handle must not turn the run's own litter into a leak:
        // retry briefly, then leave what is left to the next run's sweep.
        DeletePath(directory, attempts: 10);
        ClearScratch(Path.GetTempPath(), directory, attempts: 5);
    }

    /// <summary>This run's folder, or null before the module initializer has run.</summary>
    internal static string? Scratch => _scratch;

    /// <summary>Whether this run keeps its scratch on purpose (<c>VRCC_KEEP_TEST_SCRATCH=1</c>).</summary>
    internal static bool KeepsScratch => KeepScratch;

    private static string? _scratch;

    /// <summary>
    /// One pass over <paramref name="temp"/> and over the folders the tests write into:
    /// instance and store folders, media and paging caches. Anything named the way a test
    /// names its scratch is deleted. A per-run folder is only litter once its process is
    /// gone, which its pid decides, so a run this one did not start keeps its own, and
    /// <paramref name="own"/> stays until this run's process exits.
    /// A live second run's loose files and cache folders look exactly like litter, so those
    /// are only swept when no other run is running: sweeping them mid-run deletes files a
    /// test is still using. Its per-run folder is judged by its pid either way.
    /// The folders are passed in rather than read from %TEMP% so a test can sweep a sandbox.
    /// </summary>
    internal static void ClearScratch(string temp, string own, int attempts)
    {
        var tests = Path.Combine(temp, "VrcCompanionTests");
        var roots = new[]
        {
            tests,
            Path.Combine(tests, "shared"),
            Path.Combine(temp, "VrcMediaMeta"),
            Path.Combine(temp, "VrcCompanion.Tests"),
            Path.Combine(temp, "VrcMediaTests")
        };

        var totalSweep = !AnotherRunIsRunning(temp, own);

        foreach (var root in roots)
        {
            foreach (var entry in totalSweep ? Entries(root) : Array.Empty<string>())
            {
                if (!Kept.Contains(Path.GetFileName(entry)))
                {
                    DeletePath(entry, attempts);
                }
            }
        }

        foreach (var entry in Entries(temp))
        {
            var name = Path.GetFileName(entry);

            if (name.StartsWith(ScratchPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(name, Path.GetFileName(own), StringComparison.OrdinalIgnoreCase))
                {
                    DeleteFinishedRun(entry);
                }

                continue;
            }

            if (totalSweep &&
                GuidInName.IsMatch(name) &&
                ScratchPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                DeletePath(entry, attempts);
            }
        }
    }

    /// <summary>
    /// Whether a run other than this one is still going. Its files cannot be told apart from
    /// a crashed run's, so the sweep leaves them all to the next run that runs alone.
    /// </summary>
    private static bool AnotherRunIsRunning(string temp, string own)
    {
        foreach (var entry in Entries(temp))
        {
            var name = Path.GetFileName(entry);
            if (!name.StartsWith(ScratchPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, Path.GetFileName(own), StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(name.Substring(ScratchPrefix.Length), out var owner))
            {
                continue;
            }

            if (IsRunning(owner) && !IsAbandoned(entry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The folder is named for the process that owns it, so a folder whose process is gone
    /// is litter. A live process keeps its folder - including a run this one did not start -
    /// unless the folder has gone untouched for far longer than any run lasts, which also
    /// retires a folder whose pid Windows has since handed to an unrelated process.
    /// </summary>
    private static void DeleteFinishedRun(string folder)
    {
        var name = Path.GetFileName(folder).Substring(ScratchPrefix.Length);
        if (!int.TryParse(name, out var owner) || (IsRunning(owner) && !IsAbandoned(folder)))
        {
            return;
        }

        DeletePath(folder, attempts: 2);
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

    private static void DeletePath(string path, int attempts = 5)
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
                Thread.Sleep(100);
            }
        }
    }
}
