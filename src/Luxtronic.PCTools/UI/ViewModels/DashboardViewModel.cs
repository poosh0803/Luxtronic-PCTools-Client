using CommunityToolkit.Mvvm.ComponentModel;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly ControllerProvider _controllers;
    private readonly AppSettingsProvider _settings;

    public DashboardViewModel(MachineService machine, ControllerProvider controllers, AppSettingsProvider settings)
    {
        Machine = machine;
        _controllers = controllers;
        _settings = settings;
        _tools = controllers.CheckTools();
        controllers.Changed += Refresh;
        machine.Initialized += () => Tools = controllers.CheckTools(); // tools are unpacked during init
        Refresh();
    }

    public MachineService Machine { get; }

    [ObservableProperty] private IReadOnlyList<ToolStatus> _tools;

    [ObservableProperty] private string _serverUrl = "";

    [ObservableProperty] private string _apiKeyStatus = "";

    private void Refresh()
    {
        ServerUrl = _settings.OfflineMode ? "OFFLINE (dev mode) - nothing is sent to the server" : _settings.ServerBaseUrl;
        ApiKeyStatus = _settings.OfflineMode
            ? "not needed in offline mode"
            : _controllers.GetOrCreate() is not null
            ? "API key loaded"
            : $"API key problem: {_controllers.Error}";
    }
}
