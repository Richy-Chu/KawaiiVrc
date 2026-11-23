using System;
using System.IO;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The sweep runs from the module initializer and nowhere else, so nothing in the suite would
/// notice it widening: a change that drops the GUID rule would only show up as another tool's
/// file disappearing from %TEMP%. The run's own folder needs no plant here - losing that skip
/// deletes the folder the run just made, which the store and log tests fail on at once.
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
        var running = Path.Combine(temp, "vrc-companion-tests-" + Environment.ProcessId);

        Directory.CreateDirectory(finished);
        Directory.CreateDirectory(running);
        File.WriteAllText(guidFile, "gif");
        File.WriteAllText(decoy, "log");

        try
        {
            // The sandbox itself is handed in as the run's folder, so no child of it is skipped
            // by name and both per-run folders are judged by their pid.
            TestProfile.ClearScratch(temp, temp, attempts: 1);

            Assert.False(File.Exists(guidFile), "A file the test named after its GUID is the run's litter.");
            Assert.True(File.Exists(decoy), "A name without a GUID belongs to whatever else wrote it.");
            Assert.False(Directory.Exists(finished), "A per-run folder whose process is gone is litter.");
            Assert.True(Directory.Exists(running), "A per-run folder whose process is alive may be a run still going.");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
