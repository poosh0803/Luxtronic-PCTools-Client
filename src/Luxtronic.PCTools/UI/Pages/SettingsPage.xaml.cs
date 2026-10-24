using System.Windows;
using System.Windows.Controls;
using Luxtronic.PCTools.UI.ViewModels;

namespace Luxtronic.PCTools.UI.Pages;

public partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel;

    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        ApiKeyBox.Password = viewModel.ApiKey; // PasswordBox.Password can't be data-bound, so sync by hand
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e) => _viewModel.ApiKey = ApiKeyBox.Password;
}
