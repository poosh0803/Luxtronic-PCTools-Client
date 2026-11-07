using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly ControllerProvider _controllers;
    private readonly AppSettingsProvider _settings;
    private readonly RunState _state;

    public DashboardViewModel(MachineService machine, ControllerProvider controllers, AppSettingsProvider settings, RunState state)
    {
        _state = state;
        state.PropertyChanged += (_, _) => RestartHwInfoCommand.NotifyCanExecuteChanged();
        machine.Initialized += () => RestartHwInfoCommand.NotifyCanExecuteChanged();
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

    private bool _restarting;

    [RelayCommand(CanExecute = nameof(CanRestartHwInfo))]
    private async Task RestartHwInfoAsync()
    {
        _restarting = true;
        RestartHwInfoCommand.NotifyCanExecuteChanged();
        try { await Machine.RestartHwInfoAsync(); }
        finally
        {
            _restarting = false;
            RestartHwInfoCommand.NotifyCanExecuteChanged();
        }
    }

    // Not while a test is running: the test is reading the sensors.
    private bool CanRestartHwInfo() => !_restarting && Machine.IsInitialized && !_state.IsBusy;

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
