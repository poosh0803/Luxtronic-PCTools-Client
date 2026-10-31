using System.IO;
using System.Windows;
using System.Text.Json;
using Wpf.Ui.Appearance;

namespace Luxtronic.PCTools.Services.Shell;

public enum ThemeChoice { Dark, Light, System }

/// <summary>Applies and persists the light/dark/system theme choice (per-user, outside the install folder).</summary>
public sealed class AppThemeService
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LuxtronicPCTools", "ui.json");

    private sealed class UiSettings { public ThemeChoice Theme { get; set; } = ThemeChoice.Dark; }

    public ThemeChoice Current { get; private set; } = ThemeChoice.Dark;

    private readonly List<Window> _windows = new();

    /// <summary>
    /// Registers a window with the theme system. The window follows the OS theme only while the
    /// choice is System; for Dark/Light the system watcher is detached (a permanently attached
    /// watcher would force the OS theme at startup and ignore the saved choice).
    /// </summary>
    public void Register(Window window)
    {
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        // Watch/UnWatch need a loaded window (UnWatch throws before that), so a window registered
        // from its constructor is synced once it has loaded.
        if (window.IsLoaded) SyncWatcher(window);
        else window.Loaded += (_, _) => SyncWatcher(window);
    }

    private void SyncWatcher(Window window)
    {
        if (!window.IsLoaded) return; // Apply() may run before a registered window has loaded; Loaded syncs it then
        if (Current == ThemeChoice.System) SystemThemeWatcher.Watch(window);
        else SystemThemeWatcher.UnWatch(window);
    }

    public void ApplySaved()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                Current = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath))?.Theme ?? ThemeChoice.Dark;
            }
        }
        catch (Exception)
        {
            Current = ThemeChoice.Dark; // corrupt/unreadable UI prefs must never block startup
        }

        Apply(Current, save: false);
    }

    public void Apply(ThemeChoice choice, bool save = true)
    {
        Current = choice;
        var theme = choice switch
        {
            ThemeChoice.Light => ApplicationTheme.Light,
            ThemeChoice.Dark => ApplicationTheme.Dark,
            _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light
                ? ApplicationTheme.Light : ApplicationTheme.Dark,
        };
        ApplicationThemeManager.Apply(theme);
        foreach (var window in _windows.ToArray()) SyncWatcher(window);

        if (!save) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new UiSettings { Theme = choice }));
        }
        catch (Exception)
        {
            // Best-effort persistence only.
        }
    }
}
