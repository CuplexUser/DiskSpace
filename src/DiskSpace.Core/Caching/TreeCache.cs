using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskSpace.Core.Safety;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Caching;

/// <summary>A tree loaded from the cache, with what is known about when it was measured.</summary>
public sealed record CachedTree(DirectoryNode Root, CacheHeader Header)
{
    public TimeSpan Age => DateTimeOffset.UtcNow - Header.WrittenAt;
}

/// <summary>Bounds on how much disk the cache is allowed to occupy.</summary>
public sealed record TreeCacheLimits
{
    public int MaxEntries { get; init; } = 12;

    public long MaxTotalBytes { get; init; } = 512L * 1024 * 1024;

    public int MaxNodesPerTree { get; init; } = 3_000_000;

    /// <summary>A tree this small rescans faster than the cache would load.</summary>
    public int MinNodesToCache { get; init; } = 200;

    /// <summary>Past this, a cached measurement is history rather than a starting point.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Remembers what a root measured last time, so scanning it again paints immediately instead of
/// showing an empty window for a minute.
///
/// What the cache does not do is let a scan skip work. A directory's timestamp moves when an
/// entry is added, removed or renamed in it, but not when a file already inside it is written
/// to, and not for anything that happens in a grandchild. So "the timestamp has not moved"
/// cannot mean "this subtree is unchanged", and a cache that assumed it would quietly report old
/// numbers as though they were measurements. Every value here is replaced by a real listing
/// during the revalidation pass that follows the first paint.
/// </summary>
public sealed class TreeCache
{
    private const int IndexVersion = 1;
    private const string IndexFileName = "index.json";
    private const string TreeExtension = ".dstree";

    private readonly string _directory;
    private readonly TreeCacheLimits _limits;
    private readonly Lock _gate = new();

    /// <param name="directory">
    /// Overrides <see cref="CacheDirectory"/>. Tests must pass one, for the same reason
    /// <see cref="Cleaning.AuditLog.StartRun"/> takes one: a test run must not leave trees in
    /// the cache belonging to whoever is running it.
    /// </param>
    public TreeCache(string? directory = null, TreeCacheLimits? limits = null)
    {
        _directory = directory ?? CacheDirectory;
        _limits = limits ?? new TreeCacheLimits();
    }

    /// <summary>Where cached trees live. Sibling of the run logs.</summary>
    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskSpace",
        "cache");

    /// <summary>
    /// The tree this root measured last time, or null when there is nothing usable. Reading is
    /// never a reason to fail a scan, so every failure here is silent and simply means "no".
    /// </summary>
    public CachedTree? TryLoad(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        var canonical = Canonical(rootPath);
        var file = Path.Combine(_directory, FileNameFor(canonical));

        lock (_gate)
        {
            if (!File.Exists(file))
                return null;

            var root = TreeCacheFile.TryRead(file, _limits.MaxNodesPerTree, out var header);
            if (root is null)
            {
                Discard(file, canonical);
                return null;
            }

            if (DateTimeOffset.UtcNow - header.WrittenAt > _limits.MaxAge)
            {
                Discard(file, canonical);
                return null;
            }

            Touch(canonical);
            return new CachedTree(root, header);
        }
    }

    /// <summary>
    /// Stores a measured tree. A partial scan is stored too, flagged as partial: a half picture
    /// of a drive is worth more than an empty window, and the UI says where it came from.
    /// </summary>
    public void Save(ScanResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var canonical = Canonical(result.Root.Path);

        // Below this the cache costs more than it saves, and above it the file is large enough
        // that writing it is its own delay.
        var nodes = result.Root.TotalDirectoryCount + 1;
        if (nodes < _limits.MinNodesToCache || nodes > _limits.MaxNodesPerTree)
            return;

        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);

                var fileName = FileNameFor(canonical);
                var file = Path.Combine(_directory, fileName);

                var written = TreeCacheFile.Write(
                    file, result.Root, result, _limits.MaxNodesPerTree);

                if (written is not { } nodeCount)
                    return;

                var index = ReadIndex();
                index.Entries.RemoveAll(e => PathsMatch(e.RootPath, canonical));
                index.Entries.Add(new CacheEntry
                {
                    RootPath = canonical,
                    FileName = fileName,
                    WrittenAt = DateTimeOffset.UtcNow,
                    LastUsedAt = DateTimeOffset.UtcNow,
                    FileBytes = new FileInfo(file).Length,
                    NodeCount = nodeCount,
                    TotalSize = result.TotalSize,
                    WasComplete = result.IsComplete,
                });

