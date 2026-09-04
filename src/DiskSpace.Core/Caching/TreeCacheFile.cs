using System.Text;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Caching;

/// <summary>What a cache file says about the scan it holds, without reading the tree.</summary>
public readonly record struct CacheHeader(
    string RootPath,
    DateTimeOffset WrittenAt,
    int NodeCount,
    bool TreeWasComplete,
    TimeSpan ScanDuration,
    IReadOnlyList<ScanIssue> Issues);

/// <summary>
/// Reads and writes a scanned tree as a compact binary file.
///
/// This is the one place the project departs from its own house style of source-generated JSON,
/// and the reason is not size but depth: <c>JsonSerializerOptions.MaxDepth</c> is 64 and JSON
/// deserialization of a nested object graph recurses, while the node_modules and package-cache
/// trees this tool exists to find go far deeper than that. Working around it means a flat array
/// with parent indices, which abandons the JSON object shape anyway and keeps only its threefold
/// byte cost. A scan cache is also disposable and machine-only, unlike the audit log and the
/// quarantine manifest, which are the user's record of a destructive act and must stay readable
/// by hand. <c>BinaryReader</c> and <c>BinaryWriter</c> use no reflection, so the original
/// motive for source generation, surviving a trimmed publish, is satisfied regardless.
///
/// Both directions are iterative over an explicit stack, for the same reason
/// <see cref="FastDirectoryScanner.RollUp"/> is: recursion overflows the stack on exactly the
/// trees that make a cache worth having.
/// </summary>
internal static class TreeCacheFile
{
    internal const int FormatVersion = 1;

    private const int Magic = 0x31545344; // "DST1"

    private const byte FlagReparsePoint = 1;
    private const byte FlagHadError = 2;
    private const byte FlagSubtreeComplete = 4;

    /// <summary>Message left on a directory that could not be read when it was measured.</summary>
    private const string CachedError = "Unreadable when last measured";

    /// <summary>Guards against a corrupt child count turning into a huge allocation.</summary>
    private const int ChildCapacityCap = 1024;

    private sealed class Frame(DirectoryNode node, int remaining)
    {
        public DirectoryNode Node { get; } = node;

        public int Remaining { get; set; } = remaining;

        public List<DirectoryNode> Children { get; } = new(Math.Min(remaining, ChildCapacityCap));
    }

