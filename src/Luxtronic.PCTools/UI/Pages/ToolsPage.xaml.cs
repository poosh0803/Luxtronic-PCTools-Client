using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class ToolsPage : Page
{
    public ToolsPage(ToolsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
