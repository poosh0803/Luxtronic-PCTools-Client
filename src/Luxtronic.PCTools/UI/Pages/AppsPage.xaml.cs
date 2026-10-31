using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class AppsPage : Page
{
    public AppsPage(AppsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
