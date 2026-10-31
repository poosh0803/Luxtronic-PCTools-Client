using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Controls;

/// <summary>The list UI shared by the Apps and Drivers pages; DataContext is an <see cref="AppListViewModel"/>.</summary>
public partial class AppListView : UserControl
{
    public AppListView()
    {
        InitializeComponent();

        // Pages are singletons, so refresh on every visit to pick up changes made on the server.
        Loaded += async (_, _) =>
        {
            if (DataContext is not AppListViewModel vm) return;
            vm.SyncView();
            await vm.Refresh();
        };
    }
}
