namespace DiskSpace.Core.Scanning;

public sealed class ScanResult
{
    public required DirectoryNode Root { get; init; }
    public required IReadOnlyList<ScanIssue> Issues { get; init; }
    public required TimeSpan Duration { get; init; }

    /// <summary>
    /// False when the walk stopped early, so the totals are a floor rather than the answer.
    /// Only the progressive scanner can return false: the blocking one throws instead.
    /// </summary>
    public bool IsComplete { get; init; } = true;
    public long TotalSize => Root.TotalSize;
    public long TotalFileCount => Root.TotalFileCount;
    public int TotalDirectoryCount => Root.TotalDirectoryCount;
}
