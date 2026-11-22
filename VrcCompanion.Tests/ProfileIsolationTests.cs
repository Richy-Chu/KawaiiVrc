using System;
using System.IO;
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
