using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Luxtronic.PCTools.UI.Behaviors;

/// <summary>
/// Makes the mouse wheel scroll a ScrollViewer (or the ScrollViewer inside a ListBox) even when a
/// child or the navigation shell swallows the bubbling MouseWheel event. Hooks PreviewMouseWheel -
/// which tunnels down from the top - and scrolls explicitly.
/// </summary>
public static class WheelScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(WheelScroll), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject d, bool value) => d.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if ((bool)e.NewValue)
        {
            element.PreviewMouseWheel += OnPreviewMouseWheel;
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scrollViewer = sender as ScrollViewer ?? FindScrollViewer((DependencyObject)sender);
        if (scrollViewer is null)
        {
            return;
        }

        if (scrollViewer.ScrollableHeight <= 0)
        {
            // This page's own ScrollViewer has nothing to scroll (the navigation shell's outer one
            // is the one that overflows), but a ScrollViewer swallows the wheel event even then -
            // so hand it up to the parent instead of letting it die here.
            if (VisualTreeHelper.GetParent(scrollViewer) is UIElement parent)
            {
                e.Handled = true;
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = sender,
                });
            }

            return;
        }

        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }

            if (FindScrollViewer(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
