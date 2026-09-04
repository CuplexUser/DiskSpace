namespace DiskSpace.Core.Scanning;

public sealed class ScanOptions
{
    /// <summary>Worker count. Defaults to the processor count, which suits an SSD.</summary>
    public int MaxDegreeOfParallelism { get; set; } = Environment.ProcessorCount;

    /// <summary>
    /// Junctions and symlinks are skipped by default. Legacy profiles carry loops such as
    /// <c>AppData\Local\Application Data</c> that otherwise never terminate.
    /// </summary>
    public bool FollowReparsePoints { get; set; }

    /// <summary>Absolute paths to skip entirely, compared case-insensitively.</summary>
    public HashSet<string> ExcludedPaths { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories between progress reports, to keep the UI thread from drowning.</summary>
    public int ProgressInterval { get; set; } = 64;
}
