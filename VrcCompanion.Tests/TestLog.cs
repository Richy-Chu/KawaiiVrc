using System.IO;
using System.Runtime.CompilerServices;
using VrcCompanion.Services;

namespace VrcCompanion.Tests;

internal static class TestLog
{
    /// <summary>
    /// Runs before any test, so updater logging goes to a temp file instead of the
    /// user's %LocalAppData%\VrcCompanion\lifecycle.log.
    /// </summary>
    [ModuleInitializer]
    internal static void RedirectLifecycleLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VrcCompanionTests", "logs");
        LifecycleLog.RedirectTo(Path.Combine(directory, "lifecycle.log"));
    }
}
