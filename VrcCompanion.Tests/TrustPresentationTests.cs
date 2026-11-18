using System.Windows.Media;
using VrcCompanion.Models;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

public class TrustPresentationTests
{
    [Theory]
    [InlineData("system_trust_basic", "New User", 0x17, 0x78, 0xFF)]
    [InlineData("system_trust_known", "User", 0x2B, 0xCF, 0x5C)]
    [InlineData("system_trust_trusted", "Known User", 0xFF, 0x7B, 0x42)]
    [InlineData("system_trust_veteran", "Trusted User", 0xB1, 0x8F, 0xFF)]
    [InlineData("system_trust_legend", "Trusted Legend", 0xB1, 0x8F, 0xFF)]
    public void TrustRanksUseVrcxLabelsAndColors(string tag, string label, byte r, byte g, byte b)
    {
        var trust = UserPresentation.Trust(new[] { tag });

        Assert.Equal(label, trust.Label);
        Assert.Equal(Color.FromRgb(r, g, b), trust.Color);
    }

    [Fact]
    public void VisitorUsesUntrustedColor()
    {
        var trust = UserPresentation.Trust(Array.Empty<string>());

        Assert.Equal("Visitor", trust.Label);
        Assert.Equal(Color.FromRgb(0xCC, 0xCC, 0xCC), trust.Color);
    }

    [Fact]
    public void HigherTrustTagWinsWhenSeveralArePresent()
    {
        var trust = UserPresentation.Trust(new[]
        {
            "system_trust_basic",
            "system_trust_known",
            "system_trust_trusted",
            "system_trust_veteran"
        });

        Assert.Equal("Trusted User", trust.Label);
        Assert.Equal(UserPresentation.VeteranColor, trust.Color);
    }

    [Fact]
    public void ModeratorAndTrollColorsOverrideTheRankColor()
    {
        var moderator = UserPresentation.Trust(new[] { "system_trust_veteran" }, "moderator");
        Assert.True(moderator.IsModerator);
        Assert.Equal("Trusted User", moderator.Label);
        Assert.Equal(UserPresentation.VipColor, moderator.Color);

        var troll = UserPresentation.Trust(new[] { "system_trust_trusted", "system_troll" });
        Assert.True(troll.IsTroll);
        Assert.Equal("Known User", troll.Label);
        Assert.Equal(UserPresentation.TrollColor, troll.Color);
    }

    [Fact]
    public void FriendRowsUseTheSameTrustPresentation()
    {
        var friend = new FriendItem(new VrcUser
        {
            Id = "usr_trust",
            DisplayName = "Trust",
            Tags = new List<string> { "system_trust_trusted" }
        });

        Assert.Equal("Known User", friend.TrustLabel);
        Assert.Same(UserPresentation.BrushFor("trusted"), friend.TrustBrush);
    }

    [Fact]
    public void UnreadableProfileColorsAreAdjustedAndInvalidOnesFallBack()
    {
        var theme = UserPresentation.Theme("#000000", "#ffffff", "not-a-color");

        var icon = Assert.IsType<SolidColorBrush>(theme.Icon);
        var button = Assert.IsType<SolidColorBrush>(theme.Button);
        var subtext = Assert.IsType<SolidColorBrush>(theme.Subtext);

        Assert.True(icon.Color.R > 0x50);
        Assert.True(button.Color.R < 0xF0);
        Assert.True(subtext.Color.R > 0x80);
    }

    [Fact]
    public void ProfileButtonTextContrastsWithLightAndDarkButtonColors()
    {
        var darkText = Assert.IsType<SolidColorBrush>(
            UserPresentation.ButtonText(UserPresentation.Theme(null, "#ffffff", null).Button));
        var lightText = Assert.IsType<SolidColorBrush>(
            UserPresentation.ButtonText(UserPresentation.Theme(null, "#1778ff", null).Button));

        Assert.True(darkText.Color.R < 0x40);
        Assert.True(lightText.Color.R > 0xE0);
    }
}
