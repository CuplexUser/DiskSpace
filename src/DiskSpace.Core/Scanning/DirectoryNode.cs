namespace DiskSpace.Core.Scanning;

/// <summary>
/// One directory in a scanned tree. <see cref="OwnSize"/> is filled in by the worker that
/// enumerated this directory; <see cref="TotalSize"/> is filled in afterwards by a single
/// post-order roll-up pass, so nothing needs synchronising during the scan itself.
/// </summary>
public sealed class DirectoryNode
{
    public DirectoryNode(string path, DirectoryNode? parent)
    {
        Path = path;
        Parent = parent;
        Name = System.IO.Path.GetFileName(path.AsSpan()).IsEmpty
            ? path
            : System.IO.Path.GetFileName(path);
    }

    public string Path { get; }
    public string Name { get; }
    public DirectoryNode? Parent { get; }
    public List<DirectoryNode> Children { get; } = [];

    /// <summary>Bytes in files directly inside this directory.</summary>
    public long OwnSize { get; internal set; }

    /// <summary>Files directly inside this directory.</summary>
    public int OwnFileCount { get; internal set; }

    /// <summary>Bytes in this directory and everything below it.</summary>
    public long TotalSize { get; internal set; }

    /// <summary>Files in this directory and everything below it.</summary>
    public long TotalFileCount { get; internal set; }

    /// <summary>Directories below this one, excluding itself.</summary>
    public int TotalDirectoryCount { get; internal set; }

    public DateTime LastWriteUtc { get; internal set; }

    /// <summary>True when this directory is a junction or symlink that the scan did not follow.</summary>
    public bool IsReparsePoint { get; internal set; }

    /// <summary>Set when this directory could not be read; its sizes are then incomplete.</summary>
    public string? Error { get; internal set; }

    /// <summary>Children ordered largest first — the order the Explorer page wants.</summary>
    public IEnumerable<DirectoryNode> ChildrenBySize =>
        Children.OrderByDescending(c => c.TotalSize);

    public override string ToString() => $"{Path} ({TotalSize:N0} bytes)";
}
