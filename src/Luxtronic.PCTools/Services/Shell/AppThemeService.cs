using System.IO;
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
