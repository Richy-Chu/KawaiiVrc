using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The group popup shows one page of members, because VRChat's members endpoint returns at
/// most 100 per request. The summary has to say that instead of implying the group only has
/// the 100 members on screen, and point at the Load more button that continues the roster.
/// </summary>
public class GroupMembersSummaryTests
{
    [Fact]
    public void OnePageOfABigGroupSaysWhatIsShown()
        => Assert.Equal(
            "Showing 100 of 27,812 members — Load more fetches the next 100.",
            GroupViewModel.DescribeMembers(loaded: 100, total: 27812, isLoading: false));

    [Fact]
    public void SmallGroupCountsAsLoaded()
        => Assert.Equal("5 members loaded", GroupViewModel.DescribeMembers(5, 5, false));

    [Fact]
    public void SingleMemberIsSingular()
        => Assert.Equal("1 member loaded", GroupViewModel.DescribeMembers(1, 1, false));

    [Fact]
    public void UnknownTotalDoesNotInventOne()
        => Assert.Equal("7 members loaded", GroupViewModel.DescribeMembers(7, 0, false));

    [Theory]
    [InlineData(false, "No members loaded.")]
    [InlineData(true, "Loading members…")]
    public void EmptyListExplainsItself(bool isLoading, string expected)
        => Assert.Equal(expected, GroupViewModel.DescribeMembers(0, 27812, isLoading));
}
