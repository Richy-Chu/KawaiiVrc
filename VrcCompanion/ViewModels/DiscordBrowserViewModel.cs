using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// Discord's web app inside the shared browser. Sign-in stays on Discord's own
/// page. After sign-in the search box drives Discord's Quick Switcher and the
/// quick actions open Friends, Add Friend, or Settings.
/// </summary>
public sealed class DiscordBrowserViewModel : ViewModelBase, IEmbeddedBrowserHost, IEmbeddedBrowserAutomationHost
{
    public const string LoginUrl = "https://discord.com/login";
    public const string FriendsUrl = "https://discord.com/channels/@me";

    private const string AddFriendScript = """
        (() => {
          const nodes = Array.from(document.querySelectorAll('button, [role="button"], a, [role="tab"]'));
          const target = nodes.find(node => {
            const text = ((node.innerText || '') + ' ' + (node.getAttribute('aria-label') || '')).toLowerCase();
            return text.includes('add friend');
          });
          if (!target) return 'not-found';
          target.click();
          return 'clicked';
        })()
        """;

    private const string VoiceStateScript = """
        (() => {
          const clean = value => (value || '').replace(/\s+/g, ' ').trim();
          const panels = Array.from(document.querySelectorAll('[class*="connectionPanel"], [class*="voicePanel"], [class*="voiceConnected"]'));
          let panel = panels.find(node =>
            /voice connected/i.test(clean(node.innerText)) ||
            node.querySelector('[class*="voiceUser"]'));
          if (!panel) {
            panel = Array.from(document.querySelectorAll('div, section'))
              .filter(node => /voice connected/i.test(clean(node.innerText)))
              .sort((left, right) => clean(left.innerText).length - clean(right.innerText).length)[0];
          }
          if (!panel) return 'null';

          const users = Array.from(panel.querySelectorAll('[class*="voiceUser"]'));
          const nameNode = panel.querySelector('[class*="channelName"]') ||
            panel.querySelector('[class*="channel"] [class*="name"]');
          let channel = clean(nameNode && nameNode.innerText);
          if (!channel) {
            const labelled = Array.from(panel.querySelectorAll('[aria-label]')).find(node =>
              /voice connected/i.test(node.getAttribute('aria-label') || ''));
            channel = clean(labelled && labelled.getAttribute('aria-label'))
              .replace(/^voice connected\s*[-–—:]?\s*/i, '');
          }

          if (!channel) {
            channel = panel.innerText.split('\n').map(clean).find(line =>
              line.length > 0 &&
              line.length < 80 &&
              !/^voice connected$/i.test(line) &&
              !/^(disconnect|video|screen share|voice settings|settings)$/i.test(line)) || '';
          }

          const speaking = users.filter(node =>
            /speaking/i.test(String(node.className || '')) ||
            node.querySelector('[class*="speaking"]')).map(node => {
              const name = node.querySelector('[class*="username"], [class*="userName"], [class*="name"]');
              return clean(name && name.innerText) || clean(node.innerText).split(' ')[0];
            }).filter(Boolean);

          return JSON.stringify({
            channel: channel || 'Voice',
            count: users.length,
            speaking
          });
        })()
        """;

    private string _searchQuery = string.Empty;
    private bool _isBrowserOpen = true;
    private string _browserUrl = LoginUrl;
    private string _browserTitle = "Discord";
    private string _browserError = string.Empty;
    private string _downloadStatus = string.Empty;
    private bool _downloadStatusIsError;
    private string? _downloadedFilePath;
    private bool _isFocusMode;
    private string _actionStatus = "Sign in on Discord's own page. Search and quick actions unlock once Discord is ready.";
    private bool _actionStatusIsError;
    private bool _isLoginPage = true;
    private bool _isWorking;
    private IEmbeddedBrowserAutomation? _automation;

