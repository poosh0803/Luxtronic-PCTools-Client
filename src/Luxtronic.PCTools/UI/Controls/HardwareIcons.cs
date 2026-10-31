using System.Windows.Media;

namespace Luxtronic.PCTools.UI.Controls;

/// <summary>
/// The sidebar icons for the hardware tests, drawn as simple outlines on a 24x24 grid (strokes only,
/// no fills): a CPU package with its pins, a graphics card with two fans, a memory board of chips
/// (video memory), a RAM stick, and a 2.5" SSD with its activity light.
/// </summary>
public static class HardwareIcons
{
    public static Geometry Cpu { get; } = Make(
        "M8.5 6.5h7a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2h-7a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2z M10 10h4v4h-4z " +
        "M9.5 3.5v3 M12 3.5v3 M14.5 3.5v3 M9.5 17.5v3 M12 17.5v3 M14.5 17.5v3 " +
        "M3.5 9.5h3 M3.5 12h3 M3.5 14.5h3 M17.5 9.5h3 M17.5 12h3 M17.5 14.5h3");

    public static Geometry Gpu { get; } = Make(
        "M4.5 7h15a1.5 1.5 0 0 1 1.5 1.5v6a1.5 1.5 0 0 1-1.5 1.5h-15a1.5 1.5 0 0 1-1.5-1.5v-6A1.5 1.5 0 0 1 4.5 7z " +
        "M5.9 11.5a2.6 2.6 0 1 0 5.2 0a2.6 2.6 0 1 0-5.2 0z M12.9 11.5a2.6 2.6 0 1 0 5.2 0a2.6 2.6 0 1 0-5.2 0z " +
        "M6 16v2.5h8V16 M2.5 5v14");

    public static Geometry Vram { get; } = Make(
        "M4.5 6.5h15a1.5 1.5 0 0 1 1.5 1.5v8a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 16V8a1.5 1.5 0 0 1 1.5-1.5z " +
        "M6.5 8.7h4v2.6h-4z M13.5 8.7h4v2.6h-4z M6.5 12.7h4v2.6h-4z M13.5 12.7h4v2.6h-4z " +
        "M7 17.5v2.5 M10 17.5v2.5 M14 17.5v2.5 M17 17.5v2.5");

    public static Geometry Ram { get; } = Make(
        "M3.5 7.5h17a1 1 0 0 1 1 1V15h-19V8.5a1 1 0 0 1 1-1z M5.5 9.5h3v3h-3z M10.5 9.5h3v3h-3z M15.5 9.5h3v3h-3z " +
        "M5 15v2.5 M7.5 15v2.5 M10 15v2.5 M14 15v2.5 M16.5 15v2.5 M19 15v2.5");

    public static Geometry Ssd { get; } = Make(
        "M7 4.5h10a2.5 2.5 0 0 1 2.5 2.5v10a2.5 2.5 0 0 1-2.5 2.5H7A2.5 2.5 0 0 1 4.5 17V7A2.5 2.5 0 0 1 7 4.5z " +
        "M15.2 16.2a0.9 0.9 0 1 0 1.8 0a0.9 0.9 0 1 0-1.8 0z M8 8.5h8 M8 11.5h5");

    private static Geometry Make(string pathData)
    {
        var geometry = Geometry.Parse(pathData);
        geometry.Freeze();
        return geometry;
    }
}
