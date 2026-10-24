using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
