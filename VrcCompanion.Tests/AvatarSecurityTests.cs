using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public sealed class AvatarSecurityTests
{
    [Fact]
    public void ProviderFailedScanStatusDisablesWearAndFavorite()
    {
        const string json = """
            [
              {
                "id": "avtr_security_failed",
                "name": "Blocked Avatar",
                "authorName": "Creator",
                "description": "Metadata from the index",
                "created_at": "2025-01-02T03:04:05Z",
                "platforms": [ "pc", "quest" ],
                "unityPackages": [
                  {
                    "platform": "standalonewindows",
                    "performanceRating": "Good",
                    "scanStatus": "failed"
                  }
                ]
              }
            ]
            """;

        var avatar = Assert.Single(VrcApiClient.ParseVrcxProviderBody(json, "avtr.icu", 10));

        Assert.True(avatar.HasSecurityCheckFailed);
        Assert.False(avatar.CanUse);
        Assert.Contains("security", avatar.SecurityCheckFailedHint, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Metadata from the index", avatar.Description);
        Assert.Equal(2025, avatar.CreatedAt.Year);
        Assert.Contains("PC", avatar.PlatformsText);
        Assert.Equal("failed", Assert.Single(avatar.UnityPackages).ScanStatus);
    }

    [Fact]
    public void UnscannedProviderRowRemainsUsable()
    {
        const string json = """
            [
              {
                "id": "avtr_security_pending",
                "name": "Waiting Avatar",
                "unityPackages": [ { "platform": "pc", "scanStatus": "unscanned" } ]
              }
            ]
            """;

        var avatar = Assert.Single(VrcApiClient.ParseVrcxProviderBody(json, "avtr.icu", 10));

        Assert.False(avatar.HasSecurityCheckFailed);
        Assert.True(avatar.CanUse);
        Assert.Equal(string.Empty, avatar.SecurityCheckFailedHint);
    }

    [Fact]
    public void OwnPrivateAvatarCanBeWorn()
    {
        var avatar = new Avatar
        {
            Id = "avtr_own_private",
            Name = "My Private Avatar",
            AuthorId = "usr_me",
            AuthorName = "Me",
            ReleaseStatus = "private",
            IsOwnedByCurrentUser = true
        };

        Assert.True(avatar.IsPrivate);
        Assert.True(avatar.CanUse);
        Assert.Contains("yours", avatar.PrivacyHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnotherUsersPrivateAvatarStaysLocked()
    {
        var avatar = new Avatar
        {
            Id = "avtr_their_private",
            Name = "Someone Else's Avatar",
            AuthorId = "usr_someone_else",
            ReleaseStatus = "private",
            IsOwnedByCurrentUser = false
        };

        Assert.False(avatar.CanUse);
        Assert.Contains("only the owner", avatar.PrivacyHint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderImpostorBundlesDoNotCountAsSupportedPlatforms()
    {
        const string json = """
            [
              {
                "id": "avtr_pc_with_impostors",
                "name": "PC Avatar",
                "unityPackages": [
                  { "platform": "standalonewindows", "variant": "standard" },
                  { "platform": "android", "variant": "impostor" },
                  { "platform": "ios", "variant": "impostor" }
                ]
              }
            ]
            """;

        var avatar = Assert.Single(VrcApiClient.ParseVrcxProviderBody(json, "avtr.icu", 10));

        Assert.Equal("PC", avatar.PlatformsText);
        Assert.Equal(3, avatar.UnityPackages.Count);
    }

    [Fact]
    public void ImpostorBundlesDoNotCountAsSupportedPlatforms()
    {
        var avatar = new Avatar
        {
            Id = "avtr_pc_with_impostors",
            Name = "PC Avatar",
            UnityPackages =
            {
                new AvatarUnityPackage { Platform = "standalonewindows", Variant = "standard" },
                new AvatarUnityPackage { Platform = "android", Variant = "impostor" },
                new AvatarUnityPackage { Platform = "ios", Variant = "impostor" }
            }
        };

        Assert.Equal(new[] { "PC" }, avatar.PlatformLabels);
        Assert.Equal("PC", avatar.PlatformsText);
        Assert.DoesNotContain("Quest", avatar.PlatformsText);
        Assert.DoesNotContain("iOS", avatar.PlatformsText);
    }

    [Fact]
    public void ContentTagsUseReadableLabels()
    {
        var avatar = new Avatar
        {
            Id = "avtr_content_tags",
            Name = "Tagged Avatar",
            Tags = { "content_sex", "content_adult", "content_gore", "author_tag_chill", "system_trust_trusted" }
        };

        Assert.Equal(
            new[] { "Sexual content", "Adult content", "Gore content", "chill" },
            avatar.TagLabels);
    }

    [Fact]
    public void BackfilledContentTagsAppearOnExistingAvatarCards()
    {
        var avatar = new Avatar
        {
            Id = "avtr_backfill_tags",
            Name = "Needs Tags",
            ReleaseStatus = "private"
        };

        Assert.False(avatar.HasTags);
        Assert.True(avatar.MergeTags(new[] { "content_sex", "content_adult" }));
        Assert.True(avatar.HasTags);
        Assert.Equal(new[] { "Sexual content", "Adult content" }, avatar.TagLabels);
        Assert.False(avatar.MergeTags(new[] { "content_sex", "content_adult" }));
    }

    [Fact]
    public void ProfileAvatarCardsExposeResolvedContentTagLabels()
    {
        var friendAvatar = new FriendAvatar();
        friendAvatar.ApplyDetails(new Avatar
        {
            Id = "avtr_profile_tags",
            Name = "Profile Avatar",
            Tags = { "content_sex", "content_adult" }
        });

        Assert.True(friendAvatar.HasTags);
        Assert.Equal(new[] { "Sexual content", "Adult content" }, friendAvatar.TagLabels);
    }

    [Fact]
    public void PlatformLabelsUseActualBundlesAndReplaceStaleWiderLists()
    {
        var avatar = new Avatar
        {
            Id = "avtr_platforms",
            Name = "Platform Avatar",
            Platform = { "pc", "quest", "ios" },
            UnityPackages =
            {
                new AvatarUnityPackage { Platform = "standalonewindows" },
                new AvatarUnityPackage { Platform = "pc" }
            }
        };

        Assert.True(avatar.HasPlatforms);
        Assert.Equal(new[] { "PC" }, avatar.PlatformLabels);
        Assert.Equal("PC", avatar.PlatformsText);

        avatar.MergeDetailsFrom(new Avatar
        {
            Id = avatar.Id,
            Platform = { "android" }
        });

        Assert.Equal("Quest", avatar.PlatformsText);
    }

    [Fact]
    public void MergedVrchatDetailsCarrySecurityFailure()
    {
        var avatar = new Avatar
        {
            Id = "avtr_security_merge",
            Name = "Community Row",
            SourceName = "PAW"
        };
        var details = new Avatar
        {
            Id = avatar.Id,
            Description = "VRChat description",
            UnityPackages =
            {
                new AvatarUnityPackage
                {
                    Platform = "standalonewindows",
                    PerformanceRating = "Medium",
                    ScanStatus = "failed"
                }
            }
        };

        avatar.MergeDetailsFrom(details);

        Assert.Equal("VRChat description", avatar.Description);
        Assert.True(avatar.HasSecurityCheckFailed);
        Assert.False(avatar.CanUse);
        Assert.Equal("PAW", avatar.SourceName);
    }
}
