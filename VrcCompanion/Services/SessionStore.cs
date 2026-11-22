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
        : this(UserDataPaths.Directory)
    {
    }

    /// <summary>Test seam: keeps the suite away from the real profile.</summary>
    internal SessionStore(string directory)
    {
        _directory = directory;
        _path = Path.Combine(_directory, "session.dat");
    }

    public string FilePath => _path;

    /// <summary>Last cleared session, kept so an unexpected clear is recoverable.</summary>
    public string BackupPath => _path + ".bak";

    public bool HasBackup
    {
        get
        {
            try
            {
                return File.Exists(BackupPath);
            }
            catch
            {
                return false;
            }
        }
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
        catch (Exception ex)
        {
            // A failure to remember the session should never block sign-in, but
            // it must be visible: this is how an account ends up signed out on
            // the next launch.
            LifecycleLog.Write($"session: save failed {ex.GetType().Name}");
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
        catch (Exception ex)
        {
            // Keep the file: a decrypt that failed once (a profile still loading,
            // a locked read) is not proof that the session is gone, and deleting
            // it would sign the account out for good.
            LifecycleLog.Write($"session: unreadable file kept {ex.GetType().Name}");
            return null;
        }
    }

    public void Clear(string reason = "unspecified", bool keepBackup = true)
    {
        string? backup = null;
        try
        {
            if (File.Exists(_path) && keepBackup)
            {
                File.Copy(_path, BackupPath, overwrite: true);
                backup = BackupPath;
            }
            else if (!keepBackup && File.Exists(BackupPath))
            {
                File.Delete(BackupPath);
            }
        }
        catch (Exception ex)
        {
            LifecycleLog.Write($"session: backup before clear failed {ex.GetType().Name}");
        }

        LifecycleLog.Write(
            $"session: cleared ({reason}) backup={(backup is null ? "none" : backup)}");

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
