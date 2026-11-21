using VrcCompanion.Services;
using Xunit;
using Xunit.Sdk;

namespace VrcCompanion.Tests;

/// <summary>
/// The tests that mutate the process-wide seams in <see cref="TestHooks"/>, or
/// that drive a real WebView2, share one collection. It never runs beside another
/// collection: a leftover hook or a second browser instance is what made the same
/// suite fail a different set of tests on every run.
/// </summary>
public static class Seams
{
    public const string Collection = "shell-seams";
}

[CollectionDefinition(Seams.Collection, DisableParallelization = true)]
public sealed class SeamCollection
{
}

/// <summary>
/// Puts every seam back to its production default after each test, so the next
/// class cannot inherit the hook or launcher this one set. It runs after rather
/// than before so a hook a test installed in its constructor is never cleared out
/// from under it.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class ResetSeamsAttribute : BeforeAfterTestAttribute
{
    public override void After(System.Reflection.MethodInfo methodUnderTest) => TestHooks.ResetAll();
}
