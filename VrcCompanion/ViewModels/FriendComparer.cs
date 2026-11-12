using System.Collections;

namespace VrcCompanion.ViewModels;

/// <summary>Pinned friends first, then online presence, status, and name.</summary>
public sealed class FriendComparer : IComparer
{
    public int Compare(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }
        if (x is not FriendItem left)
        {
            return y is null ? 0 : 1;
        }
        if (y is not FriendItem right)
        {
            return -1;
        }

        var favorite = right.IsFavorite.CompareTo(left.IsFavorite);
        if (favorite != 0)
        {
            return favorite;
        }

        var online = right.IsOnline.CompareTo(left.IsOnline);
        if (online != 0)
        {
            return online;
        }

        var rank = left.StatusRank.CompareTo(right.StatusRank);
        if (rank != 0)
        {
            return rank;
        }

        return string.Compare(left.DisplayName, right.DisplayName, StringComparison.CurrentCultureIgnoreCase);
    }
}
