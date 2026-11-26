using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace VrcCompanion.Tests;

/// <summary>
/// The Escape chain is hand-written, so it drifted: the 1.0.112 audit found eight
/// stacked overlays without a branch. This guard reads the layer list out of the
/// XAML and the branches out of the code-behind, so an overlay cannot be added
/// without one and a deleted branch fails too. Only elements that stack with
/// Panel.ZIndex are layers; the Continuity people and worlds tabs swap the page
/// content instead, and PopupEscapeTests covers those.
/// </summary>
public class EscapeOverlayAuditTests
{
    private const string KeyDown = "OnPreviewKeyDown";
    private const string ProfileLayers = "TryCloseFriendProfileLayers";
    private const string HistoryLayers = "TryCloseInstanceHistoryLayers";

    /// <summary>One overlay's branch: the method that holds it, then the flag and command it runs.</summary>
    private sealed record Branch(string Method, params string[] Markers);

    /// <summary>
    /// Every stacked overlay, keyed by the x:Name the scan reads from the XAML.
    /// Each entry names the method that holds the branch and the flag and command
    /// that branch has to mention, so deleting either half fails the suite.
    /// </summary>
    private static readonly Dictionary<string, Branch> Branches = new(StringComparer.Ordinal)
    {
        // Window overlays, top of the chain first.
        ["SignOutConfirmationOverlay"] = new(KeyDown, "IsLogoutConfirmationOpen", "CancelLogoutCommand"),
        ["UpdateOverlay"] = new(KeyDown, "UpdateViewModel", "CloseCommand"),
        ["AdvancedLaunchOverlay"] = new(KeyDown, "IsAdvancedLaunchOpen", "CancelAdvancedLaunchCommand"),
        ["ResponseComposerOverlay"] = new(KeyDown, "IsResponseComposerOpen", "CancelResponseComposerCommand"),
        ["InviteComposerOverlay"] = new(KeyDown, "IsInviteComposerOpen", "CancelInviteComposerCommand"),
        ["FavoriteSlotPickerOverlay"] = new(KeyDown, "IsSlotPickerOpen", "CancelFavoriteSlotCommand"),
        ["AvatarDetailsOverlay"] = new(KeyDown, "IsAvatarDetailsOpen", "CloseAvatarDetailsCommand"),
        ["LaunchChooserOverlay"] = new(KeyDown, "IsLaunchChooserOpen", "CancelLaunchChoiceCommand"),
        ["GroupInfoOverlay"] = new(ProfileLayers, "IsGroupInfoOpen", "CloseGroupInfoCommand"),
        ["ProfileGroupsOverlay"] = new(ProfileLayers, "IsGroupsOpen", "CloseGroupsCommand"),
        ["FriendProfileOverlay"] = new(ProfileLayers, "HasSelectedFriend", "CloseFriendDetailCommand"),
        ["InstanceHistoryDetailOverlay"] = new(HistoryLayers, "IsDetailVisible", "BackCommand"),
        ["InstanceHistoryOverlay"] = new(HistoryLayers, "InstanceHistoryViewModel", "CloseCommand"),
        ["RecentPlayersOverlay"] = new(KeyDown, "RecentPlayersViewModel", "CloseRecentPlayersCommand"),
        ["FriendChangesOverlay"] = new(KeyDown, "IsFriendChangesOpen", "CloseFriendChangesCommand"),
        ["FriendLocationsOverlay"] = new(KeyDown, "IsFriendLocationsOpen", "CloseFriendLocationsCommand"),
        ["BoopPreviewOverlay"] = new(KeyDown, "IsBoopPreviewOpen", "CloseBoopPreviewCommand"),
        ["WorldDialogOverlay"] = new(KeyDown, "HasWorldDialog", "CloseWorldDialogCommand"),
        // Page-local layers, which close after the window ones.
        ["ContinuitySessionOverlay"] = new(KeyDown, "IsSessionDetailVisible", "CloseSessionCommand"),
        ["MediaViewerOverlay"] = new(KeyDown, "IsViewerOpen", "CloseViewerCommand"),
        ["ModerationMemberCardOverlay"] = new(KeyDown, "IsMemberCardOpen", "CloseMemberCardCommand"),
    };

