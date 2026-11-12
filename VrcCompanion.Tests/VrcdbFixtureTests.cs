using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// VRCDB ignores the requested row count: this saved live response for "cat"
/// (n=80) contains 1000 records and 461 KB, so the byte budget always cuts it.
/// The repair must keep the response usable instead of dropping the index.
/// </summary>
public sealed class VrcdbFixtureTests
{
    [Fact]
    public async Task SavedVrcdbResponse_IsCutByTheByteBudgetAndStillParses()
    {
        // Trimmed to the smallest prefix of the live "cat" response that still
        // exceeds the byte budget (861 of the original 1000 records) and stored
        // gzipped, so the repo does not carry the 400 KB body as text.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "vrcdb-search-cat.json.gz");
        await using var file = File.OpenRead(path);
        await using var gzip = new System.IO.Compression.GZipStream(
            file, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();

        // The cap is a byte budget, and avatar names can be multi-byte, so compare
        // UTF-8 bytes rather than string length.
        var bodyBytes = Encoding.UTF8.GetByteCount(body);
        Assert.True(
            bodyBytes > VrcApiClient.MaxProviderBytes,
            $"fixture is {bodyBytes} bytes, which would not exercise the budget");

        using var original = JsonDocument.Parse(body);
        var originalCount = original.RootElement.GetArrayLength();
        Assert.True(originalCount > 1, "fixture needs more than one record to prove a prefix survives");

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        var limited = await VrcApiClient.ReadLimitedBodyAsync(content, CancellationToken.None);

        var limitedBytes = Encoding.UTF8.GetByteCount(limited);
        Assert.True(
            limitedBytes <= VrcApiClient.MaxProviderBytes + 1,
            $"reader returned {limitedBytes} bytes for a {VrcApiClient.MaxProviderBytes}-byte budget");

        using var repaired = JsonDocument.Parse(limited);
        var records = repaired.RootElement;
        Assert.Equal(JsonValueKind.Array, records.ValueKind);

        var count = records.GetArrayLength();
        Assert.True(count > 0, "a truncated VRCDB response must still contribute records");
        Assert.True(count < originalCount, "the budget should have cut the saved response short");

        // No half-written record may survive the cut.
        foreach (var record in records.EnumerateArray())
        {
            Assert.True(record.TryGetProperty("id", out var id));
            Assert.StartsWith("avtr_", id.GetString());
        }
    }
}
