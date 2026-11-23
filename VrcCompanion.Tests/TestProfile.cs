using System;
using System.IO;
using System.Runtime.CompilerServices;
using VrcCompanion.Services;

namespace VrcCompanion.Tests;

/// <summary>
/// Runs before any test. A default <see cref="SessionStore"/> must never point
/// at the user's profile: a test that reaches a 401 path would otherwise clear
/// the real saved session and sign the user out.
/// </summary>
internal static class TestProfile
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        // First, before any seam moves: the real profile in the state the run found it.
        RealProfileWatch.EnsureBaseline();

        var directory = Path.Combine(
            Path.GetTempPath(),
            "vrc-companion-tests-" + Environment.ProcessId);
        UserDataPaths.UseDirectoryForTests(directory);

        var logs = Path.Combine(directory, "vrchat-logs");
        Directory.CreateDirectory(logs);
        VrcLogPaths.UseDirectoryForTests(logs);
    }
}
