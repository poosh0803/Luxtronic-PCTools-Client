using System.IO;
using System.Windows;
using System.Windows.Threading;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;
using Luxtronic.PCTools.UI.Pages;
using Luxtronic.PCTools.UI.ViewModels;
using Luxtronic.PCTools.UI.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.DependencyInjection;

namespace Luxtronic.PCTools;

public partial class App : Application
{
    private IHost? _host;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuxtronicPCTools", "logs");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(LogDirectory, "pctools-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        DispatcherUnhandledException += OnUnhandledException;

        // No config files to ship: loading never throws (a bad settings file falls back to
        // defaults), and a missing API key / sensor failure is shown inside the UI.
        var settings = AppSettingsProvider.Load();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices(services => ConfigureServices(services, settings))
            .Build();
        _host.Start();

        _host.Services.GetRequiredService<AppThemeService>().ApplySaved();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    private static void ConfigureServices(IServiceCollection services, AppSettingsProvider settings)
    {
        services.AddSingleton(settings);

        // Shell / app-level services
        services.AddNavigationViewPageProvider();
        services.AddSingleton<ISnackbarService, SnackbarService>();
        services.AddSingleton<LogService>();
        services.AddSingleton<AppThemeService>();
        services.AddSingleton<MachineService>();
        services.AddSingleton<ControllerProvider>();
        services.AddSingleton<RunState>();
        services.AddSingleton<AppCatalogService>();

        // Windows / pages / view-models. Pages are singletons so a half-filled session form
        // survives navigating to another page and back.
        services.AddSingleton<MainWindow>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<RunTestsViewModel>();
        services.AddSingleton<CpuTestViewModel>();
        services.AddSingleton<GpuTestViewModel>();
        services.AddSingleton<VramTestViewModel>();
        services.AddSingleton<RamTestViewModel>();
        services.AddSingleton<SsdTestViewModel>();
        services.AddSingleton<AppsViewModel>();
        services.AddSingleton<DriversViewModel>();
        services.AddSingleton<ToolsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<LogAboutViewModel>();
        services.AddSingleton<DashboardPage>();
        services.AddSingleton<RunTestsPage>();
        services.AddSingleton<CpuTestPage>();
        services.AddSingleton<GpuTestPage>();
        services.AddSingleton<VramTestPage>();
        services.AddSingleton<RamTestPage>();
        services.AddSingleton<SsdTestPage>();
        services.AddSingleton<AppsPage>();
        services.AddSingleton<DriversPage>();
        services.AddSingleton<ToolsPage>();
        services.AddSingleton<SettingsPage>();
        services.AddSingleton<LogAboutPage>();
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageBox.Show(e.Exception.Message, "Luxtronic PCTools - Unexpected error",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.StopAsync().GetAwaiter().GetResult();
        _host?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
