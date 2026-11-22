using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The module initializer points every store at a scratch root. These guards keep
/// a later change from quietly restoring the real profile as the test default.
/// </summary>
public sealed class ProfileIsolationTests
{
    [Fact]
    public void TheTestProfileRootIsNotTheRealProfile()
    {
        var realRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcCompanion");

        Assert.NotEqual(realRoot, UserDataPaths.Directory);
        Assert.StartsWith(Path.GetTempPath(), UserDataPaths.Directory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VrChatLogsAreReadFromTheTestDirectory()
    {
        Assert.StartsWith(Path.GetTempPath(), VrcLogPaths.Directory, StringComparison.OrdinalIgnoreCase);

        var log = Path.Combine(VrcLogPaths.Directory, "output_log_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(log, "test");
        File.SetLastWriteTimeUtc(log, DateTime.UtcNow.AddMinutes(1));
        try
        {
            Assert.Equal(
                Path.GetFullPath(log),
                Path.GetFullPath(VrChatLogPlayerService.FindLatestLog()!));
        }
        finally
        {
            File.Delete(log);
        }
    }

    /// <summary>
    /// A direct profile-root literal is how a new store would quietly escape the
    /// scratch root. UserDataPaths is the only file allowed to build it.
    /// </summary>
    [Fact]
    public void OnlyUserDataPathsBuildsTheRealProfileRoot()
    {
        Assert.Contains(
            ProductionSources(),
            path => string.Equals(Path.GetFileName(path), "UserDataPaths.cs", StringComparison.OrdinalIgnoreCase));

        var offenders = ProductionSources()
            .Where(path => !string.Equals(Path.GetFileName(path), "UserDataPaths.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadLines(path).Any(line => line.Contains("\"VrcCompanion\"", StringComparison.Ordinal)))
            .Select(RelativeSourcePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These files build the real VrcCompanion profile path directly; use UserDataPaths.Combine instead:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// LocalApplicationData is the base for both roots. Anything else reaching for
    /// it can write under the real profile (or read the real VRChat logs).
    /// </summary>
    [Fact]
    public void OnlyThePathRootsReadLocalApplicationData()
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UserDataPaths.cs",
            "VrcLogPaths.cs"
        };

        var offenders = ProductionSources()
            .Where(path => !allowed.Contains(Path.GetFileName(path)))
            .Where(path => File.ReadLines(path).Any(line => line.Contains(
                "SpecialFolder.LocalApplicationData",
                StringComparison.Ordinal)))
            .Select(RelativeSourcePath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "These files resolve LocalApplicationData directly; use UserDataPaths or VrcLogPaths instead:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> ProductionSources()
    {
        var root = FindProductionSourceRoot();
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Any(part => string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)));
    }

    private static string FindProductionSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "VrcCompanion", "Services", "UserDataPaths.cs");
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, "VrcCompanion");
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the VrcCompanion source tree above " + AppContext.BaseDirectory);
    }

    private static string RelativeSourcePath(string path)
        => Path.GetRelativePath(FindProductionSourceRoot(), path);

    [Fact]
    public void StoresResolveInsideTheTestRoot()
    {
        var session = new SessionStore();

        Assert.StartsWith(UserDataPaths.Directory, session.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            UserDataPaths.Directory,
            UserDataPaths.Combine("activity.json"),
            StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(
            UserDataPaths.Directory,
            UserDataPaths.Combine("instance-history.db"),
            StringComparison.OrdinalIgnoreCase);
    }
}
