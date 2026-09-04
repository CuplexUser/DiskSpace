using System.IO.Compression;

namespace DiskSpace.Core.Quarantine;

public enum QuarantineMode
{
    /// <summary>
    /// Pack the folder into a single archive on another volume. Frees the source volume
    /// immediately; costs one sequential write.
    /// </summary>
    ArchiveToOtherVolume,

    /// <summary>
    /// Rename the folder aside on its own volume. Instant at any file count, but reclaims
    /// nothing until the item is purged.
    /// </summary>
    MoveOnSameVolume,
}

public sealed class QuarantineOptions
{
    public QuarantineMode Mode { get; set; } = QuarantineMode.ArchiveToOtherVolume;

    /// <summary>
    /// Where archives are written. Null means "choose the volume with the most free space that
    /// is not the source volume", which is the point of archiving in the first place.
    /// </summary>
    public string? Location { get; set; }

    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Leftover application data is mostly text and compresses well, and writing fewer bytes
    /// usually beats the CPU cost. <see cref="CompressionLevel.NoCompression"/> is the option
    /// for someone who would rather spend the disk.
    /// </summary>
    public CompressionLevel Compression { get; set; } = CompressionLevel.Fastest;

    /// <summary>Above either threshold, the UI warns that archiving will take a while.</summary>
    public int SlowFileCountThreshold { get; set; } = 10_000;

    public long SlowSizeThreshold { get; set; } = 1024L * 1024 * 1024;

    public static string DefaultFolderName => "DiskSpaceQuarantine";

    /// <summary>
    /// Picks the roomiest fixed volume that is not the source. Falls back to null when there is
    /// no such volume, which forces the caller onto a same-volume move rather than filling the
    /// disk it is trying to empty.
    /// </summary>
    public static string? ChooseLocation(string sourcePath)
    {
        try
        {
            var sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath));

            var best = DriveInfo.GetDrives()
                .Where(d => d is { IsReady: true, DriveType: DriveType.Fixed })
                .Where(d => !string.Equals(
                    d.RootDirectory.FullName, sourceRoot, StringComparison.OrdinalIgnoreCase))
                .MaxBy(d => d.AvailableFreeSpace);

            return best is null
                ? null
                : Path.Combine(best.RootDirectory.FullName, DefaultFolderName);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
