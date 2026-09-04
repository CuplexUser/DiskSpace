using System.Collections.Concurrent;
using System.Diagnostics;

namespace DiskSpace.Core.Scanning;

/// <summary>
/// Recursive directory sizer that measures a whole tree and returns once, which is what the rule
/// catalog wants: it needs one number per target before it can decide anything.
///
/// Recursion is driven level by level rather than by <c>RecurseSubdirectories</c>, which keeps
/// parallelism, cancellation and reparse-point skipping under our own control. Totals are filled
/// in by a single post-order <see cref="RollUp"/> at the end.
///
/// The Explorer page uses <see cref="ProgressiveScanner"/> instead, which accumulates totals as
/// it goes so a tree can be shown while it is still being measured. Both share
/// <see cref="DirectoryReader"/>, so there is still exactly one set of rules about junctions,
/// hidden files and access denials. Keeping the two aggregation strategies separate is
/// deliberate: it makes the "both scanners agree" test a real comparison rather than a
/// tautology, and that test guards the numbers a deletion is planned from.
/// </summary>
public sealed class FastDirectoryScanner(ScanOptions? options = null)
{
    private readonly ScanOptions _options = options ?? new ScanOptions();

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

            await Parallel.ForEachAsync(currentLevel, parallelOptions, (node, token) =>
            {
                ScanOneDirectory(node, nextLevel, issues, counters, progress, token);
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
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var reading = DirectoryReader.Read(node, _options, cancellationToken);

        node.SetChildren(reading.Children);
        node.SetFlag(NodeFlags.Enumerated);

        if (!reading.Vanished)
        {
            node.SetOwn(reading.OwnSize, reading.OwnFileCount);
            node.RaiseLastWrite(reading.NewestEntryUtc);
            node.Error = reading.Error;

            if (reading.IssueReason is { } reason)
                issues.Add(new ScanIssue(reading.Path, reason));

            foreach (var child in reading.Children)
            {
                // A junction contributes no bytes of its own; following it would double-count
                // at best and loop forever at worst.
                if (DirectoryReader.ShouldDescend(child, _options))
                    nextLevel.Add(child);
                else
                    child.SetFlag(NodeFlags.Enumerated);
            }
        }

        var scanned = Interlocked.Increment(ref counters.Directories);
        Interlocked.Add(ref counters.Files, reading.OwnFileCount);
        Interlocked.Add(ref counters.Bytes, reading.OwnSize);

        if (progress is not null && scanned % _options.ProgressInterval == 0)
        {
            progress.Report(new ScanProgress(
                scanned,
                Interlocked.Read(ref counters.Files),
                Interlocked.Read(ref counters.Bytes),
                reading.Path));
        }
    }

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
                node.RaiseLastWrite(child.LastWriteUtc);
            }

            node.SetTotals(size, files, dirs);

            // A rolled-up tree is fully known, so every node in it reports as listed and
            // settled. Without this the tree view would draw an expander on a leaf.
            node.SetFlag(NodeFlags.Enumerated);
            node.MarkComplete();
        }
    }
}
