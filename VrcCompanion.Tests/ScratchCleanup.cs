using System.IO;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The run deletes its own scratch here, while the test host is still alive and answering.
/// Process exit is the wrong place for it: a host that has reported every result need not run
/// its exit handlers at all, and one CI run did exactly that - 860 tests passed and the whole
/// scratch folder, loose GUID files and cache roots were still there for the check that ran
/// after it. The exit handler stays as a backstop for runs that never reach this collection
/// (a filtered run, an aborted one); the next run's startup sweep covers those.
/// </summary>
[Collection(ScratchCleanup.Collection)]
public sealed class ScratchCleanup
{
    internal const string Collection = "run-scratch-cleanup";

    [Fact]
    public void TheRunSweepsItsOwnScratchBeforeItEnds()
    {
        var scratch = TestProfile.Scratch;
        Assert.NotNull(scratch);

        TestProfile.CleanUp();

        if (TestProfile.KeepsScratch)
        {
            // A deliberate keep: the files are the point, and the next run sweeps them.
            Assert.True(Directory.Exists(scratch), "VRCC_KEEP_TEST_SCRATCH=1 should keep this run's folder.");
            return;
        }

        Assert.False(
            Directory.Exists(scratch),
            "The run's own folder must be gone before the run ends; leaving it means the next step sees litter.");
    }
}
