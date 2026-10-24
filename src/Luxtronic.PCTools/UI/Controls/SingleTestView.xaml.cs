using System.Windows.Controls;

namespace Luxtronic.PCTools.UI.Controls;

/// <summary>The one shared layout every single-test tab uses (DataContext = a SingleTestViewModel).</summary>
public partial class SingleTestView : UserControl
{
    public SingleTestView() => InitializeComponent();
}
