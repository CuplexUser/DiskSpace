using System.Text;

namespace DiskSpace.Core.Scanning;

/// <summary>
/// One directory in a scanned tree.
///
/// Every total on this type is written by scanner threads while the UI thread paints the same
/// node, because the Explorer page now renders a tree that is still being measured. So the
/// counters are interlocked, the child list is published as a finished array rather than
/// appended to, and completion is tracked per node so a rising number can be told apart from a
/// settled one.
/// </summary>
public sealed class DirectoryNode
{
    private static readonly DirectoryNode[] NoChildren = [];

    /// <summary>Non-null only on a root, where it terminates the walk that composes a path.</summary>
    private readonly string? _rootPath;

    private DirectoryNode[] _children = NoChildren;
    private long _ownSize;
    private int _ownFileCount;
    private long _totalSize;
    private long _totalFileCount;
    private int _totalDirectoryCount;
    private long _lastWriteTicks;
    private int _childrenVersion;
    private int _flags;

    /// <summary>
    /// Units of work outstanding in this subtree: one for this directory's own listing, plus one
    /// for every child whose subtree is unfinished. Zero means the totals below are final.
    /// </summary>
    private int _outstanding = 1;

    /// <summary>
    /// Set by the first worker to take this node off a queue. Prioritizing an expanded subtree
    /// re-queues nodes that may already be waiting, and listing one directory twice would
    /// double-count every byte in it.
    /// </summary>
    private int _claimed;

    /// <summary>
    /// Creates a root when <paramref name="parent"/> is null, and otherwise a child named by the
    /// last segment of <paramref name="path"/>.
    /// </summary>
    public DirectoryNode(string path, DirectoryNode? parent)
    {
        ArgumentNullException.ThrowIfNull(path);
        Parent = parent;

        if (parent is null)
        {
            _rootPath = path;
            var leaf = System.IO.Path.GetFileName(path.AsSpan());
            Name = leaf.IsEmpty ? path : leaf.ToString();
        }
        else
        {
            Name = System.IO.Path.GetFileName(path);
        }
    }

    /// <summary>
    /// The hot path: the scanner already holds the segment name off the enumerator, so there is
    /// no reason to build a full path only to take the last piece of it back off again.
    /// </summary>
    internal DirectoryNode(DirectoryNode parent, string name)
    {
        Parent = parent;
        Name = name;
    }

    /// <summary>
    /// Composed on demand rather than stored. A full path string per node costs roughly 170
    /// bytes, which is over 150 MB when scanning a million directories, and the path is only
    /// wanted when a directory is listed or shown. Walking to the root is a handful of appends.
    /// </summary>
    public string Path
    {
        get
        {
            if (_rootPath is not null)
                return _rootPath;

            var depth = 0;
            for (var node = this; node._rootPath is null; node = node.Parent!)
                depth++;

            var segments = new string[depth];
            var current = this;
            for (var i = depth - 1; i >= 0; i--, current = current.Parent!)
                segments[i] = current.Name;

            var builder = new StringBuilder(current._rootPath);
            foreach (var segment in segments)
            {
                if (builder.Length > 0 && builder[^1] != System.IO.Path.DirectorySeparatorChar)
                    builder.Append(System.IO.Path.DirectorySeparatorChar);

                builder.Append(segment);
            }

            return builder.ToString();
        }
    }

    public string Name { get; }

    public DirectoryNode? Parent { get; }

    /// <summary>
    /// An immutable snapshot, replaced wholesale and never mutated in place, so a painting
    /// thread sees either no children or all of them and never a half-built list.
    /// </summary>
    public IReadOnlyList<DirectoryNode> Children => Volatile.Read(ref _children);

    /// <summary>
    /// Bumped whenever <see cref="Children"/> is replaced, so the tree view can tell "same rows,
    /// new numbers" from "the rows themselves changed".
    /// </summary>
    public int ChildrenVersion => Volatile.Read(ref _childrenVersion);

    /// <summary>Bytes in files directly inside this directory.</summary>
    public long OwnSize => Volatile.Read(ref _ownSize);

    /// <summary>Files directly inside this directory.</summary>
    public int OwnFileCount => Volatile.Read(ref _ownFileCount);

    /// <summary>Bytes in this directory and everything below it.</summary>
    public long TotalSize => Volatile.Read(ref _totalSize);

    /// <summary>Files in this directory and everything below it.</summary>
    public long TotalFileCount => Volatile.Read(ref _totalFileCount);

    /// <summary>Directories below this one, excluding itself.</summary>
    public int TotalDirectoryCount => Volatile.Read(ref _totalDirectoryCount);

    /// <summary>Newest write anywhere in this subtree. What the rule catalog ages against.</summary>
    public DateTime LastWriteUtc => new(Volatile.Read(ref _lastWriteTicks), DateTimeKind.Utc);

