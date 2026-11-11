namespace DiskSpace.Core.Safety;

/// <summary>
/// Recursive delete that understands reparse points.
///
/// <see cref="Directory.Delete(string, bool)"/> is not usable here: on a tree containing a
/// junction it either fails outright or, depending on the link type and platform version,
/// recurses through the link into data that lives somewhere else entirely. Deleting a junction
/// must remove the link and never its target — a junction inside a cache folder is a pointer
/// to real data, not a copy of it.
///
/// Iterative rather than recursive, because the trees this tool deletes are exactly the deep
/// ones (node_modules, package caches) that overflow a call stack.
/// </summary>
public static class SafeDelete
{
    /// <summary>
    /// Deletes a directory tree. Returns the bytes reclaimed from files actually removed.
    /// Individual failures are skipped so one locked file cannot abandon the rest.
    /// </summary>
    public static long DeleteDirectory(string path, bool removeRoot, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists)
            return 0;

        // A junction handed in directly: remove the link, nothing else.
        if (IsReparsePoint(root))
        {
            if (removeRoot)
                TryDeleteLink(root);
            return 0;
        }

        long reclaimed = 0;

        // Collect first, deepest last, so directories can be removed bottom-up afterwards.
        var directories = new List<DirectoryInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();
            directories.Add(current);

            IEnumerable<DirectoryInfo> children;
            try
            {
                children = current.EnumerateDirectories();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                if (IsReparsePoint(child))
                {
                    // The link itself goes; whatever it points at is left alone.
                    TryDeleteLink(child);
                    continue;
                }

                pending.Push(child);
            }
        }

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                foreach (var file in directory.EnumerateFiles())
                    reclaimed += TryDeleteFile(file);
            }
            catch (Exception)
            {
                // Unreadable directory; its files stay.
            }
        }

        // Bottom-up: the deepest directories were discovered last.
        for (var i = directories.Count - 1; i >= 0; i--)
        {
            var directory = directories[i];
            if (!removeRoot && ReferenceEquals(directory, root))
                continue;

            try
            {
                directory.Refresh();
                if (directory.Exists && !directory.EnumerateFileSystemInfos().Any())
                    directory.Delete();
            }
            catch (Exception)
            {
                // Something arrived in it, or it is held open. Harmless.
            }
        }

        return reclaimed;
    }

    /// <summary>
    /// The files under <paramref name="path"/> that nothing has created or written since
    /// <paramref name="cutoffUtc"/>, judged one file at a time. Junctions are not followed.
    ///
    /// Measuring and deleting both go through this, so what a scan offers and what a cleanup
    /// removes are chosen by the same test.
    /// </summary>
    public static IEnumerable<FileInfo> EnumerateOlderThan(
        string path, DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists || IsReparsePoint(root))
            yield break;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            foreach (var file in Listing(current.EnumerateFiles))
            {
                if (IsOlderThan(file, cutoffUtc))
                    yield return file;
            }

            foreach (var child in Listing(current.EnumerateDirectories))
            {
                if (!IsReparsePoint(child))
                    pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Deletes the files <see cref="EnumerateOlderThan"/> names, then the directories that
    /// emptied out and are themselves that old. The root always stays, and so does a young
    /// empty directory: it may be the working folder something created a moment ago.
    /// </summary>
    public static long DeleteOlderThan(
        string path, DateTime cutoffUtc, CancellationToken cancellationToken = default)
    {
        var root = new DirectoryInfo(path);
        if (!root.Exists || IsReparsePoint(root))
            return 0;

        long reclaimed = 0;

        foreach (var file in EnumerateOlderThan(path, cutoffUtc, cancellationToken))
            reclaimed += TryDeleteFile(file);

        // Collected top-down, removed bottom-up.
        var directories = new List<DirectoryInfo>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Pop();

            foreach (var child in Listing(current.EnumerateDirectories))
            {
                if (IsReparsePoint(child))
                {
                    // An old link goes; whatever it points at is left alone.
                    if (IsOlderThan(child, cutoffUtc))
                        TryDeleteLink(child);

                    continue;
                }

                directories.Add(child);
                pending.Push(child);
            }
        }

        for (var i = directories.Count - 1; i >= 0; i--)
        {
            var directory = directories[i];

            try
            {
                if (IsOlderThan(directory, cutoffUtc) && !directory.EnumerateFileSystemInfos().Any())
                    directory.Delete();
            }
            catch (Exception)
            {
                // Something arrived in it, or it is held open. Harmless.
            }
        }

        return reclaimed;
    }

    /// <summary>
    /// Creation counts as well as the last write: a file copied into place today keeps the
    /// write time of its source, and it is not old.
    /// </summary>
    private static bool IsOlderThan(FileSystemInfo entry, DateTime cutoffUtc)
    {
        try
        {
            return entry.LastWriteTimeUtc < cutoffUtc && entry.CreationTimeUtc < cutoffUtc;
        }
        catch (Exception)
        {
            return false; // Cannot tell: treat as recent and leave it.
        }
    }

    /// <summary>A directory listing that ends early, instead of throwing, where it cannot be read.</summary>
    private static IEnumerable<T> Listing<T>(Func<IEnumerable<T>> list)
    {
        IEnumerator<T> entries;

        try
        {
            entries = list().GetEnumerator();
        }
        catch (Exception)
        {
            yield break;
        }

        using (entries)
        {
            while (true)
            {
                try
                {
                    if (!entries.MoveNext())
                        yield break;
                }
                catch (Exception)
                {
                    yield break;
                }

                yield return entries.Current;
            }
        }
    }

    public static long DeleteFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? TryDeleteFile(info) : 0;
    }

    private static long TryDeleteFile(FileInfo file)
    {
        try
        {
            var size = file.Length;

            // Read-only is an attribute rather than a permission, and caches are full of them.
            if (file.IsReadOnly)
                file.IsReadOnly = false;

            file.Delete();
            return size;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void TryDeleteLink(DirectoryInfo link)
    {
        try
        {
            // Non-recursive: removes the reparse point, leaving its target intact.
            link.Delete();
        }
        catch (Exception)
        {
            // Left in place.
        }
    }

    private static bool IsReparsePoint(DirectoryInfo directory)
    {
        try
        {
            return directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            return true; // Cannot tell: treat as a link and refuse to recurse into it.
        }
    }
}
