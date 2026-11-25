using System;
using System.Buffers;
using System.IO;
using System.Text.Json;

namespace VrcCompanion.Services;

/// <summary>
/// Stages a snapshot next to its destination and renames it into place.
/// The staging file shares delete so whoever clears the data directory while
/// the write is in flight can still remove it, instead of failing their delete
/// with a sharing violation.
/// </summary>
internal static class AtomicFile
{
    public static void WriteAllBytes(string path, byte[] contents)
    {
        var temporary = path + ".tmp";

        using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete))
        {
            stream.Write(contents, 0, contents.Length);
            // A force-kill must not leave a half-written file behind the rename.
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Serializes straight into the staging file. Building the JSON as a string first costs a
    /// second, larger copy of the whole document - the activity store's ~6 MB file became an
    /// ~12 MB UTF-16 string plus a 6 MB byte array on every save, the largest source of
    /// large-object churn in the process.
    /// </summary>
    public static void WriteJson<T>(string path, T value, JsonSerializerOptions options)
    {
        var temporary = path + ".tmp";

        using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.Read | FileShare.Delete))
        {
            var sink = new StreamSink(stream);
            using (var writer = new Utf8JsonWriter(sink))
            {
                JsonSerializer.Serialize(writer, value, options);
            }

            sink.Flush();

            // A force-kill must not leave a half-written file behind the rename.
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Hands the writer's bytes to the file as they are produced. A plain <see cref="Utf8JsonWriter"/>
    /// over a stream still grows a buffer for the whole document, so a large store would keep paying
    /// for a copy of itself on the large object heap.
    /// </summary>
    private sealed class StreamSink : IBufferWriter<byte>
    {
        private const int BufferSize = 64 * 1024;

        private readonly Stream _stream;
        private byte[] _buffer = new byte[BufferSize];
        private int _written;

        public StreamSink(Stream stream) => _stream = stream;

        public void Advance(int count)
        {
            _written += count;
            if (_written == _buffer.Length)
            {
                Flush();
            }
        }

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsMemory(_written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Ensure(sizeHint);
            return _buffer.AsSpan(_written);
        }

        public void Flush()
        {
            if (_written == 0)
            {
                return;
            }

            _stream.Write(_buffer, 0, _written);
            _written = 0;
        }

        private void Ensure(int sizeHint)
        {
            if (sizeHint <= 0)
            {
                sizeHint = 1;
            }

            if (_buffer.Length - _written >= sizeHint)
            {
                return;
            }

            Flush();
            if (_buffer.Length < sizeHint)
            {
                _buffer = new byte[Math.Max(sizeHint, BufferSize)];
            }
        }
    }

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
