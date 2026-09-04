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

    /// <summary>
    /// Levels the progressive scanner lists before it starts the deep walk. Two levels of a
    /// whole drive come back in about a tenth of a second, which is what lets the Explorer page
    /// paint something real immediately instead of a status line.
    /// </summary>
    public int ShallowDepth { get; set; } = 2;

    /// <summary>
    /// Adopt a cached directory whose own timestamp has not moved, instead of listing it again.
    ///
    /// Off by default, and it is a trade rather than a free win: a directory timestamp moves
    /// when an entry is added, removed or renamed, but not when a file already inside it grows.
    /// So this reports a log file that swelled from one megabyte to five gigabytes at its old
    /// size. It is worth having anyway for package caches and node_modules, which are written
    /// once and never edited, and adopted subtrees stay marked as estimates for the whole scan.
    /// </summary>
    public bool TrustUnchangedFolders { get; set; }

    /// <summary>Directories between progress reports, to keep the UI thread from drowning.</summary>
    public int ProgressInterval { get; set; } = 64;
}
