using System.Collections.ObjectModel;
using System.Windows.Threading;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Sends chatbox messages to VRChat over OSC and can rotate them so the
/// chatbox keeps showing a fresh line (the \"magic chatbox\" behaviour).
/// </summary>
public sealed class ChatboxViewModel : ViewModelBase, IDisposable
{
    private const int MinimumRotationSeconds = 5;

    private readonly SettingsStore _store;
    private readonly CompanionSettings _settings;
    private readonly OscChatboxService _osc;
    private readonly DispatcherTimer _timer;
    private string _messageText = string.Empty;
    private string _statusText;
    private bool _isRotating;
    private int _rotationSeconds;
    private string _host;
    private int _port;
    private int _rotationIndex;
    private bool _disposed;

    public ChatboxViewModel(SettingsStore? store = null, OscChatboxService? osc = null)
    {
        _store = store ?? SettingsStore.Shared;
        _settings = _store.Settings;
        _osc = osc ?? new OscChatboxService();
        _host = string.IsNullOrWhiteSpace(_settings.OscHost) ? "127.0.0.1" : _settings.OscHost;
        _port = Math.Clamp(_settings.OscPort, 1, 65535);
        _rotationSeconds = Math.Clamp(_settings.ChatboxRotationSeconds, MinimumRotationSeconds, 600);
        _osc.Host = _host;
        _osc.Port = _port;

        RotationMessages = new ObservableCollection<string>(
            _settings.ChatboxMessages
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Select(message => message.Length > OscMessage.ChatboxLimit
                    ? message[..OscMessage.ChatboxLimit]
                    : message)
                .Distinct(StringComparer.Ordinal)
                .Take(25));

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_rotationSeconds) };
        _timer.Tick += (_, _) => RotateNext();
        _statusText = "Ready. Enable OSC in VRChat: Action Menu → Options → OSC.";

        SendCommand = new RelayCommand(Send, () => !string.IsNullOrWhiteSpace(MessageText));
        AddToRotationCommand = new RelayCommand(AddToRotation, () => !string.IsNullOrWhiteSpace(MessageText));
        RemoveMessageCommand = new RelayCommand<string>(RemoveMessage, message => message is not null);

        if (_settings.ChatboxRotateEnabled && RotationMessages.Count > 0)
        {
            _isRotating = true;
            _timer.Start();
            _statusText = $"Rotating {RotationMessages.Count} message(s) every {RotationSeconds}s.";
        }
    }

    public ObservableCollection<string> RotationMessages { get; }

    public RelayCommand SendCommand { get; }

    public RelayCommand AddToRotationCommand { get; }

    public RelayCommand<string> RemoveMessageCommand { get; }

    public string MessageText
    {
        get => _messageText;
        set
        {
            if (SetProperty(ref _messageText, value))
            {
                OnPropertyChanged(nameof(MessageCountText));
                SendCommand.RaiseCanExecuteChanged();
                AddToRotationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string MessageCountText => $"{MessageText.Length}/{OscMessage.ChatboxLimit}";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsRotating
    {
        get => _isRotating;
        set
        {
            if (!SetProperty(ref _isRotating, value))
            {
                return;
            }

            if (_isRotating && RotationMessages.Count > 0)
            {
                _rotationIndex = 0;
                _timer.Start();
                StatusText = $"Rotating {RotationMessages.Count} message(s) every {RotationSeconds}s.";
            }
            else
            {
                _timer.Stop();
                StatusText = RotationMessages.Count == 0
                    ? "Add a message before rotating."
                    : "Rotation stopped.";
            }

            _settings.ChatboxRotateEnabled = _isRotating && RotationMessages.Count > 0;
            _store.Save();
            OnPropertyChanged(nameof(RotationButtonText));
        }
    }

    public string RotationButtonText => IsRotating ? "Stop rotation" : "Start rotation";

    public int RotationSeconds
    {
        get => _rotationSeconds;
        set
        {
            var clamped = Math.Clamp(value, MinimumRotationSeconds, 600);
            if (!SetProperty(ref _rotationSeconds, clamped))
            {
                return;
            }

            _timer.Interval = TimeSpan.FromSeconds(clamped);
            _settings.ChatboxRotationSeconds = clamped;
            _store.Save();
            OnPropertyChanged(nameof(RotationSecondsText));
        }
    }

    public string RotationSecondsText => $"{RotationSeconds}s";

    public string Host
    {
        get => _host;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
            if (!SetProperty(ref _host, trimmed))
            {
                return;
            }

            _osc.Host = trimmed;
            _settings.OscHost = trimmed;
            _store.Save();
        }
    }

    public int Port
    {
        get => _port;
        set
        {
            var clamped = Math.Clamp(value, 1, 65535);
            if (!SetProperty(ref _port, clamped))
            {
                return;
            }

            _osc.Port = clamped;
            _settings.OscPort = clamped;
            _store.Save();
        }
    }

    public bool HasRotationMessages => RotationMessages.Count > 0;

    public bool HasNoRotationMessages => RotationMessages.Count == 0;

    private void Send()
    {
        var text = MessageText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        SendText(text);
    }

    private void AddToRotation()
    {
        var text = MessageText.Trim();
        if (text.Length == 0)
        {
            return;
        }

        if (text.Length > OscMessage.ChatboxLimit)
        {
            text = text[..OscMessage.ChatboxLimit];
        }

        if (!RotationMessages.Contains(text, StringComparer.Ordinal))
        {
            RotationMessages.Add(text);
            SaveMessages();
            OnPropertyChanged(nameof(HasRotationMessages));
            OnPropertyChanged(nameof(HasNoRotationMessages));
            StatusText = $"Added to rotation ({RotationMessages.Count}).";
        }
        else
        {
            StatusText = "That message is already in the rotation.";
        }
    }

    private void RemoveMessage(string? message)
    {
        if (message is null)
        {
            return;
        }

        if (RotationMessages.Remove(message))
        {
            SaveMessages();
            OnPropertyChanged(nameof(HasRotationMessages));
            OnPropertyChanged(nameof(HasNoRotationMessages));
            if (RotationMessages.Count == 0)
            {
                IsRotating = false;
            }
        }
    }

    private void RotateNext()
    {
        if (RotationMessages.Count == 0)
        {
            IsRotating = false;
            return;
        }

        var text = RotationMessages[_rotationIndex % RotationMessages.Count];
        _rotationIndex++;
        SendText(text, rotating: true);
    }

    private void SendText(string text, bool rotating = false)
    {
        try
        {
            _osc.SendChatbox(text);
            var preview = text.Length > 48 ? text[..48] + "…" : text;
            StatusText = rotating
                ? $"Sent rotation message: {preview}"
                : $"Sent: {preview}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not send OSC: {ex.Message}";
        }
    }

    private void SaveMessages()
    {
        _settings.ChatboxMessages = RotationMessages.ToList();
        _settings.ChatboxRotateEnabled = IsRotating;
        _store.Save();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _osc.Dispose();
    }
}
