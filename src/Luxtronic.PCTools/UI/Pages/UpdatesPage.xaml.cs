using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class UpdatesPage : Page
{
    public UpdatesPage(UpdatesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Pages are singletons, so check again on every visit.
        Loaded += async (_, _) => await viewModel.Check();
    }
}
