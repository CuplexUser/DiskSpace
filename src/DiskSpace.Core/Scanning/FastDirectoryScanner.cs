using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace DiskSpace.Core.Scanning;

/// <summary>
/// Recursive directory sizer built on <see cref="FileSystemEnumerable{TResult}"/>.
///
/// The transform reads Length, Attributes and LastWriteTime straight off the
/// <see cref="FileSystemEntry"/> struct, so walking a profile with a million files allocates
/// no <c>FileInfo</c> objects. Recursion is driven level by level rather than by
/// <c>RecurseSubdirectories</c>, which keeps parallelism, cancellation and reparse-point
/// skipping under our own control.
/// </summary>
public sealed class FastDirectoryScanner(ScanOptions? options = null)
{
    private readonly ScanOptions _options = options ?? new ScanOptions();

    private static readonly EnumerationOptions EnumOptions = new()
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

    private sealed class Counters
    {
        public long Directories;
        public long Files;
        public long Bytes;
    }

    public async Task<ScanResult> ScanAsync(
        string rootPath,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Not a directory: {fullPath}");

        var stopwatch = Stopwatch.StartNew();
        var root = new DirectoryNode(fullPath, null);
        var issues = new ConcurrentBag<ScanIssue>();
        var counters = new Counters();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, _options.MaxDegreeOfParallelism),
            CancellationToken = cancellationToken,
        };

        List<DirectoryNode> currentLevel = [root];

        while (currentLevel.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var nextLevel = new ConcurrentBag<DirectoryNode>();

            await Parallel.ForEachAsync(currentLevel, parallelOptions, (node, _) =>
            {
                ScanOneDirectory(node, nextLevel, issues, counters, progress);
                return ValueTask.CompletedTask;
            }).ConfigureAwait(false);

            currentLevel = [.. nextLevel];
        }

        RollUp(root);
        stopwatch.Stop();

        progress?.Report(new ScanProgress(
            counters.Directories, counters.Files, counters.Bytes, fullPath));

        return new ScanResult
        {
            Root = root,
            Issues = [.. issues],
            Duration = stopwatch.Elapsed,
        };
    }

    private void ScanOneDirectory(
        DirectoryNode node,
        ConcurrentBag<DirectoryNode> nextLevel,
        ConcurrentBag<ScanIssue> issues,
        Counters counters,
        IProgress<ScanProgress>? progress)
    {
        long ownSize = 0;
        var ownFiles = 0;
        var lastWrite = DateTime.MinValue;

        try
        {
            var entries = new FileSystemEnumerable<RawEntry>(
                node.Path,
                static (ref FileSystemEntry entry) => new RawEntry(
                    entry.IsDirectory ? entry.FileName.ToString() : string.Empty,
                    entry.IsDirectory ? 0L : entry.Length,
                    entry.IsDirectory,
                    (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                    entry.LastWriteTimeUtc.UtcDateTime),
                EnumOptions);

            foreach (var entry in entries)
            {
                if (entry.LastWriteUtc > lastWrite)
                    lastWrite = entry.LastWriteUtc;

                if (!entry.IsDirectory)
                {
                    ownSize += entry.Length;
                    ownFiles++;
                    continue;
                }

                var childPath = Path.Combine(node.Path, entry.Name);
                if (_options.ExcludedPaths.Contains(childPath))
                    continue;

                var child = new DirectoryNode(childPath, node)
                {
                    IsReparsePoint = entry.IsReparsePoint,
                    LastWriteUtc = entry.LastWriteUtc,
                };
                node.Children.Add(child);

                // A junction contributes no bytes of its own; following it would double-count
                // at best and loop forever at worst.
                if (entry.IsReparsePoint && !_options.FollowReparsePoints)
                    continue;

                nextLevel.Add(child);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                     or DirectoryNotFoundException
                                     or IOException
                                     or System.Security.SecurityException)
        {
            node.Error = ex.Message;
            issues.Add(new ScanIssue(node.Path, Describe(ex)));
        }

        node.OwnSize = ownSize;
        node.OwnFileCount = ownFiles;
        if (lastWrite > node.LastWriteUtc)
            node.LastWriteUtc = lastWrite;

        var scanned = Interlocked.Increment(ref counters.Directories);
        Interlocked.Add(ref counters.Files, ownFiles);
        Interlocked.Add(ref counters.Bytes, ownSize);

        if (progress is not null && scanned % _options.ProgressInterval == 0)
        {
            progress.Report(new ScanProgress(
                scanned,
                Interlocked.Read(ref counters.Files),
                Interlocked.Read(ref counters.Bytes),
                node.Path));
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "Access denied",
        DirectoryNotFoundException => "Removed during scan",
        PathTooLongException => "Path too long",
        _ => ex.Message,
    };

    /// <summary>
    /// Post-order roll-up over an explicit stack. A recursive version stack-overflows on the
    /// deep node_modules and package-cache trees this tool exists to find.
    /// </summary>
    internal static void RollUp(DirectoryNode root)
    {
        var stack = new Stack<(DirectoryNode Node, bool ChildrenDone)>();
        stack.Push((root, false));

        while (stack.Count > 0)
        {
            var (node, childrenDone) = stack.Pop();

            if (!childrenDone)
            {
                stack.Push((node, true));
                foreach (var child in node.Children)
                    stack.Push((child, false));
                continue;
            }

            var size = node.OwnSize;
            long files = node.OwnFileCount;
            var dirs = 0;

            foreach (var child in node.Children)
            {
                size += child.TotalSize;
                files += child.TotalFileCount;
                dirs += child.TotalDirectoryCount + 1;
                if (child.LastWriteUtc > node.LastWriteUtc)
                    node.LastWriteUtc = child.LastWriteUtc;
            }

            node.TotalSize = size;
            node.TotalFileCount = files;
            node.TotalDirectoryCount = dirs;
        }
    }
}
