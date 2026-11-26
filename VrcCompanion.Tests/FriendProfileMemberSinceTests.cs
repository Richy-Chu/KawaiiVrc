using System.Globalization;
using VrcCompanion.Models;
using VrcCompanion.Services;
using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The profile popup shows when an account joined VRChat, for friends and
/// strangers alike: a friend-list record carries date_joined, and the
/// /users/{id} record that fills a stranger's card carries it too.
/// </summary>
public class FriendProfileMemberSinceTests
{
    private const string UserId = "usr_member_since";

    /// <summary>Same format the card uses, so the assertion does not depend on the test machine's culture.</summary>
    private static string ExpectedMonth(DateTimeOffset joined)
        => "Member since " + joined.ToLocalTime().ToString("MMM yyyy", CultureInfo.CurrentCulture);

    [Fact]
    public void AFriendListRecordShowsTheJoinMonth()
    {
        var joined = new DateTimeOffset(2023, 3, 14, 9, 30, 0, TimeSpan.Zero);
        var profile = new FriendDetailViewModel(
            new FriendItem(new VrcUser
            {
                Id = UserId,
                DisplayName = "Member Since",
                DateJoined = joined.ToString("O", CultureInfo.InvariantCulture)
            }),
            new VrcApiClient(),
            ImageLoader.Shared);

        Assert.True(profile.HasMemberSince);
        Assert.Equal(ExpectedMonth(joined), profile.MemberSinceText);
        Assert.Contains("2023", profile.MemberSinceDetailText);
    }

    [Fact]
    public void AStrangersLiveUserRecordFillsTheSameCard()
    {
        var joined = new DateTimeOffset(2019, 8, 14, 4, 5, 0, TimeSpan.Zero);

        // OpenUserProfile hands the popup a stub user, exactly as the instance
        // roster, group member rows and recent players do.
        var profile = new FriendDetailViewModel(
            new FriendItem(new VrcUser { Id = UserId, DisplayName = "Not A Friend" }),
            new VrcApiClient(),
            ImageLoader.Shared);

        Assert.False(profile.HasMemberSince);
        Assert.Equal(string.Empty, profile.MemberSinceText);

        profile.ApplyUser(new VrcUser
        {
            Id = UserId,
            DisplayName = "Not A Friend",
            DateJoined = joined.ToString("O", CultureInfo.InvariantCulture),
            Status = "offline",
            Location = "offline"
        });

        Assert.True(profile.HasMemberSince);
        Assert.Equal(ExpectedMonth(joined), profile.MemberSinceText);
    }

    [Fact]
    public void NoJoinDateLeavesTheCardHidden()
    {
        var profile = new FriendDetailViewModel(
            new FriendItem(new VrcUser { Id = UserId, DisplayName = "No Date" }),
            new VrcApiClient(),
            ImageLoader.Shared);

        Assert.False(profile.HasMemberSince);
        Assert.Equal(string.Empty, profile.MemberSinceText);
        Assert.Equal(string.Empty, profile.MemberSinceDetailText);
    }

    [Fact]
    public void AnUnparseableJoinDateLeavesTheCardHidden()
    {
        var profile = new FriendDetailViewModel(
            new FriendItem(new VrcUser
            {
                Id = UserId,
                DisplayName = "Bad Date",
                DateJoined = "not a date"
            }),
            new VrcApiClient(),
            ImageLoader.Shared);

        Assert.False(profile.HasMemberSince);
        Assert.Equal(string.Empty, profile.MemberSinceText);
    }
}
