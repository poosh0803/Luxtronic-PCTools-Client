using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

/// <summary>One row on the Apps page.</summary>
public sealed partial class AppItemViewModel : ObservableObject
{
    private readonly AppCatalogService _catalog;
    private readonly LogService _log;
    private CancellationTokenSource? _cts;

    public AppItemViewModel(AppInfo info, AppCatalogService catalog, LogService log)
    {
        Info = info;
        _catalog = catalog;
        _log = log;
        IsDownloaded = AppCatalogService.IsDownloaded(info);
    }

    public AppInfo Info { get; }
    public string Name => Info.Name;
    public string Title => string.IsNullOrWhiteSpace(Info.Version) ? Info.Name : $"{Info.Name}  v{Info.Version}";
    public string? Description => Info.Description;
    public bool HasFile => Info.FileName is not null;
    public bool HasNoFile => !HasFile;

    public string FileText => Info.FileName is null
        ? "No file uploaded yet"
        : Info.FileSize is { } size ? $"{Info.FileName}  ({FormatSize(size)})" : Info.FileName;

    [ObservableProperty] private ImageSource? _icon;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasIcon))] private bool _hasIconImage;
    public bool HasIcon => HasIconImage;
    public bool HasNoIcon => !HasIconImage;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PrimaryLabel))] private bool _isDownloaded;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanDownload))] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _status = "";

    public string PrimaryLabel => IsDownloaded ? "Run" : "Download";
    public bool CanDownload => HasFile && !IsBusy;

    public async Task LoadIconAsync()
    {
        try
        {
            var bytes = await _catalog.GetIconAsync(Info).ConfigureAwait(true);
            if (bytes is null) return;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            Icon = image;
            HasIconImage = true;
            OnPropertyChanged(nameof(HasNoIcon));
        }
        catch (Exception ex)
        {
            // SVG/WebP icons the dashboard accepts but WPF can't decode land here: placeholder is shown.
            _log.Append($"{(Info.Category == "driver" ? "Drivers" : Info.Category == "tool" ? "Tools" : "Apps")}: icon for {Name} not shown ({ex.Message})");
        }
    }

    [RelayCommand]
    private async Task Primary()
    {
        if (IsBusy) return;
        try
        {
            if (!IsDownloaded)
            {
                IsBusy = true;
                Progress = 0;
                Status = "Downloading...";
                _cts = new CancellationTokenSource();
                var path = await _catalog.DownloadAsync(Info, new Progress<double>(p =>
                {
                    Progress = p;
                    Status = $"Downloading... {p:0}%";
                }), _cts.Token);
                _log.Append($"{(Info.Category == "driver" ? "Drivers" : "Apps")}: downloaded {Name} to {path}");
                IsDownloaded = true;
                Status = "Downloaded and verified.";
                return;
            }

            var local = AppCatalogService.LocalPath(Info);
            _log.Append($"{(Info.Category == "driver" ? "Drivers" : "Apps")}: launching {local}");
            Process.Start(new ProcessStartInfo(local) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(local)! });
            Status = "Launched.";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            Status = "Launch cancelled at the Windows prompt.";
        }
        catch (Exception ex)
        {
            _log.Append($"{(Info.Category == "driver" ? "Drivers" : "Apps")}: {Name} failed - {ex.Message}");
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Re-checks the disk, e.g. after the downloads folder was cleared.</summary>
    public void RefreshDownloadState()
    {
        IsDownloaded = AppCatalogService.IsDownloaded(Info);
        Status = "";
        Progress = 0;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Path.GetDirectoryName(AppCatalogService.LocalPath(Info))!;
        if (Directory.Exists(folder)) Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.#} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.##} GB",
    };
}

/// <summary>Shared by the Apps and Drivers pages: lists one category from the server and manages its downloads.</summary>
public abstract partial class AppListViewModel : ObservableObject
{
    private readonly AppCatalogService _catalog;
    private readonly AppSettingsProvider _settings;
    private readonly LogService _log;

    protected AppListViewModel(AppCatalogService catalog, AppSettingsProvider settings, LogService log)
    {
        _catalog = catalog;
        _settings = settings;
        _log = log;
    }

