using System;
using System.Windows.Media;

namespace VrcCompanion.Services;

/// <summary>
/// Decides whether the app runs its light-resource presentation. On machines
/// with a weak render tier this turns itself on, and the Settings toggle can
/// override it either way. Lite mode stops the continuously repainted aurora
/// background, GIF backdrops and image fades: all small individually, but on an
/// old PC they are the difference between smooth and sluggish.
/// </summary>
public static class AppPerformance
{
    /// <summary>Raised on the UI thread after the mode changes.</summary>
    public static event Action? Changed;

    public static bool LiteMode { get; private set; }

    /// <summary>
    /// WPF reports 0/1 for software or weak rendering. Detection is best-effort:
    /// if it cannot be read the app keeps the normal presentation.
    /// </summary>
    public static bool DetectedLiteMode
    {
        get
        {
            try
            {
                return (RenderCapability.Tier >> 16) < 2;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Applies the stored choice, or the detected one when unset.</summary>
    public static void Apply(CompanionSettings settings)
        => Set(settings.LiteMode ?? DetectedLiteMode);

    public static void Set(bool value)
    {
        if (LiteMode == value)
        {
            return;
        }

        LiteMode = value;
        Changed?.Invoke();
    }
}
