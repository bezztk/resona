using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Velopack;
using Velopack.Sources;

namespace Resona.Services;

public sealed class AppUpdateService
{
    private const string RepositoryUrl = "https://github.com/bezztk/resona";
    private readonly UpdateManager _updateManager = new(
        new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private UpdateInfo? _availableUpdate;
    private bool _hasCheckedThisSession;

    public static AppUpdateService Current { get; } = new();

    public AppUpdateState State { get; private set; }

    public event Action<AppUpdateState>? StateChanged;

    private AppUpdateService()
    {
        var currentVersion = _updateManager.CurrentVersion?.ToString() ?? GetAssemblyVersion();
        var pendingUpdate = _updateManager.UpdatePendingRestart;
        State = pendingUpdate is null
            ? new AppUpdateState(
                currentVersion,
                null,
                _updateManager.IsInstalled ? AppUpdatePhase.Idle : AppUpdatePhase.NotInstalled,
                _updateManager.IsInstalled
                    ? "Ready to check for updates."
                    : "Update checks are available after installing Resona with its setup.",
                0)
            : new AppUpdateState(
                currentVersion,
                pendingUpdate.Version.ToString(),
                AppUpdatePhase.ReadyToInstall,
                $"Version {pendingUpdate.Version} is ready to install.",
                100);
    }

    public async Task CheckForUpdatesAsync(bool force = false)
    {
        if (!_updateManager.IsInstalled || (!force && _hasCheckedThisSession))
            return;

        await _operationLock.WaitAsync();
        try
        {
            if (!force && _hasCheckedThisSession)
                return;

            _hasCheckedThisSession = true;
            SetState(State with
            {
                Phase = AppUpdatePhase.Checking,
                Message = "Checking GitHub for updates…",
                ProgressPercent = 0
            });

            _availableUpdate = await _updateManager.CheckForUpdatesAsync();
            if (_availableUpdate is null)
            {
                SetState(State with
                {
                    AvailableVersion = null,
                    Phase = AppUpdatePhase.UpToDate,
                    Message = $"Resona {State.CurrentVersion} is up to date.",
                    ProgressPercent = 0
                });
                return;
            }

            var version = _availableUpdate.TargetFullRelease.Version.ToString();
            SetState(State with
            {
                AvailableVersion = version,
                Phase = AppUpdatePhase.UpdateAvailable,
                Message = $"Version {version} is available.",
                ProgressPercent = 0
            });
        }
        catch (Exception exception)
        {
            SetState(State with
            {
                Phase = AppUpdatePhase.Failed,
                Message = $"Update check failed: {exception.Message}",
                ProgressPercent = 0
            });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task DownloadUpdateAsync()
    {
        if (_availableUpdate is null || !_updateManager.IsInstalled)
            return;

        await _operationLock.WaitAsync();
        try
        {
            var update = _availableUpdate;
            SetState(State with
            {
                Phase = AppUpdatePhase.Downloading,
                Message = $"Downloading version {update.TargetFullRelease.Version}…",
                ProgressPercent = 0
            });

            await _updateManager.DownloadUpdatesAsync(update, progress =>
            {
                SetState(State with
                {
                    Phase = AppUpdatePhase.Downloading,
                    Message = $"Downloading version {update.TargetFullRelease.Version}… {progress}%",
                    ProgressPercent = progress
                });
            });

            SetState(State with
            {
                Phase = AppUpdatePhase.ReadyToInstall,
                Message = $"Version {update.TargetFullRelease.Version} is ready to install.",
                ProgressPercent = 100
            });
        }
        catch (Exception exception)
        {
            SetState(State with
            {
                Phase = AppUpdatePhase.Failed,
                Message = $"Update download failed: {exception.Message}"
            });
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void ApplyUpdateAndRestart()
    {
        if (State.Phase != AppUpdatePhase.ReadyToInstall)
            return;

        var update = _availableUpdate?.TargetFullRelease ?? _updateManager.UpdatePendingRestart;
        if (update is null)
            return;

        _updateManager.WaitExitThenApplyUpdates(update, restart: true);

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
        else
            Environment.Exit(0);
    }

    private void SetState(AppUpdateState state)
    {
        State = state;
        StateChanged?.Invoke(state);
    }

    private static string GetAssemblyVersion()
    {
        var informationalVersion = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        return string.IsNullOrWhiteSpace(informationalVersion)
            ? "unknown"
            : informationalVersion.Split('+', 2)[0];
    }
}

public sealed record AppUpdateState(
    string CurrentVersion,
    string? AvailableVersion,
    AppUpdatePhase Phase,
    string Message,
    int ProgressPercent);

public enum AppUpdatePhase
{
    NotInstalled,
    Idle,
    Checking,
    UpToDate,
    UpdateAvailable,
    Downloading,
    ReadyToInstall,
    Failed
}
