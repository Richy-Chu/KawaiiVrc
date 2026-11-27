using System.Windows.Controls;

namespace VrcCompanion.Controls;

/// <summary>
/// The instance activity box: a title, its filter pills, the paged rows and the empty
/// text. Its DataContext is an <see cref="ViewModels.InstanceActivityFeed"/>, so the
/// Context page, the Instance tab and the world dialog's Players panel all draw the
/// exact same box from the same feed.
/// </summary>
public partial class InstanceActivityView : UserControl
{
    public InstanceActivityView()
    {
        InitializeComponent();
    }
}
