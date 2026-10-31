using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class DriversPage : Page
{
    public DriversPage(DriversViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
