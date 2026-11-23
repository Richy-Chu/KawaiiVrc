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
/// inconclusive instead: it passes, and the note names what changed and why it cannot
/// be attributed.
/// </summary>
[Collection(RealProfileGuard.Collection)]
public sealed class RealProfileGuard
{
    internal const string Collection = "real-profile-guard";

    private readonly ITestOutputHelper _output;

    public RealProfileGuard(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheRealProfileIsUnchangedByTheRun()
    {
        var inconclusive = RealProfileWatch.Evaluate();
        if (inconclusive is null)
        {
            return;
        }

        // The plain run prints this from the note file the test project echoes after
        // VSTest; this copy is what detailed verbosity shows next to the test itself.
        _output.WriteLine(inconclusive);
    }
}

/// <summary>
/// Puts the guard collection after every other collection. Test parallelization is
/// already disabled for the assembly, so the collections run one at a time in this
/// order and the guard is genuinely the last thing the run does.
/// </summary>
public sealed class RealProfileGuardCollectionOrderer : ITestCollectionOrderer
{
    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        var all = testCollections.ToList();

        foreach (var collection in all.Where(collection => collection.DisplayName != RealProfileGuard.Collection))
        {
            yield return collection;
        }

        foreach (var collection in all.Where(collection => collection.DisplayName == RealProfileGuard.Collection))
        {
            yield return collection;
        }
    }
}
