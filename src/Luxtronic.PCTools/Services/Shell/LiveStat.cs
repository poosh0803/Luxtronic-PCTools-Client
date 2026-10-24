using CommunityToolkit.Mvvm.ComponentModel;

namespace Luxtronic.PCTools.Services.Shell;

/// <summary>One live sensor tile (label + current value) on the Dashboard. Mutated in place so the
/// tiles update without being rebuilt every poll.</summary>
public sealed partial class LiveStat : ObservableObject
{
    private readonly string _unit;

    public LiveStat(string label, string unit)
    {
        Label = label;
        _unit = unit;
    }

    public string Label { get; }

    [ObservableProperty] private string _value = "--";

    public void Set(double? reading) => Value = reading is double v ? $"{v:F0}{_unit}" : "--";

    public void Clear() => Value = "--";
}
