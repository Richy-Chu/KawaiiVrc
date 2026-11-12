using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// Tests that install the process-wide fake community handler. Only this
/// collection is serialized; other tests keep running in parallel.
/// </summary>
[CollectionDefinition("CommunityHandler", DisableParallelization = true)]
public class CommunityHandlerCollection
{
}
