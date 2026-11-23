using System;
using System.IO;
using System.Linq;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The seam and the sweep both run from module initialization, so nothing in the suite would
/// notice either changing: a temp write that escaped the run's folder would only show up as
/// litter on the machine, and a sweep that widened would delete another run's files. The run's
/// own folder needs no plant here - losing that skip deletes the folder the run just made,
/// which the store and log tests fail on at once.
/// </summary>
public sealed class TestScratchTests
{
    /// <summary>No process can own this id, so a folder named for it is litter.</summary>
    private const int DeadPid = 2147483000;

    /// <summary>
    /// The whole isolation rests on this: every test and every store resolves its temp path
    /// through <see cref="Path.GetTempPath"/>, so once that points inside the run's folder
    /// nothing can write beside another run's files however the path is built.
    /// </summary>
    [Fact]
    public void EveryTempWriteLandsInTheRunsOwnFolder()
    {
        var temp = Path.GetTempPath();
        Assert.StartsWith(TestProfile.Scratch, temp, StringComparison.OrdinalIgnoreCase);

        var file = Path.Combine(temp, "vrc-cosmetic-" + Guid.NewGuid().ToString("N") + ".gif");
        File.WriteAllText(file, "gif");

        try
        {
            Assert.StartsWith(
                TestProfile.Scratch,
                Path.GetFullPath(file),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>
    /// The deferral note is only worth reading if the probe behind it names real locks: a file
    /// another handle holds has to be named, and a free file - including one that is merely
    /// read-only, which a write-open would fail on - must not be, or the note would claim a
    /// Chromium deferral while a store file sat in the way.
    /// </summary>
    [Fact]
    public void TheLockProbeNamesAHeldFileAndIgnoresAFreeOne()
    {
        var sandbox = Path.Combine(TestProfile.Scratch, "vrc-held-" + Guid.NewGuid().ToString("N"));
        var held = Path.Combine(sandbox, "held.tmp");
        var free = Path.Combine(sandbox, "free.tmp");
        var readOnly = Path.Combine(sandbox, "readonly.tmp");

        Directory.CreateDirectory(sandbox);
        File.WriteAllBytes(held, Array.Empty<byte>());
        File.WriteAllBytes(free, Array.Empty<byte>());
        File.WriteAllBytes(readOnly, Array.Empty<byte>());
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        try
        {
            using (new FileStream(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var names = TestProfile.HeldFiles(sandbox).Select(file => file.Name).ToArray();

                Assert.Contains("held.tmp", names);
                Assert.DoesNotContain("free.tmp", names);
                // Read-only is an attribute, not a lock: opening it is what the probe must try.
                Assert.DoesNotContain("readonly.tmp", names);
            }

            using (new FileStream(readOnly, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var names = TestProfile.HeldFiles(sandbox).Select(file => file.Name).ToArray();

                // Held is held, whatever the attributes say: a read probe still sees the lock.
                Assert.Contains("readonly.tmp", names);
            }
        }
        finally
        {
            File.SetAttributes(readOnly, FileAttributes.Normal);
            Directory.Delete(sandbox, recursive: true);
        }
    }

    /// <summary>
    /// Everything a run writes is inside its own folder, and the one thing it cannot delete is
    /// its browser profile, which WebView2 holds open until the host exits. Both are named for
    /// the pid that owns them, so the sweep judges folders by that pid alone - no prefixes, no
    /// GUIDs, nothing that could belong to a run still going.
    /// </summary>
    [Fact]
    public void TheSweepTakesFinishedRunsAndLeavesTheRest()
    {
        var sandbox = Path.Combine(TestProfile.Scratch, "vrc-sweep-" + Guid.NewGuid().ToString("N"));
        var deadRun = Path.Combine(sandbox, "vrc-companion-tests-" + DeadPid);
        var deadBrowser = Path.Combine(sandbox, "vrc-companion-browser-" + DeadPid);
        var ownRun = Path.Combine(sandbox, "vrc-companion-tests-" + Environment.ProcessId);
        var ownBrowser = Path.Combine(sandbox, "vrc-companion-browser-" + Environment.ProcessId);
        var unrelated = Path.Combine(sandbox, "some-other-folder");

        Directory.CreateDirectory(deadRun);
        Directory.CreateDirectory(deadBrowser);
        Directory.CreateDirectory(ownRun);
        Directory.CreateDirectory(ownBrowser);
        Directory.CreateDirectory(unrelated);

        try
        {
            TestProfile.SweepFinishedRuns(sandbox);

            Assert.False(Directory.Exists(deadRun), "A run folder whose process is gone is litter.");
            Assert.False(Directory.Exists(deadBrowser), "A browser profile whose process is gone is litter too.");
            Assert.True(Directory.Exists(ownRun), "This run's own folder stays until it exits.");
            Assert.True(Directory.Exists(ownBrowser), "This run's browser profile is locked by its own browser.");
            Assert.True(Directory.Exists(unrelated), "The sweep only knows run folders; anything else is left alone.");
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }
}
