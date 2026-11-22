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
        // Never replace a working session with nothing: a rotation that comes
        // back empty would otherwise sign the account out on the next launch.
        if (string.IsNullOrWhiteSpace(authCookie))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new StoredSession
            {
                AuthCookie = authCookie,
                SavedAt = DateTimeOffset.UtcNow
            });
            var encrypted = ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);
            // Staged and flushed before the rename, so a force-kill leaves either
            // the old session or the new one, never a truncated file.
            AtomicFile.WriteAllBytes(_path, encrypted);
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
            // Keep the file: a decrypt that failed once (a profile still loading,
            // a locked read) is not proof that the session is gone, and deleting
            // it would sign the account out for good.
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
