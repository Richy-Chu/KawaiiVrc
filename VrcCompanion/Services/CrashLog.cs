using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace VrcCompanion.Services;

/// <summary>
/// The file an unexpected failure is written to. A layout or render error can fire on every
/// measure pass, so a failure that repeats is written once per run and nothing is written to a
/// file already at the cap: an exception loop must not fill the user's disk. The generation
/// before the last one is kept as <c>crash.log.1</c>, so a crash that only reproduced once is
/// still readable after the next run.
/// </summary>
internal sealed class CrashLog
{
    internal const long DefaultMaxBytes = 1024 * 1024;

    private readonly object _sync = new();
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly HashSet<string> _written = new(StringComparer.Ordinal);

    internal CrashLog(string path, long maxBytes = DefaultMaxBytes)
    {
        _path = path;
        _maxBytes = maxBytes;
    }

    /// <summary>Where the newest entries land; <c>.1</c> beside it is the generation before.</summary>
    internal string Path => _path;

    /// <summary>
    /// Writes the failure unless this run already wrote the same one. False means nothing new
    /// was written - a repeat, or a log that could not be written - and neither is worth a second
    /// failure of its own.
    /// </summary>
    internal bool Write(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var signature = Signature(exception);
        lock (_sync)
        {
            if (!_written.Add(signature))
            {
                return false;
            }

            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                Rotate();
                if (Length() >= _maxBytes)
                {
                    // A log that could not be rotated - an editor holding it open, say - would
                    // otherwise grow without limit. Dropping the entry keeps the cap.
                    return false;
                }

                File.AppendAllText(
                    _path,
                    $"[{DateTimeOffset.Now:O}] pid={Environment.ProcessId}{Environment.NewLine}" +
                    exception + Environment.NewLine + Environment.NewLine);
                return true;
            }
            catch (Exception)
            {
                // Never let crash logging take down the app.
                return false;
            }
        }
    }

    /// <summary>
    /// Type, throwing method and message. The same failure on every pass repeats all three; a
    /// different failure changes at least one of them, so the log keeps one entry per kind of
    /// failure rather than one per occurrence.
    /// </summary>
    internal static string Signature(Exception exception)
    {
        var frame = new StackTrace(exception, fNeedFileInfo: false).GetFrame(0);
        var method = frame?.GetMethod();
        var site = method is null
            ? "?"
            : (method.DeclaringType?.FullName ?? "?") + "." + method.Name;
        return exception.GetType().FullName + "|" + site + "|" + exception.Message;
    }

    private void Rotate()
    {
        try
        {
            if (Length() < _maxBytes)
            {
                return;
            }

            File.Move(_path, _path + ".1", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The caller checks the length again and keeps the cap by dropping the entry.
        }
    }

    private long Length()
    {
        try
        {
            var file = new FileInfo(_path);
            return file.Exists ? file.Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return _maxBytes;
        }
    }
}
