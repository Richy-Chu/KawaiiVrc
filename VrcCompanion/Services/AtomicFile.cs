using System.IO;

namespace VrcCompanion.Services;

/// <summary>
/// Stages a snapshot next to its destination and renames it into place.
/// The staging file shares delete so whoever clears the data directory while
/// the write is in flight can still remove it, instead of failing their delete
/// with a sharing violation.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var temporary = path + ".tmp";

        using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(contents);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
