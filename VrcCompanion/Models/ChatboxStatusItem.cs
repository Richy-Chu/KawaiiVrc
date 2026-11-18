using System.Text.Json.Serialization;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Models;

/// <summary>
/// One saved status line. MagicChatbox keeps a library of these and only the
/// entries marked as favourites are used by the automatic cycle.
/// </summary>
public sealed class ChatboxStatusItem : ViewModelBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _message = string.Empty;
    private string? _groupId;
    private string _editText = string.Empty;
    private bool _isActive;
    private bool _useInCycle;
    private bool _isEditing;
    private bool _isSelected;
    private int _sortOrder;
    private DateTime _createdAt = DateTime.UtcNow;
    private DateTime _lastUsedAt;

    [JsonPropertyName("id")]
    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    [JsonPropertyName("message")]
    public string Message
    {
        get => _message;
        set
        {
            if (!SetProperty(ref _message, value))
            {
                return;
            }

            OnPropertyChanged(nameof(DisplayText));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [JsonPropertyName("isActive")]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (!SetProperty(ref _isActive, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusPowerText));
        }
    }

    [JsonPropertyName("groupId")]
    public string? GroupId
    {
        get => _groupId;
        set
        {
            if (SetProperty(ref _groupId, string.IsNullOrWhiteSpace(value) ? null : value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    [JsonPropertyName("sortOrder")]
    public int SortOrder
    {
        get => _sortOrder;
        set => SetProperty(ref _sortOrder, value);
    }

    [JsonIgnore]
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    [JsonPropertyName("useInCycle")]
    public bool UseInCycle
    {
        get => _useInCycle;
        set
        {
            if (!SetProperty(ref _useInCycle, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsFavorite));
            OnPropertyChanged(nameof(CycleText));
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [JsonIgnore]
    public bool IsFavorite
    {
        get => UseInCycle;
        set => UseInCycle = value;
    }

    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    [JsonIgnore]
    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value);
    }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt
    {
        get => _createdAt;
        set => SetProperty(ref _createdAt, value);
    }

    [JsonPropertyName("lastUsedAt")]
    public DateTime LastUsedAt
    {
        get => _lastUsedAt;
        set => SetProperty(ref _lastUsedAt, value);
    }

    [JsonIgnore]
    public string DisplayText => string.IsNullOrWhiteSpace(Message) ? "(empty status)" : Message;

    [JsonIgnore]
    public string GroupText => string.IsNullOrWhiteSpace(GroupId) ? "Ungrouped" : "Grouped";

    [JsonIgnore]
    public string StatusPowerText => IsActive ? "● Live" : "Use";

    [JsonIgnore]
    public string CycleText => UseInCycle ? "★ Cycling" : "☆ Manual";

    /// <summary>Raised whenever a persisted field changes so the owning view model can save.</summary>
    public event EventHandler? Changed;
}
