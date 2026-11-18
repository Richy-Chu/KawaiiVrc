using VrcCompanion.ViewModels;

namespace VrcCompanion.Models;

/// <summary>
/// A row on the Integrations page. It shows what a provider contributes right
/// now and whether the last build had to drop it to fit VRChat's line.
/// </summary>
public sealed class ChatboxIntegrationCard : ViewModelBase
{
    private readonly Action<bool> _setEnabled;
    private readonly Action<bool> _setVr;
    private readonly Action<bool> _setDesktop;
    private bool _isEnabled;
    private bool _useInVr;
    private bool _useInDesktop;
    private bool _isTrimmed;
    private string _preview = string.Empty;
    private int _order;

    public ChatboxIntegrationCard(
        string key,
        string displayName,
        string description,
        int order,
        bool isEnabled,
        Action<bool> setEnabled,
        string category = "Other",
        string setupHint = "",
        bool needsSetup = false,
        bool isAvailable = true,
        string unavailableReason = "",
        bool useInVr = true,
        bool useInDesktop = true,
        Action<bool>? setVr = null,
        Action<bool>? setDesktop = null)
    {
        Key = key;
        DisplayName = displayName;
        Description = description;
        _order = order;
        _isEnabled = isAvailable && isEnabled;
        _setEnabled = setEnabled;
        _useInVr = isAvailable && useInVr;
        _useInDesktop = isAvailable && useInDesktop;
        _setVr = setVr ?? (_ => { });
        _setDesktop = setDesktop ?? (_ => { });
        Category = category;
        SetupHint = setupHint;
        NeedsSetup = needsSetup;
        IsAvailable = isAvailable;
        UnavailableReason = unavailableReason;
    }

    public bool IsAvailable { get; }

    public string UnavailableReason { get; }

    public string Category { get; }

    public string SetupHint { get; }

    public bool NeedsSetup { get; }

    public string Key { get; }

    public string IconText => Key switch
    {
        "Status" => "💬",
        "Media" => "🎵",
        "Lyrics" => "🎤",
        "Spotify" => "🟢",
        "Window" => "🪟",
        "VrcRadar" => "📡",
        "HeartRate" => "❤",
        "Discord" => "🎮",
        "Twitch" => "📺",
        "TikTokLive" => "🎶",
        "TrackerBattery" => "🔋",
        "VrPerformance" => "🎮",
        "ComponentStats" => "🖥",
        "Soundpad" => "🔊",
        "Voicemod" => "🎙",
        "Network" => "📶",
        "Weather" => "🌦",
        "Time" => "🕒",
        _ => "✦"
    };

    public string DisplayName { get; }

    public string Description { get; }

    public int Order
    {
        get => _order;
        set
        {
            if (SetProperty(ref _order, value))
            {
                OnPropertyChanged(nameof(OrderText));
            }
        }
    }

    public string OrderText => Order.ToString("00");

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!IsAvailable && value)
            {
                return;
            }

            if (!SetProperty(ref _isEnabled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(DetailText));
            _setEnabled(value);
        }
    }

    public bool UseInVr
    {
        get => _useInVr;
        set
        {
            if (!IsAvailable && value)
            {
                return;
            }

            if (!SetProperty(ref _useInVr, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(StateText));
            _setVr(value);
        }
    }

    public bool UseInDesktop
    {
        get => _useInDesktop;
        set
        {
            if (!IsAvailable && value)
            {
                return;
            }

            if (!SetProperty(ref _useInDesktop, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ModeText));
            OnPropertyChanged(nameof(StateText));
            _setDesktop(value);
        }
    }

    public string ModeText => (UseInVr, UseInDesktop) switch
    {
        (true, true) => "VR + Desktop",
        (true, false) => "VR only",
        (false, true) => "Desktop only",
        _ => "No route"
    };

    public bool IsTrimmed
    {
        get => _isTrimmed;
        set
        {
            if (SetProperty(ref _isTrimmed, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public string Preview
    {
        get => _preview;
        set
        {
            if (SetProperty(ref _preview, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);

    public string StateText => !IsAvailable
        ? "Unavailable"
        : !IsEnabled
            ? "Off"
            : !UseInVr && !UseInDesktop
                ? "No route"
            : IsTrimmed
                ? "Trimmed to fit"
                : HasPreview ? "Live" : NeedsSetup ? "Needs setup" : "Waiting for data";

    public string DetailText => !IsAvailable
        ? UnavailableReason
        : StateText == "Needs setup" ? SetupHint : Description;
}