    /// <summary>
    /// Writes the tree to <paramref name="path"/> by way of a temporary file, so a crash partway
    /// through cannot leave something readable behind, and cannot cost the previous cache either.
    /// Returns the node count written, or null when the tree was too large to cache.
    /// </summary>
    internal static int? Write(string path, DirectoryNode root, ScanResult result, int maxNodes)
    {
        var temporary = path + ".tmp";

        try
        {
            int written;

            using (var stream = new FileStream(
                       temporary, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024))
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(DateTimeOffset.UtcNow.UtcTicks);
                writer.Write(root.Path);
                writer.Write(result.Duration.Ticks);
                writer.Write(result.IsComplete);

                var countPosition = stream.Position;
                writer.Write(0); // Patched below, once the walk knows the real number.

                writer.Write(result.Issues.Count);
                foreach (var issue in result.Issues)
                {
                    writer.Write(issue.Path);
                    writer.Write(issue.Reason);
                }

                written = WriteNodes(writer, root, maxNodes);
                if (written < 0)
                    return null;

                writer.Flush();
                stream.Position = countPosition;
                writer.Write(written);
            }

            File.Move(temporary, path, overwrite: true);
            return written;
        }
        catch (Exception)
        {
            Discard(temporary);
            return null;
        }
    }

    /// <summary>Returns the count written, or -1 once the cap is passed.</summary>
    private static int WriteNodes(BinaryWriter writer, DirectoryNode root, int maxNodes)
    {
        var stack = new Stack<DirectoryNode>();
        stack.Push(root);
        var written = 0;

        while (stack.Count > 0)
        {
            var node = stack.Pop();

            if (++written > maxNodes)
                return -1;

            byte flags = 0;
            if (node.IsReparsePoint)
                flags |= FlagReparsePoint;
            if (node.Error is not null)
                flags |= FlagHadError;
            if (node.IsComplete)
                flags |= FlagSubtreeComplete;

            // Names only, never full paths: the reader rebuilds a path from the chain it is
            // already walking, and this is where most of the saving over JSON comes from.
            writer.Write(node.Name);
            writer.Write7BitEncodedInt64(node.OwnSize);
            writer.Write7BitEncodedInt(node.OwnFileCount);
            writer.Write7BitEncodedInt64(node.TotalSize);
            writer.Write7BitEncodedInt64(node.TotalFileCount);
            writer.Write7BitEncodedInt(node.TotalDirectoryCount);

            // Fixed eight bytes rather than 7-bit encoded: tick values are large and
            // high-entropy, so the compact form would cost nine.
            writer.Write(node.OwnLastWriteUtc.Ticks);
            writer.Write(node.LastWriteUtc.Ticks);
            writer.Write(flags);

            var children = node.Children;
            writer.Write7BitEncodedInt(children.Count);

            // Pushed in reverse so the file comes out in the same order the reader expects.
            for (var i = children.Count - 1; i >= 0; i--)
                stack.Push(children[i]);
        }

        return written;
    }

    /// <summary>
    /// Null on a wrong magic, a wrong version, a truncated file, or anything else at all. A
    /// cache is disposable, so there is never a reason to fight for one.
    /// </summary>
    internal static DirectoryNode? TryRead(string path, int maxNodes, out CacheHeader header)
    {
        header = default;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);

            // Streamed rather than read into memory: a hundred-megabyte cache should not put a
            // hundred-megabyte array on the large object heap just to be parsed.
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

            if (reader.ReadInt32() != Magic || reader.ReadInt32() != FormatVersion)
                return null;

            var writtenAt = new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero);
            var rootPath = reader.ReadString();
            var duration = TimeSpan.FromTicks(reader.ReadInt64());
            var complete = reader.ReadBoolean();
            var nodeCount = reader.ReadInt32();

            if (nodeCount <= 0 || nodeCount > maxNodes)
                return null;

            var issueCount = reader.ReadInt32();
            if (issueCount < 0 || issueCount > nodeCount)
                return null;

            var issues = new ScanIssue[issueCount];
            for (var i = 0; i < issueCount; i++)
                issues[i] = new ScanIssue(reader.ReadString(), reader.ReadString());

            var root = ReadTree(reader, rootPath, nodeCount);
            if (root is null)
                return null;

            header = new CacheHeader(rootPath, writtenAt, nodeCount, complete, duration, issues);
            return root;
        }
        catch (Exception)
        {
            // Truncated by a crash mid-write, or written by a build that is no longer this one.
            return null;
        }
    }

    private static DirectoryNode? ReadTree(BinaryReader reader, string rootPath, int nodeCount)
    {
        var read = 0;
        var root = ReadNode(reader, null, rootPath, out var rootChildren);
        read++;

        var stack = new Stack<Frame>();

        if (rootChildren > 0)
            stack.Push(new Frame(root, rootChildren));
        else
            root.SetChildren([]);

        while (stack.Count > 0)
        {
            var frame = stack.Peek();

            if (frame.Remaining == 0)
            {
                stack.Pop();
                frame.Node.SetChildren([.. frame.Children]);
                continue;
            }

            frame.Remaining--;

            if (++read > nodeCount)
                return null;

            var child = ReadNode(reader, frame.Node, null, out var childCount);
            frame.Children.Add(child);

            if (childCount > 0)
                stack.Push(new Frame(child, childCount));
            else
                child.SetChildren([]);
        }

        return read == nodeCount ? root : null;
    }

    private static DirectoryNode ReadNode(
        BinaryReader reader, DirectoryNode? parent, string? rootPath, out int childCount)
    {
        var name = reader.ReadString();
        var ownSize = reader.Read7BitEncodedInt64();
        var ownFiles = reader.Read7BitEncodedInt();
        var totalSize = reader.Read7BitEncodedInt64();
        var totalFiles = reader.Read7BitEncodedInt64();
        var totalDirectories = reader.Read7BitEncodedInt();
        var ownLastWrite = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        var lastWrite = new DateTime(reader.ReadInt64(), DateTimeKind.Utc);
        var flags = reader.ReadByte();
        childCount = reader.Read7BitEncodedInt();

        var node = parent is null
            ? new DirectoryNode(rootPath!, null)
            : new DirectoryNode(parent, name);

        node.SetOwn(ownSize, ownFiles);
        node.SetTotals(totalSize, totalFiles, totalDirectories);
        node.SetLastWrite(lastWrite);
        node.OwnLastWriteUtc = ownLastWrite;
        node.IsReparsePoint = (flags & FlagReparsePoint) != 0;

        // The message itself is not stored, because by now it would be a guess about the past.
        // The bit is, so a folder that could not be read still says so until it is re-measured.
        if ((flags & FlagHadError) != 0)
            node.Error = CachedError;

        node.SetFlag(NodeFlags.FromCache);
        node.SetFlag(NodeFlags.Enumerated);

        if ((flags & FlagSubtreeComplete) != 0)
            node.MarkComplete();

        return node;
    }

    private static void Discard(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Leaving a stray temporary behind is a housekeeping problem, not a failure.
        }
    }
}
