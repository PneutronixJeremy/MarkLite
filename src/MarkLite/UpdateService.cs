using System;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace MarkLite;

/*  Update flow: check GitHub Releases in the background shortly after startup
    (and on demand from the Help menu), download silently, then let the window
    offer "Restart to update". Rendering never blocks on any of this; offline
    or rate-limited checks fail silently into the debug log.

    MARKLITE_UPDATE_URL overrides the source with a plain URL or local
    directory path — used by the packaging tests to run the whole
    check→download→apply loop against a local Releases folder. */
internal sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/PneutronixJeremy/MarkLite";

    private readonly UpdateManager _manager;

    internal UpdateService()
    {
        var overrideSource = Environment.GetEnvironmentVariable("MARKLITE_UPDATE_URL");
        _manager = string.IsNullOrEmpty(overrideSource)
            ? new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false))
            : new UpdateManager(overrideSource);
        if (!string.IsNullOrEmpty(overrideSource))
        {
            DebugLog.Write($"update source override: {overrideSource}");
        }
    }

    /// <summary>False when running unpackaged (dev build, plain publish output) — updates need an installed copy.</summary>
    internal bool IsInstalled => _manager.IsInstalled;

    internal string CurrentVersion => _manager.CurrentVersion?.ToString() ?? "0.0.0-dev";

    /// <summary>The update that has been downloaded and is waiting for a restart, if any.</summary>
    internal UpdateInfo? Pending { get; private set; }

    /// <summary>Checks and, when an update exists, downloads it. Returns the update or null. Never throws.</summary>
    internal async Task<UpdateInfo?> CheckAndDownloadAsync()
    {
        if (!_manager.IsInstalled)
        {
            DebugLog.Write("update check skipped: not an installed copy");
            return null;
        }
        if (Pending is not null)
        {
            return Pending;
        }

        try
        {
            DebugLog.Write($"update check started (current {CurrentVersion})");
            var update = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null)
            {
                DebugLog.Write("update check: up to date");
                return null;
            }

            DebugLog.Write($"update found: {update.TargetFullRelease.Version}; downloading");
            await _manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
            DebugLog.Write($"update downloaded: {update.TargetFullRelease.Version}");
            Pending = update;
            return update;
        }
        catch (Exception ex)
        {
            // Offline, rate limit, no releases yet — all silent no-ops by design.
            DebugLog.Write($"update check failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Applies the pending update now and restarts the app.</summary>
    internal void RestartToApply()
    {
        if (Pending is not null)
        {
            DebugLog.Write($"update applying now, restarting into {Pending.TargetFullRelease.Version}");
            _manager.ApplyUpdatesAndRestart(Pending);
        }
    }

    /// <summary>Arranges for the pending update to apply after the process exits (user closed normally).</summary>
    internal void ApplyOnExit()
    {
        if (Pending is not null)
        {
            DebugLog.Write($"update will apply on exit: {Pending.TargetFullRelease.Version}");
            _manager.WaitExitThenApplyUpdates(Pending);
        }
    }
}
