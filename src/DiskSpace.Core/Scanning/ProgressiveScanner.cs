using System.Collections.Concurrent;
using System.Diagnostics;

namespace DiskSpace.Core.Scanning;

/// <summary>Which queue a directory is waiting in, and how urgently it will be listed.</summary>
internal enum ScanBand
{
    /// <summary>The first few levels. Drained first, so something real is on screen at once.</summary>
    Shallow,

    /// <summary>A subtree the user has expanded, so it is the part being looked at.</summary>
    Hot,

    /// <summary>Everything else.</summary>
    Bulk,
}

/// <summary>
/// A directory sizer that publishes its tree immediately and keeps filling it in.
///
/// The blocking <see cref="FastDirectoryScanner"/> cannot show anything until the whole walk is
/// done, because its totals only exist after the final roll-up. Scanning a drive that way is
/// minutes of an empty window. Here every directory credits its bytes to itself and to each of
/// its ancestors as soon as it is listed, so the tree is renderable from the first level onward
/// and the numbers climb toward the truth.
///
/// Enumeration itself is shared with the blocking scanner through <see cref="DirectoryReader"/>.
/// </summary>
public sealed class ProgressiveScanner(ScanOptions? options = null) : IAsyncDisposable
{
    /// <summary>
    /// How many nodes one <see cref="Prioritize"/> call will walk. A cached subtree can be
    /// enormous, and this runs on the UI thread; the band the walk assigns is inherited by
    /// everything discovered later anyway, so a partial pass loses nothing.
    /// </summary>
    private const int PrioritizeVisitCap = 50_000;

    private readonly ScanOptions _options = options ?? new ScanOptions();

    private readonly ConcurrentQueue<(DirectoryNode Node, int Depth)> _shallow = new();
    private readonly ConcurrentQueue<(DirectoryNode Node, int Depth)> _hot = new();
    private readonly ConcurrentQueue<(DirectoryNode Node, int Depth)> _bulk = new();

    private readonly ConcurrentBag<ScanIssue> _issues = [];
    private readonly TaskCompletionSource _shallowReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _completed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Stopwatch _stopwatch = new();

    private CancellationTokenSource? _cancellation;
    private Task? _workers;

    private long _outstandingWork;
    private long _shallowOutstanding;
    private long _directoriesScanned;
    private long _filesSeen;
    private long _bytesSeen;
    private int _finished;
    private string _currentPath = string.Empty;

    /// <summary>The tree being built. Live: totals rise while the scan runs.</summary>
    public DirectoryNode? Root { get; private set; }

    public bool IsRunning => Root is not null && Volatile.Read(ref _finished) == 0;

    /// <summary>Locations the scan could not read, kept rather than thrown.</summary>
    public IReadOnlyList<ScanIssue> Issues => [.. _issues];

    /// <summary>
    /// A lock-free snapshot for the status bar. Pulled on the UI's own clock rather than pushed
    /// through <see cref="IProgress{T}"/>: a million directories reporting through a
    /// <c>Progress</c> callback floods the message queue faster than it can drain.
    /// </summary>
    public ScanProgress Snapshot() => new(
        Interlocked.Read(ref _directoriesScanned),
        Interlocked.Read(ref _filesSeen),
        Interlocked.Read(ref _bytesSeen),
        Volatile.Read(ref _currentPath));

