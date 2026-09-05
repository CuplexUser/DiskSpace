using DiskSpace.App.Dialogs;
using DiskSpace.Core.Settings;
using DiskSpace.Core.Updates;

namespace DiskSpace.App.Updates;

/// <summary>
/// The one place that decides when a GitHub check should actually interrupt the user. The
/// silent, startup path stays quiet about everything except a release that is both newer and
/// not already dismissed; a manual "check now" reports whatever it finds, including failure.
///
/// Methods here are awaited without <c>ConfigureAwait(false)</c> on purpose: WinForms installs
/// a synchronization context, so control returns to the UI thread afterwards and callers can
/// touch controls or show a dialog directly.
/// </summary>
public sealed class AppUpdateManager(AppSettings settings) : IDisposable
{
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(24);

    private readonly GitHubUpdateChecker _checker = new();

    public async Task CheckSilentlyAsync(Form owner)
    {
        if (!settings.CheckForUpdatesAutomatically)
            return;

        if (settings.LastUpdateCheckUtc is { } last && DateTime.UtcNow - last < AutoCheckInterval)
            return;

        var result = await _checker.CheckAsync(AppVersion.Current);
        RecordCheck();

        if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Update is not { } update)
            return;

        if (update.Version == settings.SkippedUpdateVersion)
            return;

        if (owner.IsDisposed)
            return;

        ShowAvailable(owner, update);
    }

    public async Task<UpdateCheckResult> CheckNowAsync(IWin32Window? owner)
    {
        var result = await _checker.CheckAsync(AppVersion.Current);
        RecordCheck();

        if (result is { Status: UpdateCheckStatus.UpdateAvailable, Update: { } update })
            ShowAvailable(owner, update);

        return result;
    }

    private void RecordCheck()
    {
        settings.LastUpdateCheckUtc = DateTime.UtcNow;
        settings.Save();
    }

    private void ShowAvailable(IWin32Window? owner, UpdateInfo update)
    {
        using var dialog = new UpdateAvailableDialog(AppVersion.Current, update);
        var result = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);

        if (result == DialogResult.Ignore)
        {
            settings.SkippedUpdateVersion = update.Version;
            settings.Save();
        }
    }

    public void Dispose() => _checker.Dispose();
}
