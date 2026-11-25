using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using VrcCompanion.Services;
using Xunit;
using Xunit.Abstractions;

namespace VrcCompanion.Tests;

public class AtomicFileJsonTests
{
    private readonly ITestOutputHelper _output;

    public AtomicFileJsonTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void WritingAJsonStoreDoesNotBuildTheWholeDocumentInMemory()
    {
        // A store the size of a long-lived activity feed: about 2 MB of JSON.
        var payload = new Payload
        {
            Rows = Enumerable.Range(0, 20_000).Select(_ => new string('x', 100)).ToList()
        };
        var options = new JsonSerializerOptions();
        var path = Path.Combine(Path.GetTempPath(), $"atomic-json-{Guid.NewGuid():N}.json");

        try
        {
            long documentBytes;
            using (var probe = new MemoryStream())
            {
                using var writer = new Utf8JsonWriter(probe);
                JsonSerializer.Serialize(writer, payload, options);
                documentBytes = probe.Length;
            }

            // Warm the serializer's metadata cache so the measurement is the write, not type loading.
            AtomicFile.WriteJson(path, payload, options);

            var before = GC.GetTotalAllocatedBytes(precise: true);
            AtomicFile.WriteJson(path, payload, options);
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

            _output.WriteLine(
                $"document {documentBytes:N0} bytes, write allocated {allocated:N0} bytes");
            // The old path serialized the whole document into a string and then copied it into a
            // byte[] - about three times the document size, all on the large object heap.
            Assert.True(
                allocated < documentBytes / 2,
                $"writing a {documentBytes:N0} byte document allocated {allocated:N0} bytes");

            Assert.False(File.Exists(path + ".tmp"), "the staging file should be renamed away");
            var text = File.ReadAllText(path);
            Assert.StartsWith("{\"Rows\":[\"", text, StringComparison.Ordinal);
            Assert.EndsWith("\"]}", text, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class Payload
    {
        public List<string> Rows { get; set; } = new();
    }
}
