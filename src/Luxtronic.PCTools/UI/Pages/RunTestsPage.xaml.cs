using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class RunTestsPage : Page
{
    public RunTestsPage(RunTestsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
