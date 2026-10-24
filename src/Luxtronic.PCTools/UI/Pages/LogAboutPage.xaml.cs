using System.Collections.Specialized;
using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class LogAboutPage : Page
{
    public LogAboutPage(LogAboutViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // Keep the newest line in view, like the old LogBox.ScrollToEnd().
        viewModel.Entries.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
            {
                LogList.ScrollIntoView(LogList.Items[^1]);
            }
        };
    }
}
