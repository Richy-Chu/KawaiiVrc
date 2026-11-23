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
        UserDataPaths.UseDirectoryForTests(directory);

        var logs = Path.Combine(directory, "vrchat-logs");
        Directory.CreateDirectory(logs);
        VrcLogPaths.UseDirectoryForTests(logs);

        // A store still holding a handle must not turn the run's own litter into a leak:
        // retry briefly, then leave what is left to the next run's sweep.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            DeletePath(directory);
            ClearScratch(Path.GetTempPath(), directory, attempts: 5);
        };

        // Runs that were killed or crashed never reached their own cleanup.
        ClearScratch(Path.GetTempPath(), directory, attempts: 2);
    }

    /// <summary>
    /// One pass over <paramref name="temp"/> and over the folders the tests write into:
    /// instance and store folders, media and paging caches. Anything named the way a test
    /// names its scratch is deleted. A per-run folder is only litter once its process is
    /// gone, which its pid decides, so a run this one did not start keeps its own, and
    /// <paramref name="own"/> stays until this run's process exits.
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

        foreach (var root in roots)
        {
            foreach (var entry in Entries(root))
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

            if (GuidInName.IsMatch(name) &&
                ScratchPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                DeletePath(entry, attempts);
            }
        }
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
