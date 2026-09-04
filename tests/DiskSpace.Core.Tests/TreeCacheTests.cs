using DiskSpace.Core.Caching;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Tests;

public sealed class TreeCacheTests : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(
        Path.GetTempPath(), "DiskSpace.Tests", "cache-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Every store here is built on a throwaway directory, for the same reason
    /// <c>AuditLog.StartRun</c> takes one: a test run must not leave trees in the cache of
    /// whoever is running it, or evict the ones they wanted.
    /// </summary>
    private TreeCache Cache(TreeCacheLimits? limits = null) =>
        new(_cacheDirectory, limits ?? new TreeCacheLimits { MinNodesToCache = 1 });

    private string SingleTreeFile() =>
        Directory.EnumerateFiles(_cacheDirectory, "*.dstree").Single();

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
                Directory.Delete(_cacheDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A stray lock in a test run should not fail the run itself.
        }
    }

    [Fact]
    public async Task Round_trips_a_scanned_tree()
    {
        using var fixture = new ScanFixture();
        fixture.File("a.bin", 1000);
        fixture.File("sub/b.bin", 2000);
        fixture.File("sub/deep/c.bin", 3000);
        fixture.Dir("empty");
        fixture.CreateJunction("loop", fixture.Root);

        var result = await new FastDirectoryScanner().ScanAsync(fixture.Root);

        var cache = Cache();
        cache.Save(result);

        var loaded = cache.TryLoad(fixture.Root);
        Assert.NotNull(loaded);
        Assert.True(loaded.Header.TreeWasComplete);
        Assert.Equal(result.Root.Path, loaded.Root.Path);

        var expected = Flatten(result.Root);
        var actual = Flatten(loaded.Root);

        Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));

        foreach (var (relative, node) in expected)
        {
            var other = actual[relative];
            Assert.Equal(node.Name, other.Name);
            Assert.Equal(node.Path, other.Path);
            Assert.Equal(node.OwnSize, other.OwnSize);
            Assert.Equal(node.OwnFileCount, other.OwnFileCount);
            Assert.Equal(node.TotalSize, other.TotalSize);
            Assert.Equal(node.TotalFileCount, other.TotalFileCount);
            Assert.Equal(node.TotalDirectoryCount, other.TotalDirectoryCount);
            Assert.Equal(node.LastWriteUtc, other.LastWriteUtc);
            Assert.Equal(node.OwnLastWriteUtc, other.OwnLastWriteUtc);
            Assert.Equal(node.IsReparsePoint, other.IsReparsePoint);

            // A tree straight from the cache has to be renderable on its own: expandable rows,
            // working bars and percentages, and every value marked as an estimate.
            Assert.True(other.IsFromCache);
            Assert.True(other.IsEnumerated);
            Assert.True(other.IsComplete);
        }
    }

    private static Dictionary<string, DirectoryNode> Flatten(DirectoryNode root)
    {
        var map = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(DirectoryNode Node, string Relative)>();
        stack.Push((root, string.Empty));

        while (stack.Count > 0)
        {
            var (node, relative) = stack.Pop();
            map[relative] = node;

            foreach (var child in node.Children)
                stack.Push((child, relative.Length == 0 ? child.Name : $"{relative}/{child.Name}"));
        }

        return map;
    }

    [Fact]
    public async Task Reconstructs_a_tree_deeper_than_json_would_allow()
    {
        using var fixture = new ScanFixture();

        // Well past JsonSerializerOptions.MaxDepth, which is 64 and would simply throw. This is
        // the case that decided the file format, so it gets a test of its own.
        const int depth = 120;
        var relative = string.Join('/', Enumerable.Repeat("d", depth));
        fixture.File($"{relative}/leaf.bin", 4242);

        var result = await new FastDirectoryScanner().ScanAsync(fixture.Root);

        var cache = Cache();
        cache.Save(result);

        var loaded = cache.TryLoad(fixture.Root);
        Assert.NotNull(loaded);
        Assert.Equal(4242, loaded.Root.TotalSize);

        var levels = 0;
        for (var node = loaded.Root; node.Children.Count > 0; node = node.Children[0])
            levels++;

        Assert.Equal(depth, levels);
    }

    [Fact]
    public async Task Rejects_a_truncated_file_without_throwing()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 40; i++)
            fixture.File($"branch{i}/leaf.bin", 100);

        var cache = Cache();
        cache.Save(await new FastDirectoryScanner().ScanAsync(fixture.Root));

        var file = SingleTreeFile();
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
            stream.SetLength(stream.Length * 6 / 10);

        // The realistic failure: killed partway through a write. It must read as "no cache".
        Assert.Null(cache.TryLoad(fixture.Root));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Rejects_a_file_whose_header_is_not_ours()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 40; i++)
            fixture.File($"branch{i}/leaf.bin", 100);

        var cache = Cache();
        cache.Save(await new FastDirectoryScanner().ScanAsync(fixture.Root));

        var file = SingleTreeFile();
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Write))
        {
            // A version bump, or any other build's file, must be discarded rather than misread.
            stream.Position = 4;
            stream.Write([99, 0, 0, 0]);
        }

        Assert.Null(cache.TryLoad(fixture.Root));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Does_not_cache_a_tree_that_rescans_faster_than_it_loads()
    {
        using var fixture = new ScanFixture();
        fixture.File("a.bin", 100);

        var cache = new TreeCache(_cacheDirectory, new TreeCacheLimits { MinNodesToCache = 50 });
        cache.Save(await new FastDirectoryScanner().ScanAsync(fixture.Root));

        Assert.Null(cache.TryLoad(fixture.Root));
    }

    [Fact]
    public async Task Evicts_the_least_recently_used_tree_past_the_cap()
    {
        using var fixture = new ScanFixture();

        var roots = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            fixture.File($"root{i}/branch/leaf.bin", 100);
            roots.Add(Path.Combine(fixture.Root, $"root{i}"));
        }

        var cache = new TreeCache(
            _cacheDirectory, new TreeCacheLimits { MinNodesToCache = 1, MaxEntries = 2 });

        foreach (var root in roots)
        {
            cache.Save(await new FastDirectoryScanner().ScanAsync(root));

            // The stamps have one-second resolution in neither direction, but eviction orders by
            // them, so the saves need to be distinguishable.
            await Task.Delay(10);
        }

        Assert.Null(cache.TryLoad(roots[0]));
        Assert.NotNull(cache.TryLoad(roots[1]));
        Assert.NotNull(cache.TryLoad(roots[2]));
    }

    [Fact]
    public async Task Forgetting_a_root_removes_its_tree()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 40; i++)
            fixture.File($"branch{i}/leaf.bin", 100);

        var cache = Cache();
        cache.Save(await new FastDirectoryScanner().ScanAsync(fixture.Root));
        Assert.NotNull(cache.TryLoad(fixture.Root));

        cache.Forget(fixture.Root);
        Assert.Null(cache.TryLoad(fixture.Root));
    }

    [Fact]
    public async Task A_cancelled_scan_round_trips_as_incomplete()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 2000; i++)
            fixture.File($"branch{i}/leaf.bin", 64);

        var options = new ScanOptions { ShallowDepth = 0, MaxDegreeOfParallelism = 1 };
        await using var scanner = new ProgressiveScanner(options);
        await scanner.StartAsync(fixture.Root);
        scanner.Cancel();

        var result = await scanner.RunToCompletionAsync();
        Assert.False(result.IsComplete);

        var cache = Cache();
        cache.Save(result);

        // Half a picture of a drive is worth more than an empty window, as long as the file says
        // which it is.
        var loaded = cache.TryLoad(fixture.Root);
        Assert.NotNull(loaded);
        Assert.False(loaded.Header.TreeWasComplete);
        Assert.False(loaded.Root.IsComplete);
    }
}
