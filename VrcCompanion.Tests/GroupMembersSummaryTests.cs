using VrcCompanion.ViewModels;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The group popup shows one page of 100 members at a time, so the summary has to say which
/// slice of the roster is on screen, and switch to result wording while a search is active.
/// </summary>
public class GroupMembersSummaryTests
{
    [Fact]
    public void FirstPageNamesItsRange()
        => Assert.Equal("Showing 1–100 of 27,812 members", GroupViewModel.DescribeMembers(100, 27812, false));

    [Fact]
    public void LaterPageContinuesTheRange()
        => Assert.Equal("Showing 101–200 of 27,812 members", GroupViewModel.DescribeMembers(100, 27812, false, page: 1));

    [Fact]
    public void LastPageCountsOnlyWhatItHas()
        => Assert.Equal("Showing 201–250 of 250 members", GroupViewModel.DescribeMembers(50, 250, false, page: 2));

    [Fact]
    public void SmallGroupStillShowsItsRange()
        => Assert.Equal("Showing 1–5 of 5 members", GroupViewModel.DescribeMembers(5, 5, false));

    [Fact]
    public void UnknownTotalOmitsTheTotal()
        => Assert.Equal("Showing 1–7 members", GroupViewModel.DescribeMembers(7, 0, false));

    [Theory]
    [InlineData(false, "No members loaded.")]
    [InlineData(true, "Loading members…")]
    public void EmptyListExplainsItself(bool isLoading, string expected)
        => Assert.Equal(expected, GroupViewModel.DescribeMembers(0, 27812, isLoading));

    [Fact]
    public void SearchCountsMatches()
        => Assert.Equal(
            "3 members match “bob”",
            GroupViewModel.DescribeMembers(3, 27812, false, 0, searchQuery: "bob"));

    [Fact]
    public void SingleSearchMatchIsSingular()
        => Assert.Equal(
            "1 member matches “bob”",
            GroupViewModel.DescribeMembers(1, 27812, false, 0, searchQuery: "bob"));

    [Fact]
    public void EmptySearchSaysSo()
        => Assert.Equal(
            "No members match “bob” in this group.",
            GroupViewModel.DescribeMembers(0, 27812, false, 0, searchQuery: "bob"));

    [Fact]
    public void SearchInFlightSaysItIsSearching()
        => Assert.Equal(
            "Searching for “bob”…",
            GroupViewModel.DescribeMembers(100, 27812, true, 0, searchQuery: "bob"));

    [Fact]
    public void SearchMatchCanSayHowItMatched()
        => Assert.Equal(
            "1 member matches “bob” — matched by username or user ID",
            GroupViewModel.DescribeMembers(1, 27812, false, 0, "bob", "matched by username or user ID"));
}
