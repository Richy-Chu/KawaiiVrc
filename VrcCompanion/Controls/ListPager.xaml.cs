using System.Windows.Controls;

namespace VrcCompanion.Controls;

/// <summary>
/// Previous / page / Next for a <see cref="ViewModels.PagedList{T}"/>; bind its
/// DataContext to the page and its Visibility to HasPages.
/// </summary>
public partial class ListPager : UserControl
{
    public ListPager()
    {
        InitializeComponent();
    }
}
