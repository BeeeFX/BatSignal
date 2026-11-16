using Velopack;
using Velopack.Sources;

namespace BatSignal.App;

/// <summary>
/// Self-update from GitHub Releases (Velopack). Only active in an installed copy: checks quietly,
/// downloads in the background, then the panel offers "Restart to update".
/// </summary>
internal sealed class Updater
{
    public const string RepoUrl = "https://github.com/BeeeFX/BatSignal";

    readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    UpdateInfo? _ready;
    bool _checking;

    /// <summary>Raised on the UI thread when an update has been downloaded and can be applied.</summary>
    public event Action<string>? UpdateReady;

    public bool IsInstalled => _manager.IsInstalled;

    public async Task CheckAsync()
    {
        if (!_manager.IsInstalled || _ready is not null || _checking) return;
        _checking = true;
        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) return;
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            var version = info.TargetFullRelease.Version.ToString();
            Logger.Write($"Update {version} downloaded");
            UpdateReady?.Invoke(version);
        }
        catch (Exception e)
        {
            Logger.Write($"Update check failed: {e.Message}");
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Exits, applies the downloaded update and starts the new version.</summary>
    public void ApplyAndRestart()
    {
        if (_ready is not null) _manager.ApplyUpdatesAndRestart(_ready.TargetFullRelease);
    }
}
