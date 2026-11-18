using System.Text.Json.Serialization;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Models;

/// <summary>
/// One message in the short chat history. The newest message is the one
/// currently being shown in VRChat's chatbox.
/// </summary>
public sealed class ChatboxChatItem : ViewModelBase
{
    private string _message = string.Empty;
    private string _editText = string.Empty;
    private bool _isRunning;
    private bool _canEdit;
    private bool _isEditing;
    private DateTime _sentAt = DateTime.UtcNow;

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
            OnPropertyChanged(nameof(PreviewText));
        }
    }

    [JsonPropertyName("sentAt")]
    public DateTime SentAt
    {
        get => _sentAt;
        set => SetProperty(ref _sentAt, value);
    }

    [JsonIgnore]
    public string EditText
    {
        get => _editText;
        set
        {
            if (!SetProperty(ref _editText, value))
            {
                return;
            }

            EditTextChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [JsonIgnore]
    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (!SetProperty(ref _isRunning, value))
            {
                return;
            }

            OnPropertyChanged(nameof(Opacity));
            OnPropertyChanged(nameof(StateText));
        }
    }

    [JsonIgnore]
    public bool CanEdit
    {
        get => _canEdit;
        set => SetProperty(ref _canEdit, value);
    }

    [JsonIgnore]
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    [JsonIgnore]
    public double Opacity => IsRunning ? 1 : 0.64;

    [JsonIgnore]
    public string StateText => IsRunning ? "Showing now" : "Expired";

    [JsonIgnore]
    public string DisplayText => string.IsNullOrWhiteSpace(Message) ? "(empty message)" : Message;

    [JsonIgnore]
    public string PreviewText => Message.Length > 58 ? Message[..58] + "…" : Message;

    public event EventHandler? EditTextChanged;
}
