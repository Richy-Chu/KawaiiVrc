using System;
using System.Diagnostics;
using System.Runtime;

namespace VrcCompanion.Services;

/// <summary>
/// Hands the memory a section left behind back to the system when its page is
/// closed. Dropping the visual tree only makes those objects unreachable; until
/// a collection runs the process keeps the tab's peak, and a heavy tab (Media,
/// with the metadata pass's 256 KB/512 KB buffer per picture and the viewer's
/// full-size decode) leaves a footprint that never came back down.
/// </summary>
internal static class MemorySettle
{
    internal readonly record struct Result(long BeforeBytes, long AfterBytes, long Milliseconds)
    {
        /// <summary>Private bytes the settle took back; never negative.</summary>
        public long FreedBytes => Math.Max(0, BeforeBytes - AfterBytes);
    }

    /// <summary>
    /// Drops <paramref name="dropCaches"/> first, so the images it owns become
    /// garbage, then collects them for real. The count reported is the process's
    /// own private memory, which is what the user sees.
    /// </summary>
    public static Result Run(Action? dropCaches = null)
    {
        dropCaches?.Invoke();

        var before = PrivateBytes();
        var watch = Stopwatch.StartNew();

        // Two passes: WPF bitmaps and the file buffers are finalized, so the
        // first pass frees their native memory and the second collects what the
        // finalizers released.
        Collect();
        GC.WaitForPendingFinalizers();
        Collect();

        watch.Stop();
        return new Result(before, PrivateBytes(), watch.ElapsedMilliseconds);
    }

    private static void Collect()
    {
        // Aggressive, not Forced: a forced pass collects the garbage but keeps
        // the emptied segments committed for reuse, which measured 307 MB
        // committed against a 55 MB heap - the tab's peak stayed in Task Manager
        // even though nothing was live. Aggressive compacts and decommits as
        // well, and gave back 267 MB of 268 MB of dropped buffers in one pass.
        // The large object heap holds the metadata buffers and every full-size
        // decode, so it is compacted explicitly as well.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static long PrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PrivateMemorySize64;
    }
}
