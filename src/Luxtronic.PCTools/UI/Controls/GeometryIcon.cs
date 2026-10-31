using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using Wpf.Ui.Controls;

namespace Luxtronic.PCTools.UI.Controls;

/// <summary>
/// An outline icon drawn from a path on a 24x24 grid, coloured by the surrounding Foreground so it
/// follows the theme and the sidebar's selected/hover states like the built-in symbol icons do.
/// Used for the hardware components (CPU, GPU, VRAM, RAM, SSD), which the Fluent symbol set has no
/// icons for. See <see cref="HardwareIcons"/> for the drawings.
/// </summary>
public sealed class GeometryIcon : IconElement
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(Geometry), typeof(GeometryIcon),
        new FrameworkPropertyMetadata(null, (d, e) =>
        {
            if (((GeometryIcon)d)._path is { } path)
            {
                path.Data = (Geometry?)e.NewValue;
            }
        }));

    private Path? _path;

    public GeometryIcon()
    {
        Width = 20;
        Height = 20;
    }

    public Geometry? Data
    {
        get => (Geometry?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override UIElement InitializeChildren()
    {
        _path = new Path
        {
            Data = Data,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        _path.SetBinding(Shape.StrokeProperty, new Binding { Path = new PropertyPath(nameof(Foreground)), Source = this });

        // A fixed 24x24 canvas scaled to the icon size keeps every drawing's proportions and
        // position on the shared grid, so the five icons read as one set.
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(_path);
        return new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
    }
}
