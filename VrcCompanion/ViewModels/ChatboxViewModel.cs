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
    private readonly IChatboxSender _osc;
    private readonly NowPlayingService _nowPlaying;
    private readonly LyricsService _lyricsService;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _nowPlayingTimer;
    private readonly DispatcherTimer _lyricsDisplayTimer;
    private NowPlayingInfo? _currentTrack;
    private bool _pollingNowPlaying;
    private LyricsDocument? _lyrics;
    private string? _lyricsTrackKey;
    private CancellationTokenSource? _lyricsCancellation;
    private bool _showLyrics;
    private bool _showLyricsOnVrchat;
    private bool _pauseLyricsAfterMessage;
    private int _lyricsPauseSeconds;
    private readonly LyricsPausePolicy _lyricsPause = new();
    private readonly PlaybackPositionEstimator _position = new();
    private string _lyricsStatusText = "Play a track to look for free lyrics.";
    private string _lyricsMatchText = string.Empty;
    private Task? _lyricsLoadTask;
    private int _plainLyricIndex;
    private string? _lastVrchatLyric;
    private DateTime _lastOscSend = DateTime.MinValue;
    private string _messageText = string.Empty;
    private string _statusText;
    private bool _isRotating;
    private bool _includeNowPlaying;
    private int _rotationSeconds;
    private string _host;
    private int _port;
    private int _rotationIndex;
    private bool _disposed;

    public ChatboxViewModel(
        SettingsStore? store = null,
        IChatboxSender? osc = null,
        NowPlayingService? nowPlaying = null,
        LyricsService? lyrics = null,
        bool startTimers = true)
    {
        _store = store ?? SettingsStore.Shared;
        _settings = _store.Settings;
        _osc = osc ?? new OscChatboxService();
        _nowPlaying = nowPlaying ?? new NowPlayingService();
        _lyricsService = lyrics ?? new LyricsService();
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

        _includeNowPlaying = _settings.ChatboxIncludeNowPlaying;
        if (_includeNowPlaying && !RotationMessages.Contains("{music}", StringComparer.OrdinalIgnoreCase))
        {
            RotationMessages.Add("{music}");
        }

        _showLyrics = _settings.ChatboxShowLyrics;
        if (_showLyrics && !RotationMessages.Contains("{lyrics}", StringComparer.OrdinalIgnoreCase))
        {
            RotationMessages.Add("{lyrics}");
        }

        _showLyricsOnVrchat = _settings.ChatboxLyricsOnVrchat;
        _pauseLyricsAfterMessage = _settings.ChatboxPauseLyricsAfterMessage;
        _lyricsPauseSeconds = _settings.ChatboxLyricsPauseSeconds <= 0
            ? 30
            : Math.Clamp(_settings.ChatboxLyricsPauseSeconds, 5, 3600);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_rotationSeconds) };
        _timer.Tick += (_, _) => RotateNext();
        _nowPlayingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _nowPlayingTimer.Tick += (_, _) => _ = PollNowPlayingAsync();
        _lyricsDisplayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _lyricsDisplayTimer.Tick += (_, _) =>
        {
            OnPropertyChanged(nameof(LyricsText));
            OnPropertyChanged(nameof(RemainingText));
            UpdateVrchatLyrics();
        };
        if (startTimers)
        {
            _lyricsDisplayTimer.Start();
        }

        _statusText = "Ready. Enable OSC in VRChat: Action Menu → Options → OSC.";

        SendCommand = new RelayCommand(Send, () => !string.IsNullOrWhiteSpace(MessageText));
        AddToRotationCommand = new RelayCommand(AddToRotation, () => !string.IsNullOrWhiteSpace(MessageText));
        RemoveMessageCommand = new RelayCommand<string>(RemoveMessage, message => message is not null);
        UseNowPlayingCommand = new RelayCommand(UseNowPlaying, () => CurrentTrack is not null);
        AddNowPlayingToRotationCommand = new RelayCommand(AddNowPlayingToRotation, () => CurrentTrack is not null);
        SendLyricCommand = new RelayCommand(SendLyric, () => HasLyrics);
        ResumeLyricsCommand = new RelayCommand(ResumeLyricsNow, () => ShowLyricsOnVrchat && CurrentTrack is not null);
        RetryLyricsCommand = new RelayCommand(RetryLyrics, () => CurrentTrack is not null);

        if (startTimers)
        {
            _nowPlayingTimer.Start();
            _ = PollNowPlayingAsync();
        }

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

    public RelayCommand UseNowPlayingCommand { get; }

    public RelayCommand AddNowPlayingToRotationCommand { get; }

    public RelayCommand SendLyricCommand { get; }

    public RelayCommand ResumeLyricsCommand { get; }

    public RelayCommand RetryLyricsCommand { get; }

    public NowPlayingInfo? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            if (!SetProperty(ref _currentTrack, value))
            {
                return;
            }

            OnPropertyChanged(nameof(NowPlayingText));
            OnPropertyChanged(nameof(NowPlayingSource));
            OnPropertyChanged(nameof(HasNowPlaying));
            OnPropertyChanged(nameof(HasNothingPlaying));
            _position.Update(value, DateTime.UtcNow);
            UseNowPlayingCommand.RaiseCanExecuteChanged();
            AddNowPlayingToRotationCommand.RaiseCanExecuteChanged();
            ResumeLyricsCommand.RaiseCanExecuteChanged();
            RetryLyricsCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(LyricsText));
            OnPropertyChanged(nameof(HasLyrics));
            OnPropertyChanged(nameof(RemainingText));
            OnPropertyChanged(nameof(IsLyricsPaused));
            TrackChanged();
        }
    }

    public string NowPlayingText => CurrentTrack?.DisplayText ?? "Nothing playing right now.";

    public string NowPlayingSource => CurrentTrack?.Source ?? string.Empty;

    public string RemainingText
    {
        get
        {
            if (CurrentTrack is null || CurrentTrack.Duration <= TimeSpan.Zero)
            {
                return string.Empty;
            }

            var remaining = CurrentTrack.Duration - EstimatedPosition();
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            return $"{(int)remaining.TotalMinutes}:{remaining.Seconds:00} left";
        }
    }

    public bool HasNowPlaying => CurrentTrack is not null;

    public bool HasNothingPlaying => CurrentTrack is null;

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

    /// <summary>Keeps a {music} entry in rotation so the track updates by itself.</summary>
    public bool IncludeNowPlayingInRotation
    {
        get => _includeNowPlaying;
        set
        {
            if (!SetProperty(ref _includeNowPlaying, value))
            {
                return;
            }

            if (value)
            {
                if (!RotationMessages.Contains("{music}", StringComparer.OrdinalIgnoreCase))
                {
                    RotationMessages.Add("{music}");
                }
            }
            else
            {
                for (var i = RotationMessages.Count - 1; i >= 0; i--)
                {
                    if (RotationMessages[i].Equals("{music}", StringComparison.OrdinalIgnoreCase))
                    {
                        RotationMessages.RemoveAt(i);
                    }
                }
            }

            SaveMessages();
            OnPropertyChanged(nameof(HasRotationMessages));
            OnPropertyChanged(nameof(HasNoRotationMessages));
            if (RotationMessages.Count == 0)
            {
                IsRotating = false;
            }
        }
    }

    /// <summary>Shows the current lyric line in the chatbox through {lyrics}.</summary>
    /// <summary>Sends the current lyric line to the VRChat chatbox automatically.</summary>
    public bool ShowLyricsOnVrchat
    {
        get => _showLyricsOnVrchat;
        set
        {
            if (!SetProperty(ref _showLyricsOnVrchat, value))
            {
                return;
            }

            _settings.ChatboxLyricsOnVrchat = value;
            _store.Save();
            _lastVrchatLyric = null;
            _lyricsPause.Resume();
            OnPropertyChanged(nameof(IsLyricsPaused));
            ResumeLyricsCommand.RaiseCanExecuteChanged();

            if (value)
            {
                if (IsRotating)
                {
                    IsRotating = false;
                }

                StatusText = "Lyrics will appear on the VRChat chatbox. Typed messages still send.";
                if (_lyrics is null && CurrentTrack is not null)
                {
                    TrackChanged(force: true);
                }
            }
            else
            {
                StatusText = "VRChat lyrics display stopped.";
            }
        }
    }

    /// <summary>Holds automatic lyrics for a while after a typed chatbox message.</summary>
    public bool PauseLyricsAfterMessage
    {
        get => _pauseLyricsAfterMessage;
        set
        {
            if (!SetProperty(ref _pauseLyricsAfterMessage, value))
            {
                return;
            }

            _settings.ChatboxPauseLyricsAfterMessage = value;
            _store.Save();
        }
    }

    public int LyricsPauseSeconds
    {
        get => _lyricsPauseSeconds;
        set
        {
            var clamped = Math.Clamp(value, 5, 3600);
            if (!SetProperty(ref _lyricsPauseSeconds, clamped))
            {
                return;
            }

            _settings.ChatboxLyricsPauseSeconds = clamped;
            _store.Save();
        }
    }

    public bool IsLyricsPaused => _lyricsPause.IsPaused(DateTime.UtcNow);

    public bool ShowLyricsInRotation
    {
        get => _showLyrics;
        set
        {
            if (!SetProperty(ref _showLyrics, value))
            {
                return;
            }

            _settings.ChatboxShowLyrics = value;
            if (value)
            {
                if (!RotationMessages.Contains("{lyrics}", StringComparer.OrdinalIgnoreCase))
                {
                    RotationMessages.Add("{lyrics}");
                }

                if (_lyrics is null && CurrentTrack is not null)
                {
                    TrackChanged(force: true);
                }
            }
            else
            {
                for (var i = RotationMessages.Count - 1; i >= 0; i--)
                {
                    if (RotationMessages[i].Equals("{lyrics}", StringComparison.OrdinalIgnoreCase))
                    {
                        RotationMessages.RemoveAt(i);
                    }
                }
            }

            SaveMessages();
            OnPropertyChanged(nameof(HasRotationMessages));
            OnPropertyChanged(nameof(HasNoRotationMessages));
            if (RotationMessages.Count == 0)
            {
                IsRotating = false;
            }
        }
    }

    public string LyricsText
    {
        get
        {
            if (CurrentTrack is null)
            {
                return "Play a track to look for free lyrics.";
            }

            if (_lyrics is null)
            {
                return _lyricsStatusText;
            }

            return _lyrics.PreviewLine(EstimatedPosition()) ?? _lyricsStatusText;
        }
    }

    public string LyricsStatusText
    {
        get => _lyricsStatusText;
        private set => SetProperty(ref _lyricsStatusText, value);
    }

    public string LyricsMatchText => _lyricsMatchText;

    public bool HasLyricsMatch => _lyricsMatchText.Length > 0;

    public bool HasLyrics => _lyrics?.HasLyrics == true;

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

        AddRotationText(text);
    }

    private void UseNowPlaying()
    {
        if (CurrentTrack is null)
        {
            return;
        }

        MessageText = CurrentTrack.ChatboxText;
        StatusText = "Now playing placed in the message box.";
    }

    private void AddNowPlayingToRotation()
    {
        if (CurrentTrack is null)
        {
            return;
        }

        AddRotationText(CurrentTrack.ChatboxText);
    }

    private void AddRotationText(string text)
    {
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

    private void UpdateVrchatLyrics(bool force = false)
    {
        if (!_showLyricsOnVrchat || CurrentTrack is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (!force && (_lyricsPause.IsPaused(now) || now - _lastOscSend < TimeSpan.FromSeconds(5)))
        {
            return;
        }

        var line = _lyrics?.HasLyrics == true
            ? (_lyrics.IsSynced
                ? _lyrics.LineAt(EstimatedPosition())
                : GetCurrentLyricLine(advancePlain: true))
            : null;

        var message = ChatboxLyrics.Compose(CurrentTrack.ChatboxText, line);
        if (!force && message == _lastVrchatLyric)
        {
            return;
        }

        try
        {
            _osc.SendChatbox(message);
            _lastOscSend = now;
            _lastVrchatLyric = message;
            var preview = line is null
                ? message.Length > 52 ? message[..52] + "…" : message
                : line.Length > 58 ? line[..58] + "…" : line;
            StatusText = line is null
                ? $"Now playing on VRChat: {preview}"
                : $"VRChat lyric: {preview}";
        }
        catch (Exception ex)
        {
            StatusText = $"Could not send lyrics to VRChat: {ex.Message}";
        }
    }

    private void RetryLyrics()
    {
        if (CurrentTrack is null)
        {
            return;
        }

        _lyricsService.ClearCache();
        _lastVrchatLyric = null;
        TrackChanged(force: true);
        StatusText = "Retrying lyrics…";
    }

    private void ResumeLyricsNow()
    {
        _lyricsPause.Resume();
        OnPropertyChanged(nameof(IsLyricsPaused));
        _lastVrchatLyric = null;
        _lastOscSend = DateTime.MinValue;

        if (_showLyricsOnVrchat && CurrentTrack is not null)
        {
            UpdateVrchatLyrics(force: true);
            StatusText = "Lyrics resumed.";
        }
        else
        {
            StatusText = "Lyrics are not currently displayed.";
        }
    }

    private void SendLyric()
    {
        var line = GetCurrentLyricLine(advancePlain: true);
        if (string.IsNullOrWhiteSpace(line))
        {
            StatusText = "No lyric line is available for this track.";
            return;
        }

        SendText(line, pauseLyrics: false);
        StatusText = "Sent lyric line.";
    }

    private void TrackChanged(bool force = false)
    {
        var track = CurrentTrack;

        if (track is null)
        {
            _lyricsCancellation?.Cancel();
            _lyrics = null;
            _lyricsTrackKey = null;
            _lyricsLoadTask = null;
            SetLyricsMatch(null);
            LyricsStatusText = "Play a track to look for free lyrics.";
            OnPropertyChanged(nameof(LyricsText));
            OnPropertyChanged(nameof(HasLyrics));
            SendLyricCommand.RaiseCanExecuteChanged();
            return;
        }

        var key = $"{track.Title}\u001f{track.Artist}\u001f{(track.Duration > TimeSpan.Zero ? "known" : "unknown")}";
        if (!force && key == _lyricsTrackKey)
        {
            OnPropertyChanged(nameof(LyricsText));
            return;
        }

        _lyricsCancellation?.Cancel();
        _lyricsTrackKey = key;
        _lastVrchatLyric = null;
        _lyricsLoadTask = LoadLyricsAsync(track, key);
    }

    private async Task LoadLyricsAsync(NowPlayingInfo track, string key)
    {
        var cancellation = new CancellationTokenSource();
        _lyricsCancellation = cancellation;
        _lyrics = null;
        SetLyricsMatch(null);
        LyricsStatusText = "Looking for free lyrics…";
        OnPropertyChanged(nameof(LyricsText));
        OnPropertyChanged(nameof(HasLyrics));
        SendLyricCommand.RaiseCanExecuteChanged();

        try
        {
            var result = await _lyricsService.GetForTrackAsync(
                track.Title,
                track.Artist,
                cancellation.Token,
                track.Duration);

            if (cancellation.IsCancellationRequested || key != _lyricsTrackKey)
            {
                return;
            }

            _lyrics = result.Lyrics;
            _lastVrchatLyric = null;
            SetLyricsMatch(result.Match);
            LyricsStatusText = result.Status;
            OnPropertyChanged(nameof(LyricsText));
            OnPropertyChanged(nameof(HasLyrics));
            SendLyricCommand.RaiseCanExecuteChanged();
        }
        catch (OperationCanceledException)
        {
            // A new track replaced this lookup.
        }
    }

    private string? GetCurrentLyricLine(bool advancePlain)
    {
        if (_lyrics is null || !_lyrics.HasLyrics)
        {
            return null;
        }

        if (_lyrics.IsSynced)
        {
            return _lyrics.LineAt(EstimatedPosition());
        }

        var lines = _lyrics.PlainLines;
        if (lines.Count == 0)
        {
            return null;
        }

        var line = lines[_plainLyricIndex % lines.Count];
        if (advancePlain)
        {
            _plainLyricIndex++;
        }

        return line;
    }

    private async Task PollNowPlayingAsync()
    {
        if (_pollingNowPlaying)
        {
            return;
        }

        _pollingNowPlaying = true;
        try
        {
            CurrentTrack = await _nowPlaying.GetCurrentAsync();
        }
        finally
        {
            _pollingNowPlaying = false;
        }
    }

    /// <summary>Runs one real rotation tick (for tests).</summary>
    internal void RotateNextForTest() => RotateNext();

    internal void SetTrackForTest(NowPlayingInfo? track) => CurrentTrack = track;

    internal Task? LyricsLoadTaskForTest => _lyricsLoadTask;

    private void SetLyricsMatch(string? match)
    {
        _lyricsMatchText = string.IsNullOrWhiteSpace(match) ? string.Empty : $"Matched: {match}";
        OnPropertyChanged(nameof(LyricsMatchText));
        OnPropertyChanged(nameof(HasLyricsMatch));
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
        SendText(text, rotating: true, pauseLyrics: false);
    }

    private void SendText(string text, bool rotating = false, bool pauseLyrics = true)
    {
        text = ExpandTokens(text);
        try
        {
            _osc.SendChatbox(text);
            _lastOscSend = DateTime.UtcNow;
            _lastVrchatLyric = null;
            var preview = text.Length > 48 ? text[..48] + "…" : text;
            StatusText = rotating
                ? $"Sent rotation message: {preview}"
                : $"Sent: {preview}";

            if (_showLyricsOnVrchat && _pauseLyricsAfterMessage)
            {
                var kind = pauseLyrics && !rotating
                    ? ChatboxMessageKind.Manual
                    : rotating
                        ? ChatboxMessageKind.Rotation
                        : ChatboxMessageKind.Lyric;
                _lyricsPause.OnMessageSent(kind, _lyricsPauseSeconds, DateTime.UtcNow);
                OnPropertyChanged(nameof(IsLyricsPaused));

                if (kind == ChatboxMessageKind.Manual)
                {
                    StatusText += $" Lyrics paused for {_lyricsPauseSeconds}s.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Could not send OSC: {ex.Message}";
        }
    }

    private string ExpandTokens(string text)
    {
        if (text.Contains("{music}", StringComparison.OrdinalIgnoreCase))
        {
            var track = CurrentTrack?.ChatboxText ?? "♪ Nothing playing";
            text = text.Replace("{music}", track, StringComparison.OrdinalIgnoreCase);
        }

        if (text.Contains("{lyrics}", StringComparison.OrdinalIgnoreCase))
        {
            var line = GetCurrentLyricLine(advancePlain: true) ??
                       CurrentTrack?.ChatboxText ??
                       "♪ Nothing playing";
            text = text.Replace("{lyrics}", line, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private TimeSpan EstimatedPosition() => _position.Estimate(CurrentTrack, DateTime.UtcNow);

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
        _nowPlayingTimer.Stop();
        _lyricsDisplayTimer.Stop();
        _lyricsCancellation?.Cancel();
        _lyricsCancellation?.Dispose();
        if (_osc is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

internal enum ChatboxMessageKind
{
    Manual,
    Rotation,
    Lyric
}

/// <summary>
/// Decides when automatic lyrics should yield to a manual chatbox message.
/// Rotation and explicit lyric sends deliberately do not pause the display.
/// </summary>
internal sealed class PlaybackPositionEstimator
{
    private string? _trackKey;
    private TimeSpan _reportedPosition;
    private TimeSpan _anchorPosition;
    private DateTime _anchorAt;

    public TimeSpan Update(NowPlayingInfo? track, DateTime now)
    {
        if (track is null)
        {
            _trackKey = null;
            _reportedPosition = TimeSpan.Zero;
            _anchorPosition = TimeSpan.Zero;
            _anchorAt = now;
            return TimeSpan.Zero;
        }

        var key = $"{track.Title}\u001f{track.Artist}";
        if (key != _trackKey || track.Position != _reportedPosition)
        {
            _trackKey = key;
            _reportedPosition = track.Position;
            _anchorPosition = track.Position;
            _anchorAt = now;
        }

        return Estimate(track, now);
    }

    public TimeSpan Estimate(NowPlayingInfo? track, DateTime now)
    {
        if (track is null)
        {
            return TimeSpan.Zero;
        }

        var elapsed = now - _anchorAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        var position = _anchorPosition + elapsed;
        if (track.Duration > TimeSpan.Zero && position > track.Duration)
        {
            position = track.Duration;
        }

        return position;
    }
}

internal sealed class LyricsPausePolicy
{
    public DateTime ResumeAt { get; private set; } = DateTime.MinValue;

    public bool IsPaused(DateTime now) => now < ResumeAt;

    public void OnMessageSent(ChatboxMessageKind kind, int pauseSeconds, DateTime now)
    {
        if (kind != ChatboxMessageKind.Manual)
        {
            return;
        }

        ResumeAt = now.AddSeconds(Math.Clamp(pauseSeconds, 5, 3600));
    }

    public void Resume() => ResumeAt = DateTime.MinValue;
}