    /// <summary>
    /// Starts the walk and returns once the first <see cref="ScanOptions.ShallowDepth"/> levels
    /// are listed, which takes about a tenth of a second even for a whole drive. The deep walk
    /// is already running when this returns.
    /// </summary>
    public async Task<DirectoryNode> StartAsync(
        string rootPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var fullPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Not a directory: {fullPath}");

        return await StartFromAsync(new DirectoryNode(fullPath, null), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the walk over a tree that already has values, which is how a cached tree is
    /// revalidated: the same reconciliation runs, and a fresh scan is simply the case where
    /// every directory turns out to be new.
    /// </summary>
    public async Task<DirectoryNode> StartFromAsync(
        DirectoryNode root, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);

        if (Root is not null)
            throw new InvalidOperationException("This scanner has already been started.");

        Root = root;
        root.MarkPending();

        _stopwatch.Start();
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;

        Enqueue(root, 0, ScanBand.Shallow);

        var workerCount = Math.Max(1, _options.MaxDegreeOfParallelism);
        var workers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            // Dedicated threads rather than pool work items: every one of these blocks on
            // directory I/O for its whole life, which is exactly what the pool must not be
            // asked to absorb.
            //
            // Started with no token of their own, deliberately. Handing the cancellation token
            // to StartNew means an already-cancelled one leaves the workers never scheduled, so
            // the queue is never drained, the outstanding count never reaches zero, and both
            // StartAsync and RunToCompletionAsync wait forever. The workers observe the token
            // themselves and exit through the same path as a normal finish.
            workers[i] = Task.Factory.StartNew(
                () => RunWorker(token),
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
        }

        _workers = Task.WhenAll(workers);

        await _shallowReady.Task.ConfigureAwait(false);
        return root;
    }

    /// <summary>
    /// Waits for the whole tree to be measured. A cancelled scan returns what it managed rather
    /// than throwing: the partial tree is already on screen and is honestly marked incomplete,
    /// which beats replacing it with an error message.
    /// </summary>
    public async Task<ScanResult> RunToCompletionAsync()
    {
        if (Root is null)
            throw new InvalidOperationException("This scanner has not been started.");

        await _completed.Task.ConfigureAwait(false);

        if (_workers is not null)
        {
            try
            {
                await _workers.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when the caller cancelled; the tree stands as measured so far.
            }
        }

        _stopwatch.Stop();

        return new ScanResult
        {
            Root = Root,
            Issues = [.. _issues],
            Duration = _stopwatch.Elapsed,
            IsComplete = Root.IsComplete,
        };
    }

    /// <summary>
    /// Moves the unmeasured part of one subtree to the front of the queue, so expanding a folder
    /// makes that folder finish first. Cheap enough to call from the UI thread.
    /// </summary>
    public void Prioritize(DirectoryNode node)
    {
        ArgumentNullException.ThrowIfNull(node);

        if (!IsRunning || node.IsComplete)
            return;

        var depth = 0;
        for (var walk = node; walk.Parent is not null; walk = walk.Parent)
            depth++;

        var pending = new Queue<(DirectoryNode Node, int Depth)>();
        pending.Enqueue((node, depth));
        var visited = 0;

        while (pending.Count > 0 && visited < PrioritizeVisitCap)
        {
            var (current, currentDepth) = pending.Dequeue();
            visited++;

            // Nothing outstanding below a complete node, so the walk stops there. That prune is
            // what keeps this proportional to the unmeasured part rather than the whole subtree.
            if (current.IsComplete)
                continue;

            if (!current.IsEnumerated)
            {
                // Waiting in some other band. Queueing a second copy is safe: whichever worker
                // claims it first wins, and the loser drops its copy.
                Enqueue(current, currentDepth, ScanBand.Hot);
                continue;
            }

            foreach (var child in current.Children)
                pending.Enqueue((child, currentDepth + 1));
        }
    }

    /// <summary>Stops the walk. The tree keeps whatever it has measured.</summary>
    public void Cancel() => _cancellation?.Cancel();

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();

        if (_workers is not null)
        {
            try
            {
                await _workers.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A worker that died on the way out must not stop the page from closing.
            }
        }

        _cancellation?.Dispose();
        _cancellation = null;
    }

    private ConcurrentQueue<(DirectoryNode Node, int Depth)> QueueFor(ScanBand band) => band switch
    {
        ScanBand.Shallow => _shallow,
        ScanBand.Hot => _hot,
        _ => _bulk,
    };

