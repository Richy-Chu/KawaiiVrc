using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Input;
using VrcCompanion.Models;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>One stored account shown in the Accounts section.</summary>
public sealed class AccountEntry
{
    public AccountEntry(string id, string displayName, bool isActive)
    {
        Id = id;
        DisplayName = displayName;
        IsActive = isActive;
        Initial = string.IsNullOrWhiteSpace(displayName)
            ? "?"
            : char.ToUpperInvariant(displayName.Trim()[0]).ToString();
    }

    public string Id { get; }
    public string DisplayName { get; }
    public bool IsActive { get; }

    /// <summary>A friendly avatar initial; the raw account id stays internal.</summary>
    public string Initial { get; }
}

/// <summary>
/// View model for multi-account management. Stored sessions live in
/// <see cref="MultiAccountService"/>; switching hands the cookie to the
/// signed-in <see cref="VrcApiClient"/> so the rest of the app follows.
/// </summary>
public sealed class MultiAccountViewModel : ViewModelBase
{
    private readonly MultiAccountService _accountService;
    private readonly VrcApiClient _api;

    private ObservableCollection<AccountEntry> _accounts = new();
    private string? _errorMessage;
    private bool _isBusy;

    public MultiAccountViewModel(VrcApiClient api)
        : this(api, new MultiAccountService())
    {
    }

    /// <summary>Takes the store, so tests never read the real profile's accounts.</summary>
    internal MultiAccountViewModel(VrcApiClient api, MultiAccountService accountService)
    {
        _api = api;
        _accountService = accountService;

        LoadAccountsCommand = new RelayCommand(RefreshAccounts);
        SwitchAccountCommand = new AsyncRelayCommand<string>(
            SwitchAccountAsync,
            id => !IsBusy && !string.IsNullOrWhiteSpace(id));
        // Always enabled: SaveCurrentAccount explains when nobody is signed in.
        AddAccountCommand = new RelayCommand(SaveCurrentAccount);
        RemoveAccountCommand = new RelayCommand<string>(
            RemoveAccount,
            id => !string.IsNullOrWhiteSpace(id));

        RefreshAccounts();
    }

    /// <summary>Raised after a stored account's session replaces the current one.</summary>
    public event Action<VrcUser>? SessionSwitched;

    public ObservableCollection<AccountEntry> Accounts
    {
        get => _accounts;
        set => SetProperty(ref _accounts, value);
    }

    /// <summary>Whether the sign-in screen has a saved account to offer.</summary>
    public bool HasAccounts => Accounts.Count > 0;

    public string ActiveAccountLabel
    {
        get
        {
            // The header and the API session are the source of truth. A stale
            // active.json must not make the Accounts page claim a different
            // user is signed in than the rest of the app.
            if (_api.CurrentUser is { } signedIn && !string.IsNullOrWhiteSpace(signedIn.DisplayName))
            {
                return signedIn.DisplayName;
            }

            var active = _accountService.ActiveAccountId;
            if (string.IsNullOrWhiteSpace(active))
            {
                return "(none)";
            }

            return _accountService.GetAccountDisplayName(active);
        }
    }

    public string ActiveAccountInitial
    {
        get
        {
            var name = ActiveAccountLabel.Trim();
            return name.Length == 0 || name == "(none)"
                ? "?"
                : char.ToUpperInvariant(name[0]).ToString();
        }
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public ICommand LoadAccountsCommand { get; }
    public ICommand SwitchAccountCommand { get; }
    public ICommand AddAccountCommand { get; }
    public ICommand RemoveAccountCommand { get; }

    /// <summary>
    /// Stores the session of a freshly signed-in user so the account can be
    /// switched back to later. Only the auth cookie is written to disk.
    /// </summary>
    public void RememberCurrentAccount(VrcUser user)
    {
        var cookie = _api.CurrentAuthCookie;
        if (string.IsNullOrWhiteSpace(user.Id) || string.IsNullOrWhiteSpace(cookie))
        {
            return;
        }

        _accountService.RegisterAccount(user.Id, cookie, user.DisplayName);
        _accountService.SwitchToAccount(user.Id);
        RefreshAccounts();
    }

    private void SaveCurrentAccount()
    {
        ErrorMessage = null;

        var user = _api.CurrentUser;
        if (user is null)
        {
            ErrorMessage = "Sign in before saving an account.";
            return;
        }

        if (string.IsNullOrWhiteSpace(_api.CurrentAuthCookie))
        {
            ErrorMessage = "Could not read the current VRChat session token. Sign in again before saving this account.";
            return;
        }

        RememberCurrentAccount(user);
    }

    private async Task SwitchAccountAsync(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        ErrorMessage = null;

        IsBusy = true;
        try
        {
            var cookie = _accountService.GetAuthCookie(accountId);
            if (string.IsNullOrWhiteSpace(cookie))
            {
                ErrorMessage = "That account has no saved session. Sign in with it and use \"Save current account\".";
                return;
            }

            // Only skip the network round-trip when the signed-in user and the
            // live auth cookie both already belong to this account. Matching
            // only the user ID can leave a stale cookie from another account in
            // place and make Switch appear to do nothing.
            if (string.Equals(_api.CurrentUser?.Id, accountId, StringComparison.Ordinal) &&
                string.Equals(_api.CurrentAuthCookie, cookie, StringComparison.Ordinal))
            {
                _accountService.SwitchToAccount(accountId);
                RefreshAccounts();
                return;
            }

            var user = await _api.SwitchSessionAsync(cookie);
            if (user is null)
            {
                ErrorMessage = "That saved session has expired. Sign in with that account again to refresh it.";
                return;
            }

            // VRChat may rotate the cookie during the switch. Store the fresh
            // one against the account VRChat actually returned, not merely the
            // row that was clicked.
            _accountService.RegisterAccount(
                user.Id,
                _api.CurrentAuthCookie ?? cookie,
                user.DisplayName);
            _accountService.SwitchToAccount(user.Id);
            RefreshAccounts();
            SessionSwitched?.Invoke(user);
        }
        catch (VrcApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (HttpRequestException)
        {
            ErrorMessage = "Could not reach VRChat. Check your internet connection.";
        }
        catch (TaskCanceledException)
        {
            ErrorMessage = "Switching accounts timed out. Try again.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not switch accounts.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RemoveAccount(string? accountId)
    {
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            _accountService.RemoveAccount(accountId);
            RefreshAccounts();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            ErrorMessage = "Could not remove that account.";
        }
    }

    /// <summary>Re-reads the stored accounts after a sign-out or a save.</summary>
    internal void Refresh() => RefreshAccounts();

    private void RefreshAccounts()
    {
        var active = _accountService.ActiveAccountId;
        Accounts = new ObservableCollection<AccountEntry>(
            _accountService.AccountIds.Select(id => new AccountEntry(
                id,
                _accountService.GetAccountDisplayName(id),
                string.Equals(id, active, StringComparison.Ordinal))));
        OnPropertyChanged(nameof(ActiveAccountLabel));
        OnPropertyChanged(nameof(ActiveAccountInitial));
        OnPropertyChanged(nameof(HasAccounts));
    }
}
