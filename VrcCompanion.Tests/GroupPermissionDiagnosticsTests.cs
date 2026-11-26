using System.Collections.Generic;
using System.IO;
using VrcCompanion.Models;
using VrcCompanion.Services;
using Xunit;

namespace VrcCompanion.Tests;

[Collection(Seams.Collection)]
[ResetSeams]
public class GroupPermissionDiagnosticsTests
{
    [Fact]
    public void TheLogNamesEachGroupsRoleAndWhetherItCanAgeGate()
    {
        var groups = new List<Group>
        {
            new()
            {
                Id = "grp_eden",
                Name = "Eden",
                MembershipStatus = "member",
                MyMember = new GroupMember { Permissions = new List<string> { "group-instance-public-create" } }
            },
            new()
            {
                Id = "grp_plain",
                Name = "Plain Group",
                MembershipStatus = "member",
                MyMember = new GroupMember { Permissions = new List<string> { "group-instance-members-create" } }
            }
        };
        var reported = new Dictionary<string, IReadOnlyList<string>>(System.StringComparer.Ordinal)
        {
            ["grp_eden"] = new List<string> { "group-instance-public-create", "group-instance-age-gated-create" }
        };

        GroupPermissionDiagnostics.Record(groups, reported);

        var text = File.ReadAllText(GroupPermissionDiagnostics.FilePath);
        Assert.Contains("Eden (grp_eden)", text, System.StringComparison.Ordinal);
        Assert.Contains("Plain Group (grp_plain)", text, System.StringComparison.Ordinal);
        Assert.Contains("source=permissions endpoint", text, System.StringComparison.Ordinal);
        Assert.Contains("source=membership list", text, System.StringComparison.Ordinal);
        Assert.Contains("group-instance-age-gated-create", text, System.StringComparison.Ordinal);
        Assert.Contains("age gated create: YES", text, System.StringComparison.Ordinal);
        Assert.Contains("age gated create: no", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedLookupSaysSoAndStillWritesTheMembershipList()
    {
        GroupPermissionDiagnostics.Record(
            new[]
            {
                new Group
                {
                    Id = "grp_unknown",
                    Name = "Unknown Role Group",
                    MembershipStatus = "member"
                }
            },
            reported: null,
            lookupError: "HTTP 500");

        var text = File.ReadAllText(GroupPermissionDiagnostics.FilePath);
        Assert.Contains("permissions lookup failed: HTTP 500", text, System.StringComparison.Ordinal);
        Assert.Contains("Unknown Role Group (grp_unknown)", text, System.StringComparison.Ordinal);
        Assert.Contains("source=not reported", text, System.StringComparison.Ordinal);
        Assert.Contains("age gated create: no", text, System.StringComparison.Ordinal);
    }
}