    private void Enqueue(DirectoryNode node, int depth, ScanBand band)
    {
        // A shallow child past the shallow depth is just ordinary work. Everything else keeps
        // its parent's band, which is what makes a prioritized subtree stay prioritized as it
        // is discovered, with no per-node bookkeeping.
        var target = band == ScanBand.Shallow && depth > _options.ShallowDepth
            ? ScanBand.Bulk
            : band;

        // Both counters go up before the item is visible, so neither can transiently read zero
        // and declare the scan finished while work is still being handed out.
        Interlocked.Increment(ref _outstandingWork);
        if (target == ScanBand.Shallow)
            Interlocked.Increment(ref _shallowOutstanding);

        QueueFor(target).Enqueue((node, depth));
    }

    /// <summary>Bands are tried in order, which is the whole of the scheduling policy.</summary>
    private bool TryDequeue(out DirectoryNode node, out int depth, out ScanBand band)
    {
        if (_shallow.TryDequeue(out var item))
            band = ScanBand.Shallow;
        else if (_hot.TryDequeue(out item))
            band = ScanBand.Hot;
        else if (_bulk.TryDequeue(out item))
            band = ScanBand.Bulk;
        else
        {
            (node, depth, band) = (null!, 0, ScanBand.Bulk);
            return false;
        }

        (node, depth) = (item.Node, item.Depth);
        return true;
    }

    private void RunWorker(CancellationToken token)
    {
        var idle = new SpinWait();

        while (Volatile.Read(ref _finished) == 0)
        {
            if (TryDequeue(out var node, out var depth, out var band))
            {
                idle = new SpinWait();

                try
                {
                    Process(node, depth, band, token);
                }
                catch (Exception)
                {
                    // One directory must never take a worker down with it; the node simply
                    // stays incomplete and the scan continues around it.
                }
                finally
                {
                    ReleaseWork(band);
                }

                continue;
            }

            if (token.IsCancellationRequested)
                return;

            // The queues are only briefly empty in the middle of a scan, so a short spin
            // followed by a sleep costs nothing measurable and keeps termination simple.
            if (idle.NextSpinWillYield)
                Thread.Sleep(1);
            else
                idle.SpinOnce();
        }
    }

    private void ReleaseWork(ScanBand band)
    {
        if (band == ScanBand.Shallow && Interlocked.Decrement(ref _shallowOutstanding) == 0)
            _shallowReady.TrySetResult();

        if (Interlocked.Decrement(ref _outstandingWork) == 0)
        {
            Volatile.Write(ref _finished, 1);
            _shallowReady.TrySetResult();
            _completed.TrySetResult();
        }
    }

