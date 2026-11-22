using System;
using System.IO;

namespace VrcCompanion.Services;

/// <summary>
/// Every persisted file this app owns lives under one root. Tests point that root
/// at a scratch folder before the first store is touched, so no test can read or
/// write the real profile.
/// </summary>
internal static class UserDataPaths
{
    private static string? _directory;

    public static string Directory => _directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VrcCompanion");

    public static string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = Directory;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    internal static void UseDirectoryForTests(string directory) => _directory = directory;
}
