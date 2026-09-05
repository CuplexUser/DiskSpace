namespace DiskSpace.Core.Updates;

/// <summary>A release newer than the running build: what to show, and where it downloads from.</summary>
public sealed record UpdateInfo(string Version, string ReleaseUrl, string? DownloadUrl, bool Prerelease);

public enum UpdateCheckStatus
{
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>
/// What asking GitHub came back with. <see cref="Failed"/> is kept distinct from
/// <see cref="UpToDate"/> so a manual "check now" can tell the user their connection dropped
/// instead of pretending nothing has changed.
/// </summary>
public sealed record UpdateCheckResult(UpdateCheckStatus Status, UpdateInfo? Update = null, string? Error = null)
{
    public static UpdateCheckResult UpToDate() => new(UpdateCheckStatus.UpToDate);

    public static UpdateCheckResult Available(UpdateInfo update) => new(UpdateCheckStatus.UpdateAvailable, update);

    public static UpdateCheckResult Failed(string error) => new(UpdateCheckStatus.Failed, Error: error);
}
