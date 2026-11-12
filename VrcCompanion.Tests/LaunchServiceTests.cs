using System;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

public class LaunchServiceTests
{
    private const string WorldId = "wrld_4432ea9b-729c-46e3-8eaf-846f7706d3d5";

    [Theory]
    [InlineData(WorldId + ":12345")]
    [InlineData(WorldId + ":12345~private(usr_61cc28f3-76ea-41b4-a9da-ede327ef3703)~region(us)")]
    [InlineData(WorldId + ":12345~group(grp_1a2b3c4d-5e6f-7890-abcd-ef1234567890)~groupAccessType(public)")]
    [InlineData(WorldId + ":12345~hidden(usr_61cc28f3-76ea-41b4-a9da-ede327ef3703)")]
    public void IsLocation_AcceptsVrchatInstances(string location)
    {
        Assert.True(LaunchService.IsLocation(location));
        Assert.Equal(WorldId, LaunchService.GetWorldIdFromLocation(location));
        Assert.NotNull(LaunchService.GetInstanceIdFromLocation(location));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12345")]
    [InlineData("wrld_:12345")]
    [InlineData("wrld_not-a-uuid:12345")]
    [InlineData(WorldId)]
    [InlineData(WorldId + ":")]
    [InlineData(WorldId + ":12345&ref=evil")]
    [InlineData(WorldId + ":12345?x=1")]
    [InlineData(WorldId + ":12345 #frag")]
    [InlineData(WorldId + ":12\n345")]
    [InlineData(WorldId + ":12345\" --no-vr")]
    [InlineData("wrld_4432ea9b-729c-46e3-8eaf-846f7706d3d5x:12345")]
    public void IsLocation_RejectsMalformedOrInjectedLocations(string? location)
    {
        Assert.False(LaunchService.IsLocation(location));
        Assert.Null(LaunchService.GetWorldIdFromLocation(location));
        Assert.Null(LaunchService.GetInstanceIdFromLocation(location));
    }

    [Fact]
    public void BuildLaunchUri_KeepsTheLocationReadable()
    {
        var location = WorldId + ":12345~region(us)~private(usr_61cc28f3-76ea-41b4-a9da-ede327ef3703)";

        var uri = LaunchService.BuildLaunchUri(location);

        Assert.Equal($"vrchat://launch?ref=vrchat.com&id={location}", uri, StringComparer.Ordinal);
        // Readable separators, no percent-encoding of the id.
        Assert.Contains("wrld_4432ea9b", uri, StringComparison.Ordinal);
        Assert.Contains("~region(us)", uri, StringComparison.Ordinal);
        Assert.DoesNotContain("%", uri, StringComparison.Ordinal);
        // Still exactly one '&': the ref/id separator the launcher expects.
        Assert.Equal(1, uri.Count(c => c == '&'));
    }

    [Theory]
    [InlineData("https://vrchat.com/home/avatar/avtr_1")]
    [InlineData("http://example.test/path?x=1")]
    [InlineData("HTTPS://VRChat.com/home/user/usr_1")]
    public void IsWebUrl_AcceptsHttpAndHttps(string url)
    {
        Assert.True(LaunchService.IsWebUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-msdt:/id PCWDiagnostic")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vrchat://launch?id=wrld_x:1")]
    [InlineData("ftp://example.test/file")]
    [InlineData("C:\\Windows\\notepad.exe")]
    public void IsWebUrl_RejectsOtherSchemes(string? url)
    {
        Assert.False(LaunchService.IsWebUrl(url));
    }

    [Fact]
    public void OpenUrl_DoesNotLaunchNonWebSchemes()
    {
        Assert.False(LaunchService.OpenUrl("file:///C:/Windows/System32/calc.exe"));
        Assert.False(LaunchService.OpenUrl("ms-msdt:/id PCWDiagnostic"));
        Assert.False(LaunchService.OpenUrl(null));
    }
}
