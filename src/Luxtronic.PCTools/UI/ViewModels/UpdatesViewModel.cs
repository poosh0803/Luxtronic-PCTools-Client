using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Luxtronic.PCTools.Services;
using Luxtronic.PCTools.Services.Shell;

namespace Luxtronic.PCTools.UI.ViewModels;

/// <summary>One published client release on the Updates page.</summary>
public sealed partial class ReleaseItemViewModel : ObservableObject
{
    private readonly UpdatesViewModel _parent;

    public ReleaseItemViewModel(AppInfo info, bool isLatest, bool isRunning, UpdatesViewModel parent)
    {
        Info = info;
        IsLatest = isLatest;
        IsRunning = isRunning;
        _parent = parent;
    }

    public AppInfo Info { get; }
    public bool IsLatest { get; }
    public bool IsRunning { get; }

    public string VersionText => $"v{Info.Version}";
    public string Notes => string.IsNullOrWhiteSpace(Info.Description) ? "No release notes." : Info.Description!;
    public string UploadedText => Info.CreatedAt.ToLocalTime().ToString("g");

    public string FileText => Info.FileName is null
        ? "No file uploaded"
        : Info.FileSize is { } size ? $"{size / 1024.0 / 1024:0.#} MB" : Info.FileName;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanInstall))] private bool _isBusy;

    public bool CanInstall => !IsBusy && !IsRunning && Info.FileName is not null;
    public string InstallLabel => IsRunning ? "Installed" : "Install";

    [RelayCommand]
    private Task Install() => _parent.InstallAsync(this);
}

/// <summary>
/// The Updates tab: the PC client builds published on the server (Publish page). Any release can be
/// installed - the newest to update, an older one to roll back - which downloads it (checksum
/// verified), then closes the app, swaps the exe and relaunches.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly UpdateService _updates;
    private readonly AppSettingsProvider _settings;
    private readonly ControllerProvider _controllers;
    private readonly LogService _log;
    private CancellationTokenSource? _cts;

    public UpdatesViewModel(UpdateService updates, AppSettingsProvider settings, ControllerProvider controllers, LogService log)
    {
        _updates = updates;
        _settings = settings;
        _controllers = controllers;
        _log = log;
    }

    public ObservableCollection<ReleaseItemViewModel> Releases { get; } = new();

    public bool IsOffline => _settings.OfflineMode;
    public bool CanSelfUpdate => UpdateService.CanSelfUpdate;

    [ObservableProperty] private bool _isChecking;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsNotInstalling))] private bool _isInstalling;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _message = "";

    /// <summary>What is running: the matching published release, or a note that it was never published.</summary>
    [ObservableProperty] private string _runningText = "Checking...";

    [ObservableProperty] private string _latestText = "";
    [ObservableProperty] private bool _updateAvailable;

    public bool IsNotInstalling => !IsInstalling;

    [RelayCommand]
    public async Task Check()
    {
        if (IsChecking || IsInstalling) return;
        if (_settings.OfflineMode)
        {
            Message = "Offline mode is ON (Settings) - not checking the server.";
            RunningText = "Offline";
            return;
        }

        IsChecking = true;
        Message = "";
        try
        {
            var result = await _updates.CheckAsync();
            Releases.Clear();
            foreach (var release in result.Releases)
            {
                var isRunning = result.Running is not null &&
                                string.Equals(release.FileSha256, result.Running.FileSha256, StringComparison.OrdinalIgnoreCase) &&
                                release.FileName is not null;
                // Identical files published twice: mark only the newest of them as the running one.
                if (isRunning && Releases.Any(r => r.IsRunning)) isRunning = false;
                Releases.Add(new ReleaseItemViewModel(release, ReferenceEquals(release, result.Latest), isRunning, this));
            }

            RunningText = result.Running is not null
                ? $"This PC is running v{result.Running.Version}."
                : "This copy is not a published release (for example a local build).";
            LatestText = result.Latest is not null
                ? $"Latest published: v{result.Latest.Version}, uploaded {result.Latest.CreatedAt.ToLocalTime():g}."
                : "No release has been published yet.";
            UpdateAvailable = result.UpdateAvailable;
            if (result.UpdateAvailable) Message = $"A newer version is available: v{result.Latest!.Version}.";
            else if (result.Running is not null) Message = "You are on the latest version.";
        }
        catch (Exception ex)
        {
            _log.Append($"Updates: could not check - {ex.Message}");
            RunningText = "Unknown";
            Message = $"Could not reach the server at {_settings.ServerBaseUrl}: {ex.Message}";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private Task InstallLatest()
    {
        var latest = Releases.FirstOrDefault(r => r.IsLatest);
        return latest is null ? Task.CompletedTask : InstallAsync(latest);
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    public async Task InstallAsync(ReleaseItemViewModel item)
    {
        if (IsInstalling) return;
        if (!UpdateService.CanSelfUpdate)
        {
            Message = "This copy was started through the .NET host and cannot replace itself. Run the published exe.";
            return;
        }

        if (_controllers.AnyRunning)
        {
            Message = "A test is running - wait for it to finish before updating.";
            return;
        }

        var confirmed = await ThemedDialog.ConfirmAsync(
            $"Install {item.VersionText}",
            $"Download {item.VersionText} ({item.FileText}) and install it?\n\n" +
            "The app will close and start again by itself. Your settings are kept.",
            "Install", "Cancel");
        if (!confirmed) return;

        IsInstalling = true;
        foreach (var r in Releases) r.IsBusy = true;
        Progress = 0;
        Message = $"Downloading {item.VersionText}...";
        _cts = new CancellationTokenSource();
        try
        {
            var error = await _updates.InstallAsync(item.Info, new Progress<double>(p =>
            {
                Progress = p;
                Message = $"Downloading {item.VersionText}... {p:0}%";
            }), _cts.Token);

            if (error is not null)
            {
                Message = error;
                _log.Append($"Updates: {error}");
                return;
            }

            Message = "Installing - the app will restart...";
            // The normal shutdown (stops HWiNFO, releases the log) runs, then the helper swaps the exe.
            Application.Current.MainWindow?.Close();
        }
        catch (OperationCanceledException)
        {
            Message = "Cancelled.";
        }
        catch (Exception ex)
        {
            _log.Append($"Updates: install of {item.VersionText} failed - {ex.Message}");
            Message = ex.Message;
        }
        finally
        {
            IsInstalling = false;
            foreach (var r in Releases) r.IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }
}
