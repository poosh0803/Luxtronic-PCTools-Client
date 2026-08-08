using System.Windows;
using Luxtronic.PCTools.Services;

namespace Luxtronic.PCTools;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // appsettings.json failing to load is the one startup condition treated as fatal -
        // everything else (missing API key file, sensor init failure) is recoverable/visible
        // from inside MainWindow so the technician gets an actionable message instead of the
        // app just not opening.
        AppSettingsProvider settings;
        try
        {
            settings = AppSettingsProvider.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Failed to load appsettings.json:\n\n{ex.Message}\n\nThe application cannot start.",
                "Luxtronic PCTools - Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();
    }
}