    /// <summary>The server's category name: "app" or "driver".</summary>
    public abstract string Category { get; }
    public abstract string Title { get; }
    public abstract string Subtitle { get; }
    /// <summary>Lower-case plural used in messages, e.g. "apps".</summary>
    public abstract string Plural { get; }

    public string ClearLabel => $"Remove downloaded {Plural}";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsListView))] private bool _isGridView;
    public bool IsListView => !IsGridView;

    partial void OnIsGridViewChanged(bool value) => _catalog.PreferGridView = value;

    /// <summary>Picks up the list/grid choice made on another tab.</summary>
    public void SyncView() => IsGridView = _catalog.PreferGridView;

    [RelayCommand]
    private void ShowGrid() => IsGridView = true;

    [RelayCommand]
    private void ShowList() => IsGridView = false;

    public ObservableCollection<AppItemViewModel> Apps { get; } = new();

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsEmpty))] private bool _isLoading;
    [ObservableProperty] private string _message = "";
    public bool IsOffline => _settings.OfflineMode;
    public bool IsEmpty => !IsLoading && Apps.Count == 0;

    [RelayCommand]
    public async Task Refresh()
    {
        if (IsLoading) return;
        if (_settings.OfflineMode)
        {
            Message = "Offline mode is ON (Settings) - nothing is fetched from the server.";
            return;
        }

        IsLoading = true;
        Message = "";
        try
        {
            var list = await _catalog.ListAsync(Category);
            Apps.Clear();
            foreach (var info in list)
            {
                var item = new AppItemViewModel(info, _catalog, _log);
                Apps.Add(item);
                _ = item.LoadIconAsync();
            }
            if (list.Count == 0) Message = $"The server has no {Plural} yet. Add them on its {Title} page.";
        }
        catch (Exception ex)
        {
            _log.Append($"{Title}: could not load list - {ex.Message}");
            Message = $"Could not reach the server at {_settings.ServerBaseUrl}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    private async Task ClearDownloads()
    {
        if (Apps.Any(a => a.IsBusy))
        {
            Message = "Wait for the running download to finish first.";
            return;
        }

        var confirmed = await ThemedDialog.ConfirmAsync(
            ClearLabel,
            $"Delete every downloaded {Category} from this PC? They stay on the server and can be downloaded again.",
            "Delete", "Cancel");
        if (!confirmed) return;

        var (removed, failed) = AppCatalogService.ClearDownloads(Category);
        foreach (var item in Apps) item.RefreshDownloadState();

        Message = failed == 0
            ? $"Removed {removed} downloaded {Plural}."
            : $"Removed {removed}, but {failed} could not be deleted (still running or open?).";
        _log.Append($"{Title}: {Message}");
    }
}

public sealed class AppsViewModel : AppListViewModel
{
    public AppsViewModel(AppCatalogService catalog, AppSettingsProvider settings, LogService log)
        : base(catalog, settings, log) { }

    public override string Category => "app";
    public override string Title => "Apps";
    public override string Plural => "apps";
    public override string Subtitle =>
        "Tools and installers published on the Luxtronic server. Download one (it is checked against the server's checksum), then run it.";
}

public sealed class DriversViewModel : AppListViewModel
{
    public DriversViewModel(AppCatalogService catalog, AppSettingsProvider settings, LogService log)
        : base(catalog, settings, log) { }

    public override string Category => "driver";
    public override string Title => "Drivers";
    public override string Plural => "drivers";
    public override string Subtitle =>
        "Drivers published on the Luxtronic server. Download one (it is checked against the server's checksum), then run it.";
}

public sealed class ToolsViewModel : AppListViewModel
{
    public ToolsViewModel(AppCatalogService catalog, AppSettingsProvider settings, LogService log)
        : base(catalog, settings, log) { }

    public override string Category => "tool";
    public override string Title => "Tools";
    public override string Plural => "tools";
    public override string Subtitle =>
        "Utilities published on the Luxtronic server. Download one (it is checked against the server's checksum), then run it.";
}
