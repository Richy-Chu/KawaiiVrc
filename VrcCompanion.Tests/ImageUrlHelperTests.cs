using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class ImageUrlHelperTests
{
    [Fact]
    public void AvtrIcuProxyThumbnailUsesVrchatImageEndpoint()
    {
        var normalized = ImageUrlHelper.NormalizeCommunityImageUrl(
            "https://avtr.icu/proxy/file_027b3fb6-c9d2-4fce-9224-4cdac6676883/1/256");

        Assert.Equal(
            "https://api.vrchat.cloud/api/1/image/file_027b3fb6-c9d2-4fce-9224-4cdac6676883/1/256",
            normalized);
    }

    [Fact]
    public void AvtrIcuProxyFullImageUsesVrchatFileEndpoint()
    {
        var normalized = ImageUrlHelper.NormalizeCommunityImageUrl(
            "https://avtr.icu/proxy/file_d6b99b7b-da56-4c93-a951-209cec688e4a/1/file");

        Assert.Equal(
            "https://api.vrchat.cloud/api/1/file/file_d6b99b7b-da56-4c93-a951-209cec688e4a/1/file",
            normalized);
    }

    [Fact]
    public void OrdinaryCommunityImageUrlsAreUnchanged()
    {
        const string url = "https://db.vrcnext.com/thumbs/9f/avtr_23a53f41-81a5-4c25-a7b5-2e4d0d901dcf.webp";

        Assert.Equal(url, ImageUrlHelper.NormalizeCommunityImageUrl(url));
    }

    [Fact]
    public void VrchatProxyQueryIsUnwrapped()
    {
        var normalized = ImageUrlHelper.NormalizeCommunityImageUrl(
            "https://paw-api.amelia.fun/proxy?url=https://api.vrchat.cloud/api/1/image/file_dc1d3976-a130-4127-8ee2-76b4bbdf6cf8/1/256");

        Assert.Equal(
            "https://api.vrchat.cloud/api/1/image/file_dc1d3976-a130-4127-8ee2-76b4bbdf6cf8/1/256",
            normalized);
    }

    [Fact]
    public void AvatarThumbnailAndDetailsUseTheNormalizedUrl()
    {
        var avatar = new Avatar
        {
            Id = "avtr_027b3fb6-c9d2-4fce-9224-4cdac6676883",
            Name = "Kawaii team leader",
            ImageUrl = "https://avtr.icu/proxy/file_027b3fb6-c9d2-4fce-9224-4cdac6676883/1/file",
            ThumbnailImageUrl = "https://avtr.icu/proxy/file_027b3fb6-c9d2-4fce-9224-4cdac6676883/1/256"
        };

        Assert.Equal(
            "https://api.vrchat.cloud/api/1/image/file_027b3fb6-c9d2-4fce-9224-4cdac6676883/1/256",
            avatar.ThumbnailUrl);
        Assert.Equal(
            "https://api.vrchat.cloud/api/1/file/file_027b3fb6-c9d2-4fce-9224-4cdac6676883/1/file",
            avatar.DisplayImageUrl);
    }
}
