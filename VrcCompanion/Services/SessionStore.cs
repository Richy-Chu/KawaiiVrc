using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VrcCompanion.Services;

/// <summary>
/// Stores only the VRChat auth cookie, encrypted with Windows DPAPI for the
/// current Windows user. No username or password is ever written to disk.
/// </summary>
public sealed class SessionStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VrcCompanion.Session.v1");

    private readonly string _directory;
    private readonly string _path;

    public SessionStore()
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcCompanion");
        _path = Path.Combine(_directory, "session.dat");
    }

    public bool Exists
    {
        get
        {
            try
            {
                return File.Exists(_path);
            }
            catch
            {
                return false;
            }
        }
    }

    public void Save(string authCookie)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredSession
            {
                AuthCookie = authCookie,
                SavedAt = DateTimeOffset.UtcNow
            });
            var encrypted = ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_path, encrypted);
        }
        catch
        {
            // A failure to remember the session should never block sign-in.
        }
    }

    public string? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var encrypted = File.ReadAllBytes(_path);
            var payload = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<StoredSession>(payload);
            return string.IsNullOrWhiteSpace(session?.AuthCookie) ? null : session.AuthCookie;
        }
        catch
        {
            // Corrupt or inaccessible data: forget it.
            Clear();
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }
        }
        catch
        {
            // Best effort.
        }
    }

    private sealed class StoredSession
    {
        public string AuthCookie { get; set; } = string.Empty;
        public DateTimeOffset SavedAt { get; set; }
    }
}
