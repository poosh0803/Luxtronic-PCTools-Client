using Luxtronic.PCTools.Models;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

public sealed class CpuTestViewModel : SingleTestViewModel
{
    public CpuTestViewModel(MachineService machine, ControllerProvider controllers, LogService log, AppSettingsProvider settings, RunState state)
        : base(machine, controllers, log, settings, state)
    {
    }

    public override string Title => "CPU";
    public override string ToolName => "Prime95 torture test";
    protected override TestKind Kind => TestKind.Cpu;
    protected override string Component => Models.Component.Cpu;
    protected override int? DurationMinutes => Settings.CpuDurationMinutes;

    public override string Description => Settings.CpuLoad switch
    {
        CpuLoadLevel.MaxHeat => "Load level: max heat (small FFT) - the hottest setting.",
        CpuLoadLevel.BlendNoAvx => "Load level: blend with AVX off - lower heat, closer to real-world use.",
        _ => "Load level: blend - mixed CPU and memory stress.",
    };

    public override IReadOnlyList<LiveStat>? LiveStats => Machine.CpuStats;

    protected override Task RunCoreAsync(TestSessionController local, CreateSessionRequest request, Action<string> log) =>
        local.RunCpuTestSessionAsync(request, log, Machine.UpdateCpu);
}

public sealed class GpuTestViewModel : SingleTestViewModel
{
    public GpuTestViewModel(MachineService machine, ControllerProvider controllers, LogService log, AppSettingsProvider settings, RunState state)
        : base(machine, controllers, log, settings, state)
    {
    }

    public override string Title => "GPU";
    public override string ToolName => "FurMark stress test";
    protected override TestKind Kind => TestKind.Gpu;
    protected override string Component => Models.Component.Gpu;
    protected override int? DurationMinutes => Settings.GpuDurationMinutes;
    public override string Description => "Full-load graphics stress test with artifact scanning.";
    public override IReadOnlyList<LiveStat>? LiveStats => Machine.GpuStats;

    protected override Task RunCoreAsync(TestSessionController local, CreateSessionRequest request, Action<string> log) =>
        local.RunGpuTestSessionAsync(request, log, Machine.UpdateGpu);
}

public sealed class VramTestViewModel : SingleTestViewModel
{
    public VramTestViewModel(MachineService machine, ControllerProvider controllers, LogService log, AppSettingsProvider settings, RunState state)
        : base(machine, controllers, log, settings, state)
    {
    }

    public override string Title => "VRAM";
    public override string ToolName => "memtest_vulkan";
    protected override TestKind Kind => TestKind.Vram;
    protected override string Component => Models.Component.Vram;
    protected override int? DurationMinutes => Settings.VramDurationMinutes;

    public override string Description =>
        $"Video memory test on GPU #{Settings.VramDeviceIndex} in memtest_vulkan's list (change the number in Settings).";

    public override IReadOnlyList<LiveStat>? LiveStats => Machine.GpuStats;

    protected override Task RunCoreAsync(TestSessionController local, CreateSessionRequest request, Action<string> log) =>
        local.RunVramTestSessionAsync(request, log, Machine.UpdateGpu);
}

public sealed class RamTestViewModel : SingleTestViewModel
{
    public RamTestViewModel(MachineService machine, ControllerProvider controllers, LogService log, AppSettingsProvider settings, RunState state)
        : base(machine, controllers, log, settings, state)
    {
    }

    public override string Title => "RAM";
    public override string ToolName => "TestMem5";
    protected override TestKind Kind => TestKind.Ram;
    protected override string Component => Models.Component.Ram;
    protected override int? DurationMinutes => Settings.RamDurationMinutes;
    public override string Description => "System memory stability test. Close other programs first - it uses most free RAM.";
    public override bool ShowRamReadout => true;

    protected override Task RunCoreAsync(TestSessionController local, CreateSessionRequest request, Action<string> log) =>
        local.RunRamTestSessionAsync(request, log, Machine.UpdateRam);
}

public sealed partial class SsdTestViewModel : SingleTestViewModel
{
    public SsdTestViewModel(MachineService machine, ControllerProvider controllers, LogService log, AppSettingsProvider settings, RunState state)
        : base(machine, controllers, log, settings, state)
    {
        machine.Initialized += OnMachineInitialized;
        if (machine.IsInitialized)
        {
            OnMachineInitialized();
        }
    }

    public override string Title => "SSD";
    public override string ToolName => "DiskSpd sequential benchmark";
    protected override TestKind Kind => TestKind.Ssd;
    protected override string Component => Models.Component.Ssd;
    protected override int? DurationMinutes => null;
    public override string Description => "Sequential read and write speed, measured with a scratch file on the chosen drive (deleted afterwards).";
    public override bool HasDrivePicker => true;

    public IReadOnlyList<SsdDriveOption> DriveOptions => Machine.DriveOptions;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private SsdDriveOption? _selectedDrive;
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private string _targetFolder = "";

    // Overwritten on every drive change so a stale path never points at the wrong physical drive.
    partial void OnSelectedDriveChanged(SsdDriveOption? value) => ApplyDefaultFolder(value);

    private void ApplyDefaultFolder(SsdDriveOption? option)
    {
        if (option is { DriveLetters.Count: > 0 })
        {
            TargetFolder = $"{option.DriveLetters[0]}\\";
        }
    }

    private void OnMachineInitialized()
    {
        OnPropertyChanged(nameof(DriveOptions));
        SelectedDrive ??= DriveOptions.FirstOrDefault();
    }

    protected override string? ValidateBeforeStart()
    {
        if (SelectedDrive is null)
        {
            return "Select a drive to benchmark first.";
        }

        ApplyDefaultFolder(SelectedDrive);
        return string.IsNullOrWhiteSpace(TargetFolder)
            ? "Couldn't auto-detect a folder on the selected drive - type one manually (e.g. \"D:\\\") before starting."
            : null;
    }

    protected override Task RunCoreAsync(TestSessionController local, CreateSessionRequest request, Action<string> log) =>
        local.RunSsdTestSessionAsync(request, SelectedDrive!.Drive, TargetFolder.Trim(), log);
}
