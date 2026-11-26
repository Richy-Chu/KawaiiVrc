using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// Writes the permissions VRChat reports for the signed-in account's groups to a small log
/// beside the other app data. It exists so a role that hides the age-gated (18+) option can
/// be checked against the API's own answer without putting a diagnostics panel in the UI.
/// Nothing reads it; deleting it is always safe.
/// </summary>
internal static class GroupPermissionDiagnostics
{
    public const string FileName = "group-permissions.log";

    private const long MaxBytes = 128 * 1024;

    public static string FilePath => UserDataPaths.Combine(FileName);

    /// <summary>
    /// Appends one snapshot. <paramref name="reported"/> is the answer from
    /// users/{id}/groups/permissions; a group missing from it fell back to the
    /// membership list, and <paramref name="lookupError"/> says the call failed.
    /// </summary>
    public static void Record(
        IReadOnlyList<Group> groups,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? reported,
        string? lookupError = null)
    {
        try
        {
            var text = new StringBuilder();
            text.AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {groups.Count} group(s)");
            if (!string.IsNullOrWhiteSpace(lookupError))
            {
                text.AppendLine($"  permissions lookup failed: {lookupError}");
            }

            foreach (var group in groups.OrderBy(group => group.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                IReadOnlyList<string>? reportedList = null;
                var hasReported = reported is not null &&
                                  reported.TryGetValue(group.EffectiveId, out reportedList);
                var permissions = hasReported ? reportedList : group.MyMember?.Permissions;
                var source = hasReported
                    ? "permissions endpoint"
                    : group.MyMember is null
                        ? "not reported"
                        : "membership list";
                var list = permissions is null || permissions.Count == 0
                    ? "(none)"
                    : string.Join(", ", permissions);
                text.AppendLine(
                    $"  {group.Name} ({group.EffectiveId}) membership={group.MembershipStatus} source={source}");
                text.AppendLine($"    age gated create: {(CanAgeGate(permissions) ? "YES" : "no")}");
                text.AppendLine($"    permissions: {list}");
            }

            text.AppendLine();

            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Only the latest snapshots matter; keep the file small enough to open.
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
            {
                File.Delete(path);
            }

            File.AppendAllText(path, text.ToString());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"group permission diagnostics: {ex.Message}");
        }
    }

    /// <summary>Same rule the create card uses: `*` carries every group permission.</summary>
    internal static bool CanAgeGate(IReadOnlyList<string>? permissions)
        => permissions is not null &&
           (permissions.Contains("*") || permissions.Contains("group-instance-age-gated-create"));
}
