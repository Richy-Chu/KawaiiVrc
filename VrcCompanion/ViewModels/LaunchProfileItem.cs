using VrcCompanion.Models;

namespace VrcCompanion.ViewModels;

/// <summary>A launch profile row. The built-in default profile has no arguments.</summary>
public sealed class LaunchProfileItem : ViewModelBase
{
    public LaunchProfileItem(LaunchProfile profile, bool isDefault = false)
    {
        Profile = profile;
        IsDefault = isDefault;
    }

    public LaunchProfile Profile { get; }

    public bool IsDefault { get; }

    public string Id => Profile.Id;

    public string Name
    {
        get => Profile.Name;
        set
        {
            if (string.Equals(Profile.Name, value, StringComparison.Ordinal))
            {
                return;
            }

            Profile.Name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
        }
    }

    public string Arguments
    {
        get => Profile.Arguments;
        set
        {
            if (string.Equals(Profile.Arguments, value, StringComparison.Ordinal))
            {
                return;
            }

            Profile.Arguments = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
        }
    }

    public string DisplayName => IsDefault
        ? "Default (no extra arguments)"
        : string.IsNullOrWhiteSpace(Name) ? "Untitled profile" : Name;

    public string Summary => IsDefault
        ? "No extra arguments"
        : string.IsNullOrWhiteSpace(Arguments) ? "No extra arguments" : Arguments.Trim();
}