    public DiscordBrowserViewModel()
    {
        SearchCommand = new AsyncRelayCommand(SearchAsync, CanSearch);
        FriendsCommand = new AsyncRelayCommand(OpenFriendsAsync, CanUseDiscord);
        AddFriendCommand = new AsyncRelayCommand(AddFriendAsync, CanUseDiscord);
        SettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, CanUseDiscord);
        OpenDownloadFolderCommand = new RelayCommand(OpenDownloadFolder);
    }

    public event Action<string>? BrowserNavigationRequested;

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value ?? string.Empty))
            {
                SearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsBrowserOpen
    {
        get => _isBrowserOpen;
        private set => SetProperty(ref _isBrowserOpen, value);
    }

    public bool IsFocusMode
    {
        get => _isFocusMode;
        set => SetProperty(ref _isFocusMode, value);
    }

    public string BrowserUrl
    {
        get => _browserUrl;
        private set => SetProperty(ref _browserUrl, value);
    }

    public string BrowserTitle
    {
        get => _browserTitle;
        private set => SetProperty(ref _browserTitle, value);
    }

    public string BrowserError
    {
        get => _browserError;
        private set => SetProperty(ref _browserError, value);
    }

    public string DownloadStatus
    {
        get => _downloadStatus;
        private set => SetProperty(ref _downloadStatus, value);
    }

    public bool DownloadStatusIsError
    {
        get => _downloadStatusIsError;
        private set => SetProperty(ref _downloadStatusIsError, value);
    }

    public string ActionStatus
    {
        get => _actionStatus;
        private set => SetProperty(ref _actionStatus, value);
    }

    public bool ActionStatusIsError
    {
        get => _actionStatusIsError;
        private set => SetProperty(ref _actionStatusIsError, value);
    }

    /// <summary>True while Discord's sign-in or registration page is showing.</summary>
    public bool IsLoginPage
    {
        get => _isLoginPage;
        private set
        {
            if (SetProperty(ref _isLoginPage, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public bool CanOpenDownloadFolder => !string.IsNullOrWhiteSpace(_downloadedFilePath);

    public IEmbeddedBrowserAutomation? Automation
    {
        get => _automation;
        set
        {
            if (ReferenceEquals(_automation, value))
            {
                return;
            }

            _automation = value;
            OnPropertyChanged(nameof(Automation));
            RaiseCommandStates();
        }
    }

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand FriendsCommand { get; }

    public AsyncRelayCommand AddFriendCommand { get; }

    public AsyncRelayCommand SettingsCommand { get; }

    public RelayCommand OpenDownloadFolderCommand { get; }

    public void SetBrowserLocation(string url, string title)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            BrowserUrl = url;
            UpdateReadiness();
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            BrowserTitle = title;
        }
    }

    public void SetBrowserError(string message)
    {
        BrowserError = message;
        if (!string.IsNullOrWhiteSpace(message))
        {
            SetActionStatus(message, true);
        }
    }

    public void SetDownloadStatus(string message, bool isError = false, string? downloadedPath = null)
    {
        DownloadStatus = message;
        DownloadStatusIsError = isError;
        SetDownloadedFilePath(downloadedPath);
    }

    /// <summary>
    /// Reads the connected voice channel from the Discord page the user is
    /// already signed into. Returns null when the page is not ready, the user
    /// is not in a voice channel, or Discord's DOM has changed.
    /// </summary>
    public async Task<DiscordVoiceSnapshot?> ReadVoiceStateAsync()
    {
        if (Automation is not { } automation || IsLoginPage)
        {
            return null;
        }

        try
        {
            var result = await automation.ExecuteScriptAsync(VoiceStateScript);
            return DiscordVoiceSnapshot.TryParse(result);
        }
        catch
        {
            return null;
        }
    }

    private bool CanSearch()
        => !_isWorking &&
           !IsLoginPage &&
           Automation is not null &&
           !string.IsNullOrWhiteSpace(SearchQuery);

    private bool CanUseDiscord()
        => !_isWorking && !IsLoginPage && Automation is not null;

    private async Task SearchAsync()
    {
        if (IsLoginPage)
        {
            SetActionStatus("Sign in to Discord first. The search works once the app is loaded.", true);
            return;
        }

        if (Automation is not { } automation || string.IsNullOrWhiteSpace(SearchQuery))
        {
            return;
        }

        var query = SearchQuery.Trim();
        await RunAsync(async () =>
        {
            await automation.SendKeyAsync("k", "KeyK", 75, control: true);
            await Task.Delay(350);
            await automation.InsertTextAsync(query);
            SetActionStatus("Quick Switcher opened. Choose a friend, server, or channel.");
        }, "Opening Discord Quick Switcher...");
    }

    private async Task OpenFriendsAsync()
    {
        if (Automation is null)
        {
            return;
        }

        await RunAsync(() =>
        {
            SetExpectedLocation(FriendsUrl, "Friends");
            BrowserNavigationRequested?.Invoke(FriendsUrl);
            SetActionStatus("Friends opened.");
            return Task.CompletedTask;
        }, "Opening Friends...");
    }

    private async Task AddFriendAsync()
    {
        if (Automation is not { } automation)
        {
            return;
        }

        await RunAsync(async () =>
        {
            SetExpectedLocation(FriendsUrl, "Friends");
            BrowserNavigationRequested?.Invoke(FriendsUrl);
            await Task.Delay(1200);
            var result = await automation.ExecuteScriptAsync(AddFriendScript);
            SetActionStatus(
                result.Contains("clicked", StringComparison.OrdinalIgnoreCase)
                    ? "Add Friend opened."
                    : "Friends opened. Click Add Friend in Discord if it did not open automatically.");
        }, "Opening Add Friend...");
    }

    private async Task OpenSettingsAsync()
    {
        if (Automation is not { } automation)
        {
            return;
        }

        var query = SearchQuery.Trim();
        await RunAsync(async () =>
        {
            await automation.SendKeyAsync(",", "Comma", 188, control: true);
            await Task.Delay(650);

            if (query.Length == 0)
            {
                SetActionStatus("Settings opened.");
                return;
            }

            var result = await automation.ExecuteScriptAsync(BuildSettingsSearchScript(query));
            SetActionStatus(
                result.Contains("filled", StringComparison.OrdinalIgnoreCase)
                    ? "Settings opened with your search filled in."
                    : "Settings opened. Use its search box to find a setting.");
        }, "Opening Settings...");
    }

    private async Task RunAsync(Func<Task> action, string workingMessage)
    {
        if (_isWorking)
        {
            return;
        }

        _isWorking = true;
        RaiseCommandStates();
        SetActionStatus(workingMessage);
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            SetActionStatus(
                "Discord did not accept that action. Make sure you are signed in and the page has finished loading.",
                true);
        }
        finally
        {
            _isWorking = false;
            RaiseCommandStates();
        }
    }

    private void SetExpectedLocation(string url, string title)
    {
        BrowserUrl = url;
        BrowserTitle = title;
        UpdateReadiness();
    }

    private void UpdateReadiness()
    {
        var loginPage = IsLoginUrl(BrowserUrl);
        if (loginPage == IsLoginPage)
        {
            return;
        }

        IsLoginPage = loginPage;
        SetActionStatus(loginPage
            ? "Sign in on Discord's own page. Search and quick actions unlock once Discord is ready."
            : "Discord is ready. Search friends, servers, or settings.");
    }

    private static bool IsLoginUrl(string url)
        => url.Contains("discord.com/login", StringComparison.OrdinalIgnoreCase) ||
           url.Contains("discord.com/register", StringComparison.OrdinalIgnoreCase);

    internal static string BuildSettingsSearchScript(string query)
    {
        var json = JsonSerializer.Serialize(query);
        return $$"""
            (() => {
              const query = {{json}};
              const input = Array.from(document.querySelectorAll('input')).find(candidate =>
                /search/i.test(candidate.getAttribute('placeholder') || '') ||
                /search/i.test(candidate.getAttribute('aria-label') || ''));
              if (!input) return 'not-found';
              input.focus();
              const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
              setter.call(input, query);
              input.dispatchEvent(new Event('input', { bubbles: true }));
              return 'filled';
            })()
            """;
    }

    private void SetActionStatus(string message, bool isError = false)
    {
        ActionStatus = message;
        ActionStatusIsError = isError;
    }

    private void RaiseCommandStates()
    {
        SearchCommand.RaiseCanExecuteChanged();
        FriendsCommand.RaiseCanExecuteChanged();
        AddFriendCommand.RaiseCanExecuteChanged();
        SettingsCommand.RaiseCanExecuteChanged();
    }

    private void SetDownloadedFilePath(string? path)
    {
        if (string.Equals(_downloadedFilePath, path, StringComparison.Ordinal))
        {
            return;
        }

        _downloadedFilePath = path;
        OnPropertyChanged(nameof(CanOpenDownloadFolder));
    }

    private void OpenDownloadFolder()
    {
        if (!LaunchService.OpenFolder(_downloadedFilePath))
        {
            SetDownloadStatus("The downloaded file and its folder are no longer available.", true);
        }
    }
}
