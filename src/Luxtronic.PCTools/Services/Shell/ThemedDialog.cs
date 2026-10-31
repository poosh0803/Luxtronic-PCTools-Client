using System.Windows;
using Wpf.Ui.Controls;

namespace Luxtronic.PCTools.Services.Shell;

/// <summary>
/// Confirmation dialogs in the app's own (WPF-UI Fluent, light/dark) theme, instead of the classic
/// Windows message box that ignores it.
/// </summary>
public static class ThemedDialog
{
    /// <summary>Shows a Yes/No dialog over the main window; true when the primary button is chosen.</summary>
    public static async Task<bool> ConfirmAsync(string title, string message, string yesText = "Yes", string noText = "No")
    {
        var box = new Wpf.Ui.Controls.MessageBox
        {
            Title = title,
            Content = message,
            PrimaryButtonText = yesText,
            CloseButtonText = noText,
            Owner = Application.Current?.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var result = await box.ShowDialogAsync();
        return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
    }
}
