using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

[assembly: TestCollectionOrderer(
    "VrcCompanion.Tests.RealProfileGuardCollectionOrderer",
    "VrcCompanion.Tests")]

namespace VrcCompanion.Tests;

/// <summary>
/// The last word on whether the run touched real data. xUnit has no assembly teardown
/// hook - the framework executor returns before the tests finish, so a check there runs
/// too early - which leaves ordering this collection after every other one as the one
/// point after the last test.
/// A leak fails the test. A run the installed app was writing through is reported as
/// inconclusive instead: it passes, and the note the project prints after VSTest names
/// what changed and why it cannot be attributed.
/// </summary>
[Collection(RealProfileGuard.Collection)]
public sealed class RealProfileGuard
{
    internal const string Collection = "real-profile-guard";

    [Fact]
    public void TheRealProfileIsUnchangedByTheRun() => RealProfileWatch.Evaluate();
}

/// <summary>
/// Puts the guard and then the scratch cleanup after every other collection. Test
/// parallelization is already disabled for the assembly, so the collections run one at a time
/// in this order: the guard judges the run, and only then does the run delete its own scratch,
/// so a leak is reported before the files it might mention are gone. Both used to depend on
/// process exit, which a test host is not obliged to reach.
/// </summary>
public sealed class RealProfileGuardCollectionOrderer : ITestCollectionOrderer
{
    private static readonly string[] Tail =
    {
        RealProfileGuard.Collection,
        ScratchCleanup.Collection
    };

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        var all = testCollections.ToList();

        foreach (var collection in all.Where(collection => !Tail.Contains(collection.DisplayName)))
        {
            yield return collection;
        }

        foreach (var name in Tail)
        {
            foreach (var collection in all.Where(collection => collection.DisplayName == name))
            {
                yield return collection;
            }
        }
    }
}
