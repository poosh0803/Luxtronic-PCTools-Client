using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class GamesPage : Page
{
    public GamesPage(GamesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