    private void Process(DirectoryNode node, int depth, ScanBand band, CancellationToken token)
    {
        // Leaving the node unclaimed and unsettled is the point: a cancelled scan reports an
        // incomplete tree rather than a complete one that is missing half the disk.
        if (token.IsCancellationRequested)
            return;

        if (!node.TryClaim())
            return;

        DirectoryReading reading;
        try
        {
            reading = DirectoryReader.Read(node, _options, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        Volatile.Write(ref _currentPath, reading.Path);

        if (reading.Vanished)
        {
            Vanish(node);
            return;
        }

        var deltaBytes = reading.OwnSize - node.OwnSize;
        long deltaFiles = reading.OwnFileCount - node.OwnFileCount;
        var deltaDirectories = 0;

        node.SetOwn(reading.OwnSize, reading.OwnFileCount);
        node.Error = reading.Error;

        if (reading.IssueReason is { } reason)
            _issues.Add(new ScanIssue(reading.Path, reason));

        var children = Reconcile(node, reading.Children, _options.TrustUnchangedFolders,
            ref deltaBytes, ref deltaFiles, ref deltaDirectories);

        node.SetChildren(children);
        node.SetFlag(NodeFlags.Enumerated);
        node.ClearFlag(NodeFlags.FromCache);

        Credit(node, deltaBytes, deltaFiles, deltaDirectories, reading.NewestEntryUtc);

        foreach (var child in children)
        {
            // A junction contributes no bytes of its own; following it would double-count at
            // best and loop forever at worst. It is settled here rather than queued, or its
            // parent would wait forever for a listing that never happens.
            if (!DirectoryReader.ShouldDescend(child, _options))
            {
                child.SetFlag(NodeFlags.Enumerated);
                child.MarkComplete();
                continue;
            }

            // Already settled: either adopted from the cache on trust, or a junction. Everything
            // that still needs listing was reopened by the reconciliation above.
            if (child.IsComplete)
                continue;

            node.RegisterPendingChild();
            Enqueue(child, depth + 1, band);
        }

        Interlocked.Increment(ref _directoriesScanned);
        Interlocked.Add(ref _filesSeen, reading.OwnFileCount);
        Interlocked.Add(ref _bytesSeen, reading.OwnSize);

        // Strictly after the children are queued and counted, so this node can never be seen
        // complete while work below it is still outstanding.
        Settle(node);
    }

    /// <summary>
    /// Matches what is on disk against what the node already holds. On a fresh scan the node has
    /// nothing, so this is the "everything is new" case and costs one array assignment.
    /// </summary>
    private static DirectoryNode[] Reconcile(
        DirectoryNode node,
        DirectoryNode[] fresh,
        bool trustUnchanged,
        ref long deltaBytes,
        ref long deltaFiles,
        ref int deltaDirectories)
    {
        var existing = node.Children;

        if (existing.Count == 0)
        {
            deltaDirectories += fresh.Length;
            return fresh;
        }

        var byName = new Dictionary<string, DirectoryNode>(
            existing.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var child in existing)
            byName[child.Name] = child;

        var merged = new List<DirectoryNode>(fresh.Length);

        foreach (var candidate in fresh)
        {
            if (byName.Remove(candidate.Name, out var kept))
            {
                // The one place the folder timestamp is allowed to decide anything. Compared
                // before it is overwritten, and only consulted when the user has opted in.
                var unchanged = kept.IsFromCache
                                && kept.OwnLastWriteUtc == candidate.OwnLastWriteUtc;

                // Reusing the node object is what keeps the tree view's row tags valid, so
                // revalidating a cached tree updates rows in place instead of rebuilding them.
                kept.IsReparsePoint = candidate.IsReparsePoint;
                kept.OwnLastWriteUtc = candidate.OwnLastWriteUtc;

                // Adopted subtrees keep their cache marking for the rest of the scan, so an
                // estimate is never drawn as though it had been measured.
                if (!trustUnchanged || !unchanged)
                {
                    kept.ClearFlag(NodeFlags.FromCache);
                    kept.MarkPending();
                }

                merged.Add(kept);
                continue;
            }

            deltaDirectories++;
            merged.Add(candidate);
        }

        foreach (var gone in byName.Values)
        {
            deltaBytes -= gone.TotalSize;
            deltaFiles -= gone.TotalFileCount;
            deltaDirectories -= gone.TotalDirectoryCount + 1;
        }

        return [.. merged];
    }

    /// <summary>Credits a directory's own contents to itself and to every ancestor.</summary>
    private static void Credit(
        DirectoryNode node, long bytes, long files, int directories, DateTime newestEntryUtc)
    {
        var raising = newestEntryUtc > DateTime.MinValue;

        for (var current = node; current is not null; current = current.Parent)
        {
            current.AddTotals(bytes, files, directories);

            // The chain is monotone upward, so the first ancestor that already holds a newer
            // value ends the climb.
            if (raising)
                raising = current.RaiseLastWrite(newestEntryUtc);
        }
    }

    /// <summary>Takes back everything a directory was credited with, after it disappeared.</summary>
    private void Vanish(DirectoryNode node)
    {
        Credit(node, -node.TotalSize, -node.TotalFileCount, -node.TotalDirectoryCount,
            DateTime.MinValue);

        node.SetOwn(0, 0);
        node.SetChildren([]);
        node.SetFlag(NodeFlags.Enumerated);
        node.ClearFlag(NodeFlags.FromCache);
        Settle(node);
    }

    private static void Settle(DirectoryNode node)
    {
        for (var current = node; current is not null; current = current.Parent)
        {
            if (!current.ReleaseOne())
                return;
        }
    }
}
