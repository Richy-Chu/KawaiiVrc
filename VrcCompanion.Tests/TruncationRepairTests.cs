using System.Net.Http;
using System.Text.Json;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// A provider body that exceeds the byte budget is truncated mid-flight. These
/// tests pin the repair behaviour: the cut must never be mistaken for the end of
/// a record, and a partly-read index must still contribute its complete records
/// instead of vanishing.
/// </summary>
public sealed class TruncationRepairTests
{
    [Fact]
    public void ArrayBodyCutInsideStringWithBraces_KeepsOnlyCompleteRecords()
    {
        // The second record is cut inside "description", which itself contains a
        // brace pair; a brace-counting repair would treat that } as structure and
        // produce invalid JSON, dropping the whole index.
        const string body =
            "[{\"id\":\"avtr_1\",\"name\":\"ok\"}," +
            "{\"id\":\"avtr_2\",\"description\":\"brace } inside { text\",\"name\":\"half";

        var repaired = VrcApiClient.RepairTruncatedJson(body);

        Assert.NotNull(repaired);
        using var document = JsonDocument.Parse(repaired!);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Equal(1, document.RootElement.GetArrayLength());
        Assert.Equal("avtr_1", document.RootElement[0].GetProperty("id").GetString());
    }

    [Fact]
    public void ArrayBodyCutInsideStringWithEscapedQuoteAndBraces_StillParses()
    {
        const string body =
            "[{\"id\":\"avtr_a\"}," +
            "{\"id\":\"avtr_b\",\"description\":\"he said \\\"} and { already\\\"\",\"tags\":[\"x\"]}," +
            "{\"id\":\"avtr_c\",\"description\":\"cut inside } here";

        var repaired = VrcApiClient.RepairTruncatedJson(body);

        Assert.NotNull(repaired);
        using var document = JsonDocument.Parse(repaired!);
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal("avtr_b", document.RootElement[1].GetProperty("id").GetString());
    }

    [Fact]
    public void VrcndbObjectBodyCutMidArray_KeepsCompleteResults()
    {
        // VRCNDb answers {page, limit, results:[...]} instead of a bare array, so
        // the records sit one level deeper and the repair must close array+object.
        const string body =
            "{\"page\":1,\"limit\":25,\"total\":5000,\"results\":[" +
            "{\"id\":\"avtr_a\",\"name\":\"one\"}," +
            "{\"id\":\"avtr_b\",\"name\":\"two\"}," +
            "{\"id\":\"avtr_c\",\"name\":\"thr";

        var repaired = VrcApiClient.RepairTruncatedJson(body);

        Assert.NotNull(repaired);
        using var document = JsonDocument.Parse(repaired!);
        var results = document.RootElement.GetProperty("results");
        Assert.Equal(2, results.GetArrayLength());
        Assert.Equal("avtr_b", results[1].GetProperty("id").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("page").GetInt32());
    }

    [Fact]
    public async Task OversizedProviderBody_IsCappedButStillYieldsRecords()
    {
        // 2000 records with padded descriptions comfortably exceed the 400 KB
        // budget, mirroring VRCDB/WorldBalancer ignoring the requested row count.
        var payload = new System.Text.StringBuilder("[");
        for (var i = 0; i < 2000; i++)
        {
            if (i > 0)
            {
                payload.Append(',');
            }

            payload.Append($"{{\"id\":\"avtr_{i}\",\"name\":\"avatar {i}\",")
                .Append("\"description\":\"")
                .Append('x', 400)
                .Append("\"}");
        }

        payload.Append(']');
        var body = payload.ToString();
        Assert.True(body.Length > 400_000, $"fixture is only {body.Length} chars");

        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var limited = await VrcApiClient.ReadLimitedBodyAsync(content, CancellationToken.None);

        // The repair adds one closing bracket past the byte budget.
        Assert.True(
            limited.Length <= VrcApiClient.MaxProviderBytes + 1,
            $"reader returned {limited.Length} chars for a {VrcApiClient.MaxProviderBytes}-byte budget");
        using var document = JsonDocument.Parse(limited);
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.True(
            document.RootElement.GetArrayLength() > 1,
            "truncated body must still contribute complete records");
        Assert.Equal("avtr_0", document.RootElement[0].GetProperty("id").GetString());
    }

    [Fact]
    public void BodyCutBeforeAnyCompleteRecord_ReturnsNullSoItIsLogged()
    {
        const string body = "[{\"id\":\"avtr_a\",\"description\":\"never finishes";

        Assert.Null(VrcApiClient.RepairTruncatedJson(body));
    }
}
