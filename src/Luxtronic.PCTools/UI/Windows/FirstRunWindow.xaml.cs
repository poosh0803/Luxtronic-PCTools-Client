using System.Windows;
using Luxtronic.PCTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Luxtronic.PCTools.UI.Windows;

/// <summary>
/// Shown once on a fresh install: the three server-connection settings (URL, API key, offline
/// mode) and nothing else. Closing it any way other than Finish leaves the defaults and sends the
/// technician on to the Settings page, same as before this window existed.
/// </summary>
public partial class FirstRunWindow : FluentWindow
{
    private readonly AppSettingsProvider _settings;

    public FirstRunWindow(AppSettingsProvider settings)
    {
        _settings = settings;
        SystemThemeWatcher.Watch(this);
        InitializeComponent();

        ServerUrlBox.Text = settings.ServerBaseUrl;
        ApiKeyBox.Password = settings.ApiKey ?? "";
        OfflineSwitch.IsChecked = settings.OfflineMode;
    }

    /// <summary>True when the technician pressed Finish with valid values.</summary>
    public bool Completed { get; private set; }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        var error = _settings.SetConnection(ServerUrlBox.Text, ApiKeyBox.Password, OfflineSwitch.IsChecked == true);
        if (error is not null)
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        Completed = true;
        Close();
    }

    private void Skip_Click(object sender, RoutedEventArgs e) => Close();
}
