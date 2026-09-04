namespace DiskSpace.Core.Scanning;

/// <summary>Snapshot of an in-flight scan, reported on a throttled cadence.</summary>
public readonly record struct ScanProgress(
    long DirectoriesScanned,
    long FilesSeen,
    long BytesSeen,
    string CurrentPath);

/// <summary>A location the scan could not read, kept rather than thrown.</summary>
public readonly record struct ScanIssue(string Path, string Reason);
