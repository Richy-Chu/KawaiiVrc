using System;
using System.Diagnostics;
using System.IO;
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
        AppDomain.CurrentDomain.ProcessExit += (_, _) => DeleteScratch(directory);

        // Runs that were killed or crashed never reached their own cleanup.
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

            DeleteScratch(folder, attempts: 2);
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

    private static void DeleteScratch(string folder, int attempts = 5)
    {
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
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