    /// <summary>
    /// This directory's own timestamp, as reported by the enumeration of its parent.
    ///
    /// Deliberately separate from <see cref="LastWriteUtc"/>, which is a subtree maximum: only
    /// this one answers "has anything been added to or removed from this folder", which is the
    /// single question the scan cache asks of it.
    /// </summary>
    public DateTime OwnLastWriteUtc { get; internal set; }

    /// <summary>True when this directory is a junction or symlink that the scan did not follow.</summary>
    public bool IsReparsePoint { get; internal set; }

    /// <summary>Set when this directory could not be read; its sizes are then incomplete.</summary>
    public string? Error { get; internal set; }

    /// <summary>This directory has been listed, so <see cref="Children"/> is final.</summary>
    public bool IsEnumerated => (Volatile.Read(ref _flags) & (int)NodeFlags.Enumerated) != 0;

    /// <summary>This directory and everything below it has been measured.</summary>
    public bool IsComplete => Volatile.Read(ref _outstanding) == 0;

    /// <summary>Totals came from the on-disk cache and have not been re-measured yet.</summary>
    public bool IsFromCache => (Volatile.Read(ref _flags) & (int)NodeFlags.FromCache) != 0;

    /// <summary>
    /// Children ordered largest first, the order the Explorer page wants.
    ///
    /// Safe to call while a scan is running: OrderByDescending reads every key before it sorts,
    /// so a size that moves mid-sort cannot make the comparer contradict itself.
    /// </summary>
    public IEnumerable<DirectoryNode> ChildrenBySize =>
        Children.OrderByDescending(c => c.TotalSize);

    public override string ToString() => $"{Path} ({TotalSize:N0} bytes)";

    internal void SetOwn(long size, int fileCount)
    {
        Volatile.Write(ref _ownSize, size);
        Volatile.Write(ref _ownFileCount, fileCount);
    }

    internal void SetChildren(DirectoryNode[] children)
    {
        Volatile.Write(ref _children, children.Length == 0 ? NoChildren : children);
        Interlocked.Increment(ref _childrenVersion);
    }

    /// <summary>Applies a signed delta to this node alone. Callers walk the parent chain.</summary>
    internal void AddTotals(long bytes, long files, int directories)
    {
        if (bytes != 0)
            Interlocked.Add(ref _totalSize, bytes);

        if (files != 0)
            Interlocked.Add(ref _totalFileCount, files);

        if (directories != 0)
            Interlocked.Add(ref _totalDirectoryCount, directories);
    }

    /// <summary>
    /// Raises the subtree maximum. Returns false once a node already holds a newer value, which
    /// lets a caller walking to the root stop early: the chain is monotone upward.
    /// </summary>
    internal bool RaiseLastWrite(DateTime utc)
    {
        var ticks = utc.Ticks;

        while (true)
        {
            var current = Volatile.Read(ref _lastWriteTicks);
            if (current >= ticks)
                return false;

            if (Interlocked.CompareExchange(ref _lastWriteTicks, ticks, current) == current)
                return true;
        }
    }

    internal void RegisterPendingChild() => Interlocked.Increment(ref _outstanding);

    /// <summary>Releases one unit of work. True when this subtree just became complete.</summary>
    internal bool ReleaseOne() => Interlocked.Decrement(ref _outstanding) == 0;

    /// <summary>True for the one caller that gets to list this directory.</summary>
    internal bool TryClaim() => Interlocked.Exchange(ref _claimed, 1) == 0;

    internal void SetFlag(NodeFlags flag) => UpdateFlags(flag, set: true);

    internal void ClearFlag(NodeFlags flag) => UpdateFlags(flag, set: false);

    /// <summary>Marks a subtree that is known to be measured. Used by the cache loader.</summary>
    internal void MarkComplete() => Volatile.Write(ref _outstanding, 0);

    /// <summary>Restores the "one unit of work for my own listing" state before a revalidation.</summary>
    internal void MarkPending()
    {
        Volatile.Write(ref _claimed, 0);
        Volatile.Write(ref _outstanding, 1);
    }

    internal void SetTotals(long size, long files, int directories)
    {
        Volatile.Write(ref _totalSize, size);
        Volatile.Write(ref _totalFileCount, files);
        Volatile.Write(ref _totalDirectoryCount, directories);
    }

    internal void SetLastWrite(DateTime utc) => Volatile.Write(ref _lastWriteTicks, utc.Ticks);

    private void UpdateFlags(NodeFlags flag, bool set)
    {
        while (true)
        {
            var current = Volatile.Read(ref _flags);
            var updated = set ? current | (int)flag : current & ~(int)flag;

            if (current == updated
                || Interlocked.CompareExchange(ref _flags, updated, current) == current)
            {
                return;
            }
        }
    }
}

[Flags]
internal enum NodeFlags
{
    None = 0,

    /// <summary>The directory has been listed.</summary>
    Enumerated = 1,

    /// <summary>The values came from the cache and have not been confirmed against disk.</summary>
    FromCache = 2,
}
