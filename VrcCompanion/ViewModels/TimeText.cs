namespace VrcCompanion.ViewModels;

internal static class TimeText
{
    public static string Relative(DateTimeOffset? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var delta = DateTimeOffset.Now - value.Value;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        if (delta.TotalMinutes < 1)
        {
            return "just now";
        }
        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}m ago";
        }
        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}h ago";
        }
        if (delta.TotalDays < 7)
        {
            return $"{(int)delta.TotalDays}d ago";
        }

        return value.Value.ToLocalTime().ToString("MMM d");
    }
}
