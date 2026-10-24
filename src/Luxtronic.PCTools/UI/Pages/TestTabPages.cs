using System.Windows.Controls;
using Luxtronic.PCTools.UI.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

// One thin page type per test tab: the navigation sidebar resolves pages by type, so each tab needs
// its own class, but they all show the same SingleTestView with their own view-model.

public sealed class CpuTestPage : Page
{
    public CpuTestPage(CpuTestViewModel viewModel) => Content = new SingleTestView { DataContext = viewModel };
}

public sealed class GpuTestPage : Page
{
    public GpuTestPage(GpuTestViewModel viewModel) => Content = new SingleTestView { DataContext = viewModel };
}

public sealed class VramTestPage : Page
{
    public VramTestPage(VramTestViewModel viewModel) => Content = new SingleTestView { DataContext = viewModel };
}

public sealed class RamTestPage : Page
{
    public RamTestPage(RamTestViewModel viewModel) => Content = new SingleTestView { DataContext = viewModel };
}

public sealed class SsdTestPage : Page
{
    public SsdTestPage(SsdTestViewModel viewModel) => Content = new SingleTestView { DataContext = viewModel };
}
