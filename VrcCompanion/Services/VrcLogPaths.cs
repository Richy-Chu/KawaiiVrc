using System;
using System.IO;

namespace VrcCompanion.Services;

/// <summary>
/// VRChat's own output logs. These live outside this app's profile, so they are
/// read-only input rather than user data. Tests point this at a scratch folder so
/// a run never reads the real logs.
/// </summary>
internal static class VrcLogPaths
{
    private static string? _directory;

    public static string Directory => _directory ?? Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "..",
        "LocalLow",
        "VRChat",
        "VRChat"));

    internal static void UseDirectoryForTests(string directory) => _directory = directory;
}
