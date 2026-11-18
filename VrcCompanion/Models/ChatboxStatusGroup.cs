using System.Text.Json.Serialization;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Models;

/// <summary>
/// A named group of saved statuses. Only groups switched on for cycling take
/// part in the automatic rotation, matching MagicChatBox's status groups.
/// </summary>
public sealed class ChatboxStatusGroup : ViewModelBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private bool _isActiveForCycle = true;
    private bool _isFilterActive;

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    [JsonPropertyName("isActiveForCycle")]
    public bool IsActiveForCycle
    {
        get => _isActiveForCycle;
        set
        {
            if (SetProperty(ref _isActiveForCycle, value))
            {
                OnPropertyChanged(nameof(CycleText));
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    [JsonIgnore]
    public bool IsFilterActive
    {
        get => _isFilterActive;
        set => SetProperty(ref _isFilterActive, value);
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "(unnamed group)" : Name;

    [JsonIgnore]
    public string CycleText => IsActiveForCycle ? "In cycle" : "Paused";

    public event EventHandler? Changed;
}
