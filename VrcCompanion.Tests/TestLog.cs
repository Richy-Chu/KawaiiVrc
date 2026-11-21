using System;
using System.IO;
using System.Runtime.CompilerServices;
using VrcCompanion.Controls;
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

    /// <summary>
    /// Keeps the shared settings store off the user's real settings file. Several
    /// tests save through it (sorts, shields policy, lite mode), so without this a
    /// run inherits whatever the installed app last wrote and changes it too.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateSharedSettings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VrcCompanionTests", "shared");
        Directory.CreateDirectory(directory);
        SettingsStore.RedirectSharedForTests(
            Path.Combine(directory, Guid.NewGuid().ToString("N") + ".json"));
    }

    /// <summary>
    /// Keeps the WebView2 profile the tests create out of the user's %LocalAppData%, so a
    /// test run cannot touch real logins and cannot collide with a running copy of the app.
    /// </summary>
    [ModuleInitializer]
    internal static void IsolateBrowserProfile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "VrcCompanionTests", "browser");
        Directory.CreateDirectory(folder);
        EmbeddedBrowser.UserDataFolderOverride = folder;
    }
}
