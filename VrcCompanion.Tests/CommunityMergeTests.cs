using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The federated search merges six indexes. The same avatar is often listed by
/// several of them, so a duplicate must collapse to one card carrying exactly one
/// source label rather than appearing twice or claiming two indexes.
/// </summary>
public sealed class CommunityMergeTests
{
    [Fact]
    public void DuplicateIdFromTwoIndexes_AppearsOnceWithSingleSourceLabel()
    {
        var shared = "avtr_11111111-1111-1111-1111-111111111111";
        var onlySecond = "avtr_22222222-2222-2222-2222-222222222222";

        var pages = new[]
        {
            ("AviSeek", (IReadOnlyList<Avatar>)new List<Avatar>
            {
                new() { Id = shared, Name = "Shared avatar from AviSeek" }
            }),
            ("VRCDB", (IReadOnlyList<Avatar>)new List<Avatar>
            {
                new() { Id = shared, Name = "Shared avatar from VRCDB" },
                new() { Id = onlySecond, Name = "VRCDB only" }
            })
        };

        var merged = VrcApiClient.MergeCommunityResults(pages);

        Assert.Equal(2, merged.Count);
        var duplicate = Assert.Single(merged, avatar => avatar.Id == shared);
        Assert.Equal("AviSeek", duplicate.SourceName);
        Assert.Equal("Shared avatar from AviSeek", duplicate.Name);

        var unique = Assert.Single(merged, avatar => avatar.Id == onlySecond);
        Assert.Equal("VRCDB", unique.SourceName);
    }

    [Fact]
    public void RowsWithoutAnAvatarId_AreDropped()
    {
        var pages = new[]
        {
            ("PAW", (IReadOnlyList<Avatar>)new List<Avatar>
            {
                new() { Id = string.Empty, Name = "no id" },
                new() { Id = "avtr_33333333-3333-3333-3333-333333333333", Name = "valid" }
            })
        };

        var merged = VrcApiClient.MergeCommunityResults(pages);

        var only = Assert.Single(merged);
        Assert.Equal("valid", only.Name);
        Assert.Equal("PAW", only.SourceName);
    }
}
