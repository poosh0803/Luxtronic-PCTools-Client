using CommunityToolkit.Mvvm.ComponentModel;

namespace Luxtronic.PCTools.Services.Shell;

/// <summary>
/// App-wide "a test is running" state shared by the Run Tests page and every single-test tab, so
/// all Start buttons disable together. Also pauses the Dashboard's idle sensor polling while a
/// test runs (SensorMonitor can't be read from two places at once).
/// </summary>
public sealed partial class RunState : ObservableObject
{
    private readonly MachineService _machine;

    public RunState(MachineService machine) => _machine = machine;

    [ObservableProperty] private bool _isBusy;

    public void Begin()
    {
        IsBusy = true;
        _machine.PauseIdlePolling();
    }

    public void End()
    {
        IsBusy = false;
        _machine.ResumeIdlePolling();
    }
}
