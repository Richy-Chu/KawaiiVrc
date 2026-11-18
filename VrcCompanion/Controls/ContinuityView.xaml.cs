using System.Windows;
using System.Windows.Controls;
using VrcCompanion.ViewModels;

namespace VrcCompanion.Controls;

public partial class ContinuityView : UserControl
{
    public ContinuityView()
    {
        InitializeComponent();
    }

    private void ContinuityPersonRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ContinuityPersonItem person } &&
            DataContext is ContinuityViewModel viewModel)
        {
            viewModel.RequestAvatar(person);
        }
    }
}
