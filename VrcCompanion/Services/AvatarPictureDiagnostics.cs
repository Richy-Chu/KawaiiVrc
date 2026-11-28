using System;
using System.IO;
using System.Text;

namespace VrcCompanion.Services;

/// <summary>
/// Writes every avatar-picture decision to a small log beside the other app data:
/// which row, which avatar name and creator, which picture, and where that picture
/// came from. Nothing reads it; deleting it is always safe.
///
/// It exists because a row's picture can come from four different places (the
/// player's own VRChat record, this account's avatar history, a public index
/// record, or a cached same-named avatar), and a wrong picture cannot be told
/// apart from a right one by looking at the window. The log names the source for
/// every picture so a report of "this row shows somebody else's avatar" can be
/// checked against the record it came from.
/// </summary>
internal static class AvatarPictureDiagnostics
{
    public const string FileName = "avatar-pictures.log";

    private const long MaxBytes = 256 * 1024;

    private static readonly object Gate = new();

    public static string FilePath => UserDataPaths.Combine(FileName);

    /// <summary>
    /// Appends one decision. <paramref name="stage"/> names the code path
    /// (for example "activity-row", "history", "popup", "instance-row"),
    /// <paramref name="picture"/> is the URL the row draws and
    /// <paramref name="detail"/> says why that source was accepted or refused.
    /// </summary>
    public static void Record(
        string stage,
        string? userId,
        string? avatarName,
        string? author,
        string? picture,
        string? detail = null)
    {
        try
        {
            var text = new StringBuilder()
                .Append('[')
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("] ")
                .Append(stage)
                .Append(" user=")
                .Append(Clean(userId))
                .Append(" avatar=")
                .Append(Clean(avatarName))
                .Append(" by=")
                .Append(Clean(author))
                .Append(" pic=")
                .Append(PictureId(picture));

            if (!string.IsNullOrWhiteSpace(detail))
            {
                text.Append(" · ").Append(OneLine(detail));
            }

            text.AppendLine();

            lock (Gate)
            {
                var path = FilePath;
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Delete(path);
                }

                File.AppendAllText(path, text.ToString());
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"avatar picture diagnostics: {ex.Message}");
        }
    }

    /// <summary>
    /// The VRChat file the picture is served from, so two rows showing the same
    /// picture can be compared without printing whole URLs.
    /// </summary>
    public static string PictureId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "-";
        }

        return ImageUrlHelper.ExtractFileId(url) ?? "external";
    }

    private static string Clean(string? value)
    {
        var text = OneLine(value);
        return text.Length == 0 ? "-" : text.Length > 40 ? text[..40] : text;
    }

    private static string OneLine(string? value)
        => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
}