    private static readonly Regex Comments = new("<!--.*?-->", RegexOptions.Singleline);

    private static readonly Regex Elements = new(
        @"<(?<tag>[A-Za-z][A-Za-z0-9_.:]*)(?<attributes>[^<>]*)>",
        RegexOptions.Singleline);

    private static readonly Regex NameAttribute = new("x:Name\\s*=\\s*\"(?<name>[^\"]+)\"");

    [Fact]
    public void EveryStackedOverlayHasAnEscapeBranch()
    {
        var codeBehind = File.ReadAllText(SourceFile("VrcCompanion", "MainWindow.xaml.cs"));
        var methods = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyDown] = MethodBody(codeBehind, "void OnPreviewKeyDown(KeyEventArgs e)"),
            [ProfileLayers] = MethodBody(codeBehind, "bool TryCloseFriendProfileLayers()"),
            [HistoryLayers] = MethodBody(codeBehind, "bool TryCloseInstanceHistoryLayers()"),
        };

        var problems = Audit(
            new[]
            {
                ("VrcCompanion/MainWindow.xaml",
                    File.ReadAllText(SourceFile("VrcCompanion", "MainWindow.xaml"))),
                ("VrcCompanion/Controls/ContinuityView.xaml",
                    File.ReadAllText(SourceFile("VrcCompanion", "Controls", "ContinuityView.xaml"))),
            },
            Branches,
            methods);

        Assert.True(
            problems.Count == 0,
            "The Escape audit drifted:" + Environment.NewLine +
            string.Join(Environment.NewLine, problems.Select(problem => " - " + problem)));
    }

    [Fact]
    public void TheGuardReportsAnOverlayThatHasNoBranch()
    {
        const string xaml = """
            <Grid x:Name="KnownOverlay" Panel.ZIndex="30" />
            <Grid x:Name="NewOverlay" Panel.ZIndex="31" />
            <Grid Panel.ZIndex="32" />
            """;
        var branches = new Dictionary<string, Branch>(StringComparer.Ordinal)
        {
            ["KnownOverlay"] = new(KeyDown, "IsKnownOpen", "CloseKnownCommand")
        };
        var methods = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyDown] = "if (_viewModel.IsKnownOpen) { _viewModel.CloseKnownCommand.Execute(null); }"
        };

        var problems = Audit(new[] { ("synthetic.xaml", xaml) }, branches, methods);

        Assert.Contains(
            problems,
            problem => problem.Contains("NewOverlay", StringComparison.Ordinal) &&
                       problem.Contains("no branch", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("no x:Name", StringComparison.Ordinal));
        Assert.DoesNotContain(problems, problem => problem.Contains("KnownOverlay", StringComparison.Ordinal));
    }

    [Fact]
    public void TheGuardReportsABranchThatLostItsCommand()
    {
        const string xaml = """<Grid x:Name="KnownOverlay" Panel.ZIndex="30" />""";
        var branches = new Dictionary<string, Branch>(StringComparer.Ordinal)
        {
            ["KnownOverlay"] = new(KeyDown, "IsKnownOpen", "CloseKnownCommand")
        };
        var methods = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [KeyDown] = "if (_viewModel.IsKnownOpen) { return; }"
        };

        var problems = Audit(new[] { ("synthetic.xaml", xaml) }, branches, methods);

        Assert.Contains(
            problems,
            problem => problem.Contains("KnownOverlay", StringComparison.Ordinal) &&
                       problem.Contains("CloseKnownCommand", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns every way the layer list and the Escape chain disagree; empty is
    /// the only passing answer. Messages carry the file and line so the fix is
    /// obvious.
    /// </summary>
    private static IReadOnlyList<string> Audit(
        IEnumerable<(string File, string Xaml)> sources,
        IReadOnlyDictionary<string, Branch> branches,
        IReadOnlyDictionary<string, string> methods)
    {
        var problems = new List<string>();
        var overlays = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var stacked = 0;

        foreach (var (file, xaml) in sources)
        {
            var text = Comments.Replace(xaml, string.Empty);
            foreach (Match element in Elements.Matches(text))
            {
                var attributes = element.Groups["attributes"].Value;
                if (!attributes.Contains("Panel.ZIndex", StringComparison.Ordinal))
                {
                    continue;
                }

                stacked++;
                var line = 1 + text[..element.Index].Count(character => character == '\n');
                var name = NameAttribute.Match(attributes);
                if (!name.Success)
                {
                    problems.Add(
                        $"{file}:{line} stacks with Panel.ZIndex but has no x:Name, so the Escape audit cannot see it.");
                    continue;
                }

                var value = name.Groups["name"].Value;
                if (overlays.ContainsKey(value))
                {
                    problems.Add(
                        $"{file}:{line} reuses the x:Name '{value}'; overlay names have to be unique for the audit.");
                    continue;
                }

                overlays.Add(value, $"{file}:{line}");
            }
        }

        if (stacked == 0)
        {
            problems.Add("The scan found no Panel.ZIndex overlays, so it is not reading the XAML at all.");
        }

        foreach (var overlay in overlays)
        {
            if (!branches.ContainsKey(overlay.Key))
            {
                problems.Add(
                    $"{overlay.Value}: '{overlay.Key}' stacks with Panel.ZIndex but has no branch in the Escape chain. " +
                    $"Add one to {KeyDown} (or a helper it calls) and an entry here.");
            }
        }

        foreach (var branch in branches)
        {
            if (!overlays.TryGetValue(branch.Key, out var where))
            {
                problems.Add(
                    $"'{branch.Key}' has a branch entry but no Panel.ZIndex overlay with that x:Name in the XAML files.");
                continue;
            }

            if (!methods.TryGetValue(branch.Value.Method, out var body))
            {
                problems.Add($"{where}: '{branch.Key}' is handled in {branch.Value.Method}, but that method is gone.");
                continue;
            }

            foreach (var marker in branch.Value.Markers)
            {
                if (!body.Contains(marker, StringComparison.Ordinal))
                {
                    problems.Add(
                        $"{where}: '{branch.Key}' should be closed in {branch.Value.Method}, but '{marker}' is gone from it.");
                }
            }
        }

        if (!methods.TryGetValue(KeyDown, out var keyDown))
        {
            problems.Add($"{KeyDown} is gone, so nothing answers the key.");
            return problems;
        }

        foreach (var helper in branches.Values
                     .Select(branch => branch.Method)
                     .Where(method => !string.Equals(method, KeyDown, StringComparison.Ordinal))
                     .Distinct(StringComparer.Ordinal))
        {
            if (!keyDown.Contains(helper + "()", StringComparison.Ordinal))
            {
                problems.Add($"{KeyDown} does not call {helper}(), so the overlays it closes cannot answer Escape.");
            }
        }

        return problems;
    }

    /// <summary>
    /// The brace-matched body of one method, so a branch in a helper counts only
    /// through code that belongs to that helper.
    /// </summary>
    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"'{signature}' is not in MainWindow.xaml.cs, so the audit cannot read it.");
        }

        var open = source.IndexOf('{', start);
        var depth = 0;
        for (var index = open; index >= 0 && index < source.Length; index++)
        {
            if (source[index] == '{')
            {
                depth++;
            }
            else if (source[index] == '}' && --depth == 0)
            {
                return source[start..(index + 1)];
            }
        }

        throw new InvalidOperationException(
            $"'{signature}' has no closing brace in MainWindow.xaml.cs.");
    }

    private static string SourceFile(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(
            $"Could not find {string.Join('/', parts)} above {AppContext.BaseDirectory}.");
    }
}
