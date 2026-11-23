using System;
using System.IO;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The sweep runs from the module initializer and nowhere else, so nothing in the suite would
/// notice it widening: a change that drops the GUID rule would only show up as another tool's
/// file disappearing from %TEMP%, and one that ignores a second run deletes files that run's
/// tests are still using. The run's own folder needs no plant here - losing that skip deletes
/// the folder the run just made, which the store and log tests fail on at once.
/// </summary>
public sealed class TestScratchTests
{
    /// <summary>No process can own this id, so a folder named for it is litter.</summary>
    private const int DeadPid = 2147483000;

    [Fact]
    public void TheSweepTakesTheRunsLitterAndLeavesTheRest()
    {
        var temp = Path.Combine(Path.GetTempPath(), "vrc-sweep-" + Guid.NewGuid().ToString("N"));
        var guidFile = Path.Combine(temp, "vrc-cosmetic-" + Guid.NewGuid().ToString("N") + ".gif");
        var decoy = Path.Combine(temp, "vrc-other-tool.log");
        var finished = Path.Combine(temp, "vrc-companion-tests-" + DeadPid);

        Directory.CreateDirectory(finished);
        File.WriteAllText(guidFile, "gif");
        File.WriteAllText(decoy, "log");

        try
        {
            // No other run folder here, so this run is alone and sweeps everything.
            TestProfile.ClearScratch(temp, temp, attempts: 1);

            Assert.False(File.Exists(guidFile), "A file the test named after its GUID is the run's litter.");
            Assert.True(File.Exists(decoy), "A name without a GUID belongs to whatever else wrote it.");
            Assert.False(Directory.Exists(finished), "A per-run folder whose process is gone is litter.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    /// <summary>
    /// Two suites running at once write the same kinds of names, so a sweep cannot tell the
    /// other run's files from a crashed one's. Deleting them breaks that run's tests; the
    /// folder it owns stays by pid, and its loose files and cache entries wait for a run that
    /// sweeps alone.
    /// </summary>
    [Fact]
    public void TheSweepLeavesALiveRunsScratchAlone()
    {
        var temp = Path.Combine(Path.GetTempPath(), "vrc-sweep-" + Guid.NewGuid().ToString("N"));
        var live = Path.Combine(temp, "vrc-companion-tests-" + Environment.ProcessId);
        var finished = Path.Combine(temp, "vrc-companion-tests-" + DeadPid);
        var guidFile = Path.Combine(temp, "vrc-cosmetic-" + Guid.NewGuid().ToString("N") + ".gif");
        var cacheRoot = Path.Combine(temp, "VrcMediaMeta");
        var cacheEntry = Path.Combine(cacheRoot, Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(live);
        Directory.CreateDirectory(finished);
        Directory.CreateDirectory(cacheEntry);
        File.WriteAllText(guidFile, "gif");

        try
        {
            TestProfile.ClearScratch(temp, temp, attempts: 1);

            Assert.True(Directory.Exists(live), "A per-run folder whose process is alive belongs to that run.");
            Assert.True(
                File.Exists(guidFile),
                "While another run is live, a GUID-named file may be the file its test is using.");
            Assert.True(
                Directory.Exists(cacheEntry),
                "The same goes for the cache folders the other run's tests write into.");
            Assert.False(Directory.Exists(finished), "A folder whose process is gone is litter whatever else runs.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
