using System.Globalization;
using System.Windows.Media;
using VrcCompanion.Services;

namespace VrcCompanion.ViewModels;

/// <summary>
/// One row of the Context page's instance activity box. It wraps the line VRChat's
/// log wrote, so the row shows the log's own time and never invents an event.
/// </summary>
public sealed class InstanceActivityItem
{
    private static readonly Brush JoinBrush = MakeBrush("#3DDC97");
    private static readonly Brush LeaveBrush = MakeBrush("#8B95AA");
    private static readonly Brush UdonBrush = MakeBrush("#A78BFA");

    public InstanceActivityItem(VrChatLogActivity activity)
    {
        Activity = activity;
    }

    public VrChatLogActivity Activity { get; }

    public VrChatLogActivityKind Kind => Activity.Kind;

    /// <summary>Filter key used by <see cref="ContextViewModel.SelectedActivityFilter"/>.</summary>
    public string KindKey => Activity.Kind switch
    {
        VrChatLogActivityKind.Joined => "joined",
        VrChatLogActivityKind.Left => "left",
        _ => "udon"
    };

    public string KindLabel => Activity.Kind switch
    {
        VrChatLogActivityKind.Joined => "JOINED",
        VrChatLogActivityKind.Left => "LEFT",
        _ => "UDON"
    };

    public Brush KindBrush => Activity.Kind switch
    {
        VrChatLogActivityKind.Joined => JoinBrush,
        VrChatLogActivityKind.Left => LeaveBrush,
        _ => UdonBrush
    };

    public bool IsUdon => Activity.Kind == VrChatLogActivityKind.Udon;

    public string DisplayName => string.IsNullOrWhiteSpace(Activity.DisplayName)
        ? (IsUdon ? "Udon" : "Someone")
        : Activity.DisplayName;

    /// <summary>The name for a person, the event for Udon.</summary>
    public string Title => IsUdon ? "Udon event" : DisplayName;

    public string DetailText => string.IsNullOrWhiteSpace(Activity.Detail)
        ? KindLabel
        : Activity.Detail;

    /// <summary>How many identical lines this row stands for; 1 for everything but folded Udon noise.</summary>
    public int RepeatCount => Activity.RepeatCount;

    public bool HasRepeat => Activity.RepeatCount > 1;

    public string RepeatText => Activity.RepeatCount > 1
        ? $"× {Activity.RepeatCount.ToString("N0", CultureInfo.CurrentCulture)}"
        : string.Empty;

    /// <summary>The log line's own time, in the app's 12-hour style.</summary>
    public string TimeText => Activity.At.ToLocalTime().ToString("h:mm:ss tt", CultureInfo.CurrentCulture);

    public string ToolTip => IsUdon
        ? $"Udon · {TimeText}" +
          (HasRepeat ? $" · seen {RepeatText} times" : string.Empty) +
          $"\n{DetailText}"
        : $"{DisplayName} · {KindLabel} · {TimeText}";

    public bool HasUserId => !string.IsNullOrWhiteSpace(Activity.UserId);

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
