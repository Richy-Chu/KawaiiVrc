using System;
using System.Diagnostics;
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
/// </summary>
internal static class TestProfile
{
    private const string ScratchPrefix = "vrc-companion-tests-";

    /// <summary>
    /// Tests write their own scratch under these roots and nothing claimed it back: folders
    /// named for a GUID, media caches named for a GUID and then suffixed. Each root is cleared
    /// before the tests start (a run that crashed never cleaned up) and again when this one
    /// exits. The log folder, the shared settings file and the browser profile are shared
    /// infrastructure under the first root, not scratch, and stay.
    /// </summary>
    private static readonly (string Root, string[] Kept)[] PerTestRoots =
    {
        (Path.Combine(Path.GetTempPath(), "VrcCompanionTests"), new[] { "logs", "shared", "browser" }),
        (Path.Combine(Path.GetTempPath(), "VrcMediaMeta"), Array.Empty<string>())
    };

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
            ClearPerTestScratch(attempts: 5);
        };

        // Runs that were killed or crashed never reached their own cleanup.
        ClearPerTestScratch(attempts: 2);
        DeleteScratchOfFinishedRuns(directory);
    }

    /// <summary>
    /// The folder is named for the process that owns it, so a folder whose process is gone
    /// is litter. A live process keeps its folder - including a run this one did not start -
    /// unless the folder has gone untouched for far longer than any run lasts, which also
    /// retires a folder whose pid Windows has since handed to an unrelated process.
    /// </summary>
    private static void DeleteScratchOfFinishedRuns(string own)
    {
        string[] leftovers;
        try
        {
            leftovers = Directory.GetDirectories(Path.GetTempPath(), ScratchPrefix + "*");
        }
        catch (Exception)
        {
            return;
        }

        foreach (var folder in leftovers)
        {
            if (string.Equals(folder, own, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = Path.GetFileName(folder).Substring(ScratchPrefix.Length);
            if (!int.TryParse(name, out var owner) || (IsRunning(owner) && !IsAbandoned(folder)))
            {
                continue;
            }

            DeletePath(folder, attempts: 2);
        }
    }

    private static void ClearPerTestScratch(int attempts)
    {
        foreach (var (root, kept) in PerTestRoots)
        {
            string[] entries;
            try
            {
                entries = Directory.Exists(root)
                    ? Directory.GetFileSystemEntries(root)
                    : Array.Empty<string>();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                if (!kept.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    DeletePath(entry, attempts);
                }
            }
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