                Evict(index);
                WriteIndex(index);
            }
            catch (Exception)
            {
                // Two copies of the app racing, a read-only profile, a full disk. None of those
                // should turn into an error the user has to read.
            }
        }
    }

    /// <summary>Drops one root's cached tree, so the next scan starts from nothing.</summary>
    public void Forget(string rootPath)
    {
        var canonical = Canonical(rootPath);

        lock (_gate)
            Discard(Path.Combine(_directory, FileNameFor(canonical)), canonical);
    }

    /// <summary>Removes every cached tree.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(_directory))
                    Directory.Delete(_directory, recursive: true);
            }
            catch (Exception)
            {
                // A file held open by another copy of the app; the sweep will get it later.
            }
        }
    }

    /// <summary>Bytes the cache currently occupies, for the Settings page.</summary>
    public long TotalBytes()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_directory))
                    return 0;

                return Directory.EnumerateFiles(_directory).Sum(f => new FileInfo(f).Length);
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

    /// <summary>
    /// Housekeeping, run once at startup: expired and over-cap entries go, along with any tree
    /// the index has lost track of and any index entry whose tree is gone.
    /// </summary>
    public void Sweep()
    {
        lock (_gate)
        {
            try
            {
                if (!Directory.Exists(_directory))
                    return;

                var index = ReadIndex();
                var now = DateTimeOffset.UtcNow;

                foreach (var expired in index.Entries.Where(e => now - e.WrittenAt > _limits.MaxAge).ToList())
                {
                    DeleteFile(Path.Combine(_directory, expired.FileName));
                    index.Entries.Remove(expired);
                }

                index.Entries.RemoveAll(e => !File.Exists(Path.Combine(_directory, e.FileName)));
                Evict(index);

                var known = index.Entries.Select(e => e.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(_directory, "*" + TreeExtension))
                {
                    if (!known.Contains(Path.GetFileName(file)))
                        DeleteFile(file);
                }

                foreach (var stray in Directory.EnumerateFiles(_directory, "*.tmp"))
                    DeleteFile(stray);

                WriteIndex(index);
            }
            catch (Exception)
            {
                // Housekeeping must never stop the app from opening.
            }
        }
    }

    private void Evict(CacheIndex index)
    {
        var ordered = index.Entries.OrderBy(e => e.LastUsedAt).ToList();

        while (ordered.Count > _limits.MaxEntries
               || ordered.Sum(e => e.FileBytes) > _limits.MaxTotalBytes)
        {
            if (ordered.Count == 0)
                break;

            var victim = ordered[0];
            ordered.RemoveAt(0);
            index.Entries.Remove(victim);
            DeleteFile(Path.Combine(_directory, victim.FileName));
        }
    }

    private void Touch(string canonical)
    {
        try
        {
            var index = ReadIndex();
            var entry = index.Entries.FirstOrDefault(e => PathsMatch(e.RootPath, canonical));
            if (entry is null)
                return;

            index.Entries.Remove(entry);
            index.Entries.Add(entry with { LastUsedAt = DateTimeOffset.UtcNow });
            WriteIndex(index);
        }
        catch (Exception)
        {
            // Losing a last-used stamp only affects eviction order.
        }
    }

    private void Discard(string file, string canonical)
    {
        DeleteFile(file);

        try
        {
            var index = ReadIndex();
            if (index.Entries.RemoveAll(e => PathsMatch(e.RootPath, canonical)) > 0)
                WriteIndex(index);
        }
        catch (Exception)
        {
            // The sweep will reconcile the index later.
        }
    }

    private CacheIndex ReadIndex()
    {
        var path = Path.Combine(_directory, IndexFileName);

        try
        {
            if (!File.Exists(path))
                return new CacheIndex { Version = IndexVersion, Entries = [] };

            var index = JsonSerializer.Deserialize(File.ReadAllText(path), CacheJson.Default.CacheIndex);

            // No migration path, ever. A cache rebuilds itself in under a minute, so migration
            // code for disposable data would be pure risk for no benefit.
            if (index is null || index.Version != IndexVersion)
            {
                Clear();
                return new CacheIndex { Version = IndexVersion, Entries = [] };
            }

            return index;
        }
        catch (Exception)
        {
            return new CacheIndex { Version = IndexVersion, Entries = [] };
        }
    }

    private void WriteIndex(CacheIndex index)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, IndexFileName);
            var temporary = path + ".tmp";

            File.WriteAllText(temporary, JsonSerializer.Serialize(index, CacheJson.Default.CacheIndex));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            // An index that cannot be written costs eviction accuracy, not correctness: every
            // tree file still carries its own header.
        }
    }

    private static void DeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
            // Held open by another copy of the app; it will be swept next time.
        }
    }

    private static bool PathsMatch(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Canonicalized before hashing, so C:\PROGRA~1, a junction alias and C:\Program Files all
    /// name the same cache file rather than three.
    /// </summary>
    private static string Canonical(string rootPath)
    {
        try
        {
            return PathCanonicalizer.Canonicalize(rootPath).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception)
        {
            return rootPath.TrimEnd(Path.DirectorySeparatorChar);
        }
    }

    private static string FileNameFor(string canonical)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16)) + TreeExtension;
    }
}
