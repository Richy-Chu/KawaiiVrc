using System.Collections.Generic;
using VrcCompanion.Models;

namespace VrcCompanion.Services;

/// <summary>
/// An order-independent fingerprint of a friend snapshot: who they are, where
/// they are, their status and current avatar.
/// <para>
/// The order matters less than you would think: VRChat returns the friend list
/// in a slightly different order on each poll, so an ordered fingerprint never
/// matched and the "nothing changed" shortcut never fired. Summing a hash per
/// friend makes a reordered but otherwise identical snapshot compare equal.
/// </para>
/// </summary>
internal static class FriendSignature
{
    private const ulong Prime = 1099511628211UL;

    public static long Compute(IReadOnlyList<VrcUser> friends)
    {
        var sum = 0UL;
        foreach (var friend in friends)
        {
            var hash = 14695981039346656037UL;
            hash = Mix(hash, friend.Id);
            hash = Mix(hash, friend.Location);
            hash = Mix(hash, friend.Status);
            hash = Mix(hash, friend.StatusDescription);
            hash = Mix(hash, friend.CurrentAvatarImageUrl);
            sum += hash;
        }

        // Count is folded in so an empty list cannot match, and so adding or
        // dropping a friend changes the value even if their hash collided.
        return (long)(sum ^ ((ulong)friends.Count * Prime));
    }

    private static ulong Mix(ulong hash, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            foreach (var character in value)
            {
                hash = (hash ^ character) * Prime;
            }
        }

        return hash * Prime;
    }
}
