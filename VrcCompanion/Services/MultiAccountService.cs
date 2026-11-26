using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Manages multiple VRChat accounts with encrypted session storage.
/// Accounts are stored as JSON under %LOCALAPPDATA%\\VrcCompanion\\accounts\\.
/// Only auth cookies are stored (never passwords).
/// </summary>
public sealed class MultiAccountService : IDisposable
{
    private const string AccountsDirectoryName = "accounts";
    private const string ActiveAccountFile = "active.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VrcCompanion.Account.v1");

    private readonly string _accountsDirectory;
    private readonly SessionStore _sessionStore = new();
    private string? _activeAccountId;

    public MultiAccountService()
        : this(Path.Combine(UserDataPaths.Directory, AccountsDirectoryName))
    {
    }

    /// <summary>
    /// Points the store at another folder, so a test can read saved accounts
    /// without touching the real profile's accounts folder.
    /// </summary>
    internal MultiAccountService(string accountsDirectory)
    {
        _accountsDirectory = accountsDirectory;
        try
        {
            Directory.CreateDirectory(_accountsDirectory);
            LoadActiveAccount();
        }
        catch
        {
            // Read-only environments are fine; account management simply won't persist.
        }
    }

    /// <summary>
    /// All registered account IDs.
    /// </summary>
    public List<string> AccountIds { get; } = new();

    /// <summary>
    /// The currently active account ID, if any.
    /// </summary>
    public string? ActiveAccountId
    {
        get => _activeAccountId;
        set
        {
            _activeAccountId = value;
            SaveActiveAccount();
        }
    }

    /// <summary>
    /// Get the display name for an account (from cached profile).
    /// </summary>
    public string GetAccountDisplayName(string accountId)
    {
        var profile = LoadAccountProfile(accountId);
        return string.IsNullOrWhiteSpace(profile?.DisplayName) ? accountId : profile.DisplayName;
    }

    /// <summary>
    /// Register a new account with its auth cookie.
    /// </summary>
    public void RegisterAccount(string accountId, string authCookie, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(authCookie))
            return;

        if (!AccountIds.Contains(accountId))
            AccountIds.Add(accountId);

        SaveAccountProfile(accountId, new StoredAccountProfile
        {
            AccountId = accountId,
            DisplayName = displayName ?? accountId,
            RegisteredAt = DateTimeOffset.UtcNow
        });

        SaveAccountCookie(accountId, authCookie);
    }

    /// <summary>
    /// Remove an account and its stored data.
    /// </summary>
    public void RemoveAccount(string accountId)
    {
        AccountIds.Remove(accountId);
        if (_activeAccountId == accountId)
            _activeAccountId = null;

        try
        {
            var profilePath = Path.Combine(_accountsDirectory, $"{accountId}.json");
            if (File.Exists(profilePath))
                File.Delete(profilePath);

            var cookiePath = Path.Combine(_accountsDirectory, $"{accountId}.cookie");
            if (File.Exists(cookiePath))
                File.Delete(cookiePath);
        }
        catch { /* Best effort */ }

        SaveActiveAccount();
    }

    /// <summary>Reads an account's auth cookie, decrypting it with Windows DPAPI.</summary>
    public string? GetAuthCookie(string accountId)
    {
        try
        {
            var path = Path.Combine(_accountsDirectory, $"{accountId}.cookie");
            if (!File.Exists(path))
                return null;

            var encrypted = File.ReadAllBytes(path);
            var payload = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var value = Encoding.UTF8.GetString(payload);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Stores an account's auth cookie encrypted with Windows DPAPI.</summary>
    public void SaveAccountCookie(string accountId, string authCookie)
    {
        try
        {
            var path = Path.Combine(_accountsDirectory, $"{accountId}.cookie");
            Directory.CreateDirectory(_accountsDirectory);
            var encrypted = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(authCookie),
                Entropy,
                DataProtectionScope.CurrentUser);
            AtomicFile.WriteAllBytes(path, encrypted);
        }
        catch { /* Best effort */ }
    }

    /// <summary>
    /// Switch to a different account (sets it as active).
    /// </summary>
    public void SwitchToAccount(string accountId)
    {
        if (!AccountIds.Contains(accountId))
            return;

        ActiveAccountId = accountId;
    }

    private void LoadActiveAccount()
    {
        try
        {
            var path = Path.Combine(_accountsDirectory, ActiveAccountFile);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var data = JsonSerializer.Deserialize<ActiveAccountData>(json, JsonOptions);
                if (data is not null)
                {
                    _activeAccountId = data.ActiveAccountId;
                    AccountIds.AddRange(data.AccountIds ?? new List<string>());
                }
            }
        }
        catch
        {
            // Ignore corrupt active account file
        }
    }

    private void SaveActiveAccount()
    {
        try
        {
            var data = new ActiveAccountData
            {
                ActiveAccountId = _activeAccountId,
                AccountIds = new List<string>(AccountIds)
            };
            var path = Path.Combine(_accountsDirectory, ActiveAccountFile);
            Directory.CreateDirectory(_accountsDirectory);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(data, JsonOptions));
        }
        catch { /* Best effort */ }
    }

    private StoredAccountProfile? LoadAccountProfile(string accountId)
    {
        try
        {
            var path = Path.Combine(_accountsDirectory, $"{accountId}.json");
            if (!File.Exists(path))
                return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StoredAccountProfile>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private void SaveAccountProfile(string accountId, StoredAccountProfile profile)
    {
        try
        {
            var path = Path.Combine(_accountsDirectory, $"{accountId}.json");
            Directory.CreateDirectory(_accountsDirectory);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOptions));
        }
        catch { /* Best effort */ }
    }

    public void Dispose()
    {
        // No unmanaged resources; the underlying SessionStore is stateless for our purposes.
    }

    private sealed class StoredAccountProfile
    {
        [JsonPropertyName("accountId")]
        public string AccountId { get; set; } = string.Empty;

        [JsonPropertyName("displayName")]
        public string DisplayName { get; set; } = string.Empty;

        [JsonPropertyName("registeredAt")]
        public DateTimeOffset RegisteredAt { get; set; }
    }

    private sealed class ActiveAccountData
    {
        [JsonPropertyName("activeAccountId")]
        public string? ActiveAccountId { get; set; }

        [JsonPropertyName("accountIds")]
        public List<string>? AccountIds { get; set; }
    }
}