using System;
using System.IO;
using System.Text;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A refresh re-read the newest VRChat logs in full, with three regexes per line, on the caller's
/// thread: with logs of tens of megabytes it froze the window for seconds every half minute. The
/// read is capped at <see cref="VrChatLogAvatarService.TailBytes"/> per file now, which these
/// cases decide without a clock: a switch behind the cap must not be seen, one inside it must be.
/// </summary>
[Collection(Seams.Collection)]
[ResetSeams]
public sealed class VrChatLogAvatarServiceTests
{
    [Fact]
    public void ARefreshReadsOnlyTheTailAndResolvesTheAvatarById()
    {
        var directory = VrcLogPaths.Directory;
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "output_log_" + Guid.NewGuid().ToString("N") + ".txt");
        var earlyId = "usr_" + Guid.NewGuid().ToString("D");
        var lateId = "usr_" + Guid.NewGuid().ToString("D");

        var log = new StringBuilder();
        log.AppendLine($"[Behaviour] OnPlayerJoined Early ({earlyId})");
        log.AppendLine("[Behaviour] Switching Early to avatar EarlyAvatar");
        // Push those two lines outside the cap the reader keeps from the end of the file.
        while (log.Length < VrChatLogAvatarService.TailBytes + 512 * 1024)
        {
            log.AppendLine("2026-11-24 12:00:00 Notifications: noise that is neither a join nor a switch");
        }

        log.AppendLine($"[Behaviour] OnPlayerJoined Late ({lateId})");
        log.AppendLine("[AssetBundleDownloadManager] [1] Unpacking Avatar (LateAvatar by SomeAuthor)");
        log.AppendLine("[Behaviour] Switching Late to avatar LateAvatar");

        try
        {
            File.WriteAllText(path, log.ToString());

            var service = new VrChatLogAvatarService();

            var late = service.GetAvatar(lateId, "Late");
            Assert.NotNull(late);
            Assert.Equal("LateAvatar", late!.Name);
            Assert.Equal("SomeAuthor", late.AuthorName);

            // The same switch read by name, and the one behind the cap that was never read.
            Assert.Equal("LateAvatar", service.GetAvatar(null, "Late")?.Name);
            Assert.Null(service.GetAvatar(earlyId, "Early"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
