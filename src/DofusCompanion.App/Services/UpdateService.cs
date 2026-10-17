using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DofusCompanion.App.Localization;
using DofusCompanion.Application.Settings;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace DofusCompanion.App.Services;

/// <summary>
/// Automatic updates from the GitHub releases of the project (Velopack): at startup, if allowed in the settings,
/// the app asks GitHub whether a newer version exists and offers it; one click downloads it (only the changed
/// parts when possible), closes the app, replaces it and restarts it. The user's data folder is not touched.
/// Only an installed copy (Setup.exe) can update itself; a copy run from the zip just says so.
/// </summary>
public sealed partial class UpdateService(ISettingsStore settings, StatusService status, ILogger<UpdateService> logger) : ObservableObject
{
    public const string RepositoryUrl = "https://github.com/nbk265117/goldboss";

    private UpdateManager? _manager;
    private UpdateInfo? _pending;

    /// <summary>Closes the app cleanly before the update is applied (wired by the shell).</summary>
    public Action? Quit { get; set; }

    [ObservableProperty] public partial bool IsAvailable { get; set; }
    [ObservableProperty] public partial string? AvailableVersion { get; set; }
    [ObservableProperty] public partial string Banner { get; set; } = "";
    [ObservableProperty] public partial bool IsDownloading { get; set; }
    [ObservableProperty] public partial int Progress { get; set; }

    // DGB_UPDATE_SOURCE (a local releases folder) is for testing an update end to end before publishing it.
    private UpdateManager Manager => _manager ??= Environment.GetEnvironmentVariable("DGB_UPDATE_SOURCE") is { Length: > 0 } local
        ? new UpdateManager(local)
        : new UpdateManager(new GithubSource(RepositoryUrl, null, false));

    /// <summary>Startup check, silent unless an update is found.</summary>
    public Task CheckAtStartupAsync() => settings.Current.CheckForUpdates ? CheckAsync(false) : Task.CompletedTask;

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync(true);

    private async Task CheckAsync(bool userRequested)
    {
        try
        {
            if (!Manager.IsInstalled)
            {
                if (userRequested) status.Info(Loc.Instance["Update.NotInstalled"]);
                return;
            }

            var info = await Manager.CheckForUpdatesAsync();
            if (info is null)
            {
                if (userRequested) status.Info(Loc.Instance.Format("Update.UpToDate", AppInfo.Version));
                return;
            }

            _pending = info;
            AvailableVersion = info.TargetFullRelease.Version.ToString();
            Banner = Loc.Instance.Format("Update.Available", AvailableVersion);
            IsAvailable = true;
            status.Info(Banner);
            logger.LogInformation("Update available: {Version}", AvailableVersion);
        }
        catch (Exception ex)
        {
            // No network, GitHub unreachable or rate-limited: never blocks the app.
            logger.LogWarning(ex, "Update check failed");
            if (userRequested) status.Error(Loc.Instance.Format("Update.Failed", ex.Message));
        }
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_pending is null || IsDownloading) return;
        IsDownloading = true;
        try
        {
            status.Info(Loc.Instance.Format("Update.Downloading", AvailableVersion));
            await Manager.DownloadUpdatesAsync(_pending, p => Progress = p);
            logger.LogInformation("Update {Version} downloaded, restarting", AvailableVersion);
            Manager.WaitExitThenApplyUpdates(_pending.TargetFullRelease, silent: false, restart: true);
            Quit?.Invoke();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Update download failed");
            status.Error(Loc.Instance.Format("Update.Failed", ex.Message));
        }
        finally
        {
            IsDownloading = false;
        }
    }
}
