using System.IO.Enumeration;

namespace DiskSpace.Core.Scanning;

/// <summary>What one directory listing produced. Totals are the caller's job.</summary>
internal readonly record struct DirectoryReading(
    string Path,
    long OwnSize,
    int OwnFileCount,
    DateTime NewestEntryUtc,
    DirectoryNode[] Children,
    string? Error,
    string? IssueReason,
    bool Vanished);

/// <summary>
/// Lists exactly one directory.
///
/// Split out of <see cref="FastDirectoryScanner"/> so the blocking scanner and the progressive
/// one share a single walker. Two implementations of this would be two sets of rules about
/// junctions, access denials and hidden files, and they would drift.
/// </summary>
internal static class DirectoryReader
{
    /// <summary>Entries between cancellation checks inside one directory.</summary>
    private const int CancellationCheckInterval = 1024;

    /// <summary>ERROR_INVALID_REPARSE_DATA, as an IOException HResult.</summary>
    private const int InvalidReparseData = unchecked((int)0x80071128);

    internal static readonly EnumerationOptions EnumOptions = new()
    {
        RecurseSubdirectories = false,
        // Deliberately false: an unreadable directory should become a recorded issue,
        // not vanish silently and quietly understate the total.
        IgnoreInaccessible = false,
        // Hidden and system files occupy disk like any other.
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    private readonly record struct RawEntry(
        string Name,
        long Length,
        bool IsDirectory,
        bool IsReparsePoint,
        DateTime LastWriteUtc);

    internal static DirectoryReading Read(
        DirectoryNode node, ScanOptions options, CancellationToken cancellationToken)
    {
        var path = node.Path;
        var children = new List<DirectoryNode>();
        long ownSize = 0;
        var ownFiles = 0;
        var newest = DateTime.MinValue;
        var seen = 0;

        try
        {
            var entries = new FileSystemEnumerable<RawEntry>(
                path,
                static (ref FileSystemEntry entry) => new RawEntry(
                    entry.IsDirectory ? entry.FileName.ToString() : string.Empty,
                    entry.IsDirectory ? 0L : entry.Length,
                    entry.IsDirectory,
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                    entry.LastWriteTimeUtc.UtcDateTime),
                EnumOptions);

            foreach (var entry in entries)
            {
                // A single directory can hold a million entries. Checking only between
                // directories, as the level-synchronous scanner used to, made Cancel do
                // nothing at all for as long as one of those took to list.
                if (++seen % CancellationCheckInterval == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                if (entry.LastWriteUtc > newest)
                    newest = entry.LastWriteUtc;

                if (!entry.IsDirectory)
                {
                    ownSize += entry.Length;
                    ownFiles++;
                    continue;
                }

                if (options.ExcludedPaths.Count > 0
                    && options.ExcludedPaths.Contains(Path.Combine(path, entry.Name)))
                {
                    continue;
                }

                var child = new DirectoryNode(node, entry.Name)
                {
                    IsReparsePoint = entry.IsReparsePoint,
                    OwnLastWriteUtc = entry.LastWriteUtc,
                };

                // Seeds the subtree maximum with the directory's own timestamp, so an empty
                // folder still reports when it was last touched rather than reporting nothing.
                child.SetLastWrite(entry.LastWriteUtc);
                children.Add(child);
            }
        }
        catch (DirectoryNotFoundException) when (!Directory.Exists(path))
        {
            // Removed between being discovered and being listed. Under the progressive scanner
            // that window is minutes rather than milliseconds, so on a live machine this is
            // ordinary churn; reporting it as an issue would bury the real access denials.
            return new DirectoryReading(
                path, 0, 0, DateTime.MinValue, [], null, null, Vanished: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                     or DirectoryNotFoundException
                                     or IOException
                                     or System.Security.SecurityException)
        {
            // Bytes counted before the throw are kept: a partly readable directory still
            // reports what it managed, and the issue records that the rest is missing.
            return new DirectoryReading(
                path,
                ownSize,
                ownFiles,
                newest,
                [.. children],
                ex.Message,
                Describe(ex),
                Vanished: false);
        }

        return new DirectoryReading(
            path, ownSize, ownFiles, newest, [.. children], null, null, Vanished: false);
    }

    /// <summary>Whether a discovered child is worth descending into.</summary>
    internal static bool ShouldDescend(DirectoryNode child, ScanOptions options) =>
        !child.IsReparsePoint || options.FollowReparsePoints;

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied",
        DirectoryNotFoundException => "Removed during scan",
        PathTooLongException => "Path too long",
        // Left by tools that hard-link aggressively, pnpm's content store among them. The OS
        // message for this one arrives in the display language, which reads as gibberish in an
        // otherwise English list of issues.
        IOException { HResult: InvalidReparseData } => "Damaged reparse point",
        _ => ex.Message,
    };
}
