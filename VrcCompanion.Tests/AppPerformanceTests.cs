using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public sealed class AppPerformanceTests
{
    [Fact]
    public void StoredChoiceOverridesDetection()
    {
        // Lite mode is process-wide and every shell constructor applies the saved
        // settings, so the count below only means anything from a known baseline.
        AppPerformance.Set(false);

        var notified = 0;
        void Handler() => notified++;
        AppPerformance.Changed += Handler;
        try
        {
            AppPerformance.Apply(new CompanionSettings { LiteMode = true });
            Assert.True(AppPerformance.LiteMode);

            AppPerformance.Apply(new CompanionSettings { LiteMode = false });
            Assert.False(AppPerformance.LiteMode);

            // Setting the same value twice must not re-notify listeners.
            AppPerformance.Set(false);
            Assert.Equal(2, notified);
        }
        finally
        {
            AppPerformance.Changed -= Handler;
            AppPerformance.Set(false);
        }
    }

    [Fact]
    public void LiteModeDecodesPicturesAtAWidthARowCanShow()
    {
        try
        {
            AppPerformance.Set(false);
            Assert.Equal(1024, ImageLoader.ClampDecodeWidth(1024));

            // The cap only bites above it: a row that asked for less keeps what it asked for,
            // and the floor of 32 still applies.
            AppPerformance.Set(true);
            Assert.Equal(ImageLoader.LiteDecodeWidth, ImageLoader.ClampDecodeWidth(1024));
            Assert.Equal(64, ImageLoader.ClampDecodeWidth(64));
            Assert.Equal(32, ImageLoader.ClampDecodeWidth(8));
        }
        finally
        {
            AppPerformance.Set(false);
        }
    }

    [Fact]
    public void LiteModeSurvivesASettingsRoundTrip()
    {
        var path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "VrcCompanion.Tests",
            System.Guid.NewGuid().ToString("N"),
            "settings.json");

        var store = new SettingsStore(path);
        store.Settings.LiteMode = true;
        store.Save();

        var reloaded = new SettingsStore(path);
        Assert.True(reloaded.Settings.LiteMode);

        reloaded.Settings.LiteMode = false;
        reloaded.Save();
        Assert.False(new SettingsStore(path).Settings.LiteMode);
    }
}
