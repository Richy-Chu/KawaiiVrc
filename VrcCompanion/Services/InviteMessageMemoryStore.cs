using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Services;

/// <summary>
/// Remembers the last custom message successfully written for each account and
/// message type. VRChat starts a 60-minute edit cooldown after each write, so
/// re-writing the same text after a relaunch would only earn a 429.
/// </summary>
public sealed class InviteMessageMemoryStore
{
    public static InviteMessageMemoryStore Shared { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _sync = new();
    private readonly string _path;

    public InviteMessageMemoryStore(string? path = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? DefaultPath() : path;
    }

    public string? Get(string? accountId, string messageType)
        => GetEntry(accountId, messageType)?.Text;

    public InviteMessageMemory? GetEntry(string? accountId, string messageType)
    {
        if (string.IsNullOrWhiteSpace(accountId) || string.IsNullOrWhiteSpace(messageType))
        {
            return null;
        }

        lock (_sync)
        {
            var file = LoadFile();
            return file.Accounts.TryGetValue(accountId, out var account) &&
                   account.Messages.TryGetValue(messageType, out var message)
                ? new InviteMessageMemory(message.Text, Math.Clamp(message.Slot, 0, 11))
                : null;
        }
    }

    public void Set(string? accountId, string messageType, string? text, int slot = 11)
    {
        if (string.IsNullOrWhiteSpace(accountId) ||
            string.IsNullOrWhiteSpace(messageType) ||
            string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        lock (_sync)
        {
            try
            {
                var file = LoadFile();
                if (!file.Accounts.TryGetValue(accountId, out var account))
                {
                    account = new StoredAccount();
                    file.Accounts[accountId] = account;
                }

                account.Messages[messageType] = new StoredMessage
                {
                    Text = text.Trim(),
                    Slot = Math.Clamp(slot, 0, 11),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                WriteFile(file);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"invite message memory save {_path}: {ex.Message}");
            }
        }
    }

    private static string DefaultPath() => UserDataPaths.Combine("invite-messages.json");

    private StoredFile LoadFile()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new StoredFile();
            }

            var file = JsonSerializer.Deserialize<StoredFile>(File.ReadAllText(_path), JsonOptions);
            if (file is null)
            {
                return new StoredFile();
            }

            file.Accounts ??= new Dictionary<string, StoredAccount>(StringComparer.Ordinal);
            return file;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"invite message memory load {_path}: {ex.Message}");
            return new StoredFile();
        }
    }

    private void WriteFile(StoredFile file)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(file, JsonOptions));
    }

    private sealed class StoredFile
    {
        [JsonPropertyName("accounts")]
        public Dictionary<string, StoredAccount> Accounts { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredAccount
    {
        [JsonPropertyName("messages")]
        public Dictionary<string, StoredMessage> Messages { get; set; } = new(StringComparer.Ordinal);
    }

    private sealed class StoredMessage
    {
        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("slot")]
        public int Slot { get; set; } = 11;

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}

public sealed record InviteMessageMemory(string Text, int Slot);
