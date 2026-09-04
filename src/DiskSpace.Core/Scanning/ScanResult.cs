namespace DiskSpace.Core.Scanning;

public sealed class ScanResult
{
    public required DirectoryNode Root { get; init; }
    public required IReadOnlyList<ScanIssue> Issues { get; init; }
    public required TimeSpan Duration { get; init; }
    public long TotalSize => Root.TotalSize;
    public long TotalFileCount => Root.TotalFileCount;
    public int TotalDirectoryCount => Root.TotalDirectoryCount;
}
