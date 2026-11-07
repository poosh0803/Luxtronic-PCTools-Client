using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class ScriptsPage : Page
{
    public ScriptsPage(ScriptsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
