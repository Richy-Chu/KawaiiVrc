using System.Collections.Generic;
using System.Text.Json;
using VrcCompanion.Models;
using Xunit;

namespace VrcCompanion.Tests;

public class GroupInstanceTests
{
    [Fact]
    public void DeserializesWorldAndMembersForAGroupInstance()
    {
        const string json = """
        [
          {
            "instanceId": "12345~group(grp_abc)",
            "location": "wrld_4432ea9b-729c-46e3-8eaf-846aa0a37fdd:12345~group(grp_abc)",
            "memberCount": 6,
            "world": {
              "name": "The Great Pug",
              "thumbnailImageUrl": "https://api.vrchat.cloud/api/1/image/file_abc/1/256"
            }
          }
        ]
        """;

        var instances = JsonSerializer.Deserialize<List<GroupInstance>>(json);

        var instance = Assert.Single(instances!);
        Assert.Equal("The Great Pug", instance.WorldName);
        Assert.Equal("6 people here", instance.UsersText);
        Assert.Equal("Group", instance.AccessText);
        Assert.True(instance.CanJoin);
        Assert.Contains("file_abc", instance.ThumbnailUrl, System.StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidOrMissingLocationsCannotBeJoined()
    {
        var instance = new GroupInstance { Location = "not-a-location" };

        Assert.False(instance.CanJoin);
        Assert.Equal("Unknown world", instance.WorldName);
        Assert.Equal("Public", instance.AccessText);
    }
}
