using DiskSpace.Core.Caching;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Tests;

/// <summary>
/// Revalidating a cached tree in place is where the scanner does its most delicate work: it
/// applies signed deltas to every ancestor of a directory whose contents moved. A sign error
/// here produces numbers that look plausible and are wrong, so each shape of change gets its
/// own test.
/// </summary>
public sealed class CachedRevalidationTests : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(
        Path.GetTempPath(), "DiskSpace.Tests", "revalidate-" + Guid.NewGuid().ToString("N"));

    private TreeCache Cache() =>
        new(_cacheDirectory, new TreeCacheLimits { MinNodesToCache = 1 });

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

    private static DirectoryNode Child(DirectoryNode node, string name) =>
        node.Children.Single(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Scans, caches, and hands back the tree as it would be loaded on a later run.</summary>
    private async Task<CachedTree> ScanAndReload(ScanFixture fixture)
    {
        var cache = Cache();
        cache.Save(await new FastDirectoryScanner().ScanAsync(fixture.Root));

        var loaded = cache.TryLoad(fixture.Root);
        Assert.NotNull(loaded);
        return loaded;
    }

    private static async Task<ScanResult> Revalidate(DirectoryNode cachedRoot, ScanOptions? options = null)
    {
        await using var scanner = new ProgressiveScanner(options);
        await scanner.StartFromAsync(cachedRoot);
        return await scanner.RunToCompletionAsync();
    }

    /// <summary>
    /// Stamps every directory with a fixed time.
    ///
    /// Windows writes a directory's timestamp back lazily, so two scans taken moments apart can
    /// legitimately read different values for a folder that nobody touched. That costs trust
    /// mode some adoptions, which is the safe direction, but it makes a test that counts them
    /// depend on flush timing. Fixing the stamps first removes the race and leaves the
    /// comparison itself under test.
    /// </summary>
    private static void StampDirectories(string root, DateTime utc)
    {
        Directory.SetLastWriteTimeUtc(root, utc);

        foreach (var directory in Directory.EnumerateDirectories(
                     root, "*", SearchOption.AllDirectories))
        {
            Directory.SetLastWriteTimeUtc(directory, utc);
        }
    }

    [Fact]
    public async Task An_unchanged_tree_revalidates_to_the_same_totals()
    {
        using var fixture = new ScanFixture();
        fixture.File("a.bin", 1000);
        fixture.File("sub/b.bin", 2000);
        fixture.File("sub/deep/c.bin", 3000);
        fixture.File("other/d.bin", 4000);

        var cached = await ScanAndReload(fixture);
        var result = await Revalidate(cached.Root);

        Assert.True(result.IsComplete);
        Assert.Equal(10_000, result.TotalSize);
        Assert.Equal(4, result.TotalFileCount);
        Assert.Equal(3, result.TotalDirectoryCount);

        // Nothing may still be claiming to be an estimate once it has been listed for real.
        Assert.False(result.Root.IsFromCache);
        Assert.False(Child(result.Root, "sub").IsFromCache);
    }

    [Fact]
    public async Task A_file_that_grew_in_place_is_picked_up()
    {
        using var fixture = new ScanFixture();
        fixture.File("sub/log.bin", 1000);

        var cached = await ScanAndReload(fixture);

        var directory = new DirectoryInfo(Path.Combine(fixture.Root, "sub"));
        var stamp = directory.LastWriteTimeUtc;

        using (var stream = new FileStream(
                   Path.Combine(fixture.Root, "sub", "log.bin"), FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(9000);
        }

        // Restored deliberately: NTFS does not move a folder's timestamp when a file inside it
        // is written to, and this test exists to prove the scanner does not rely on it doing so.
        directory.LastWriteTimeUtc = stamp;

        var result = await Revalidate(cached.Root);

        Assert.Equal(9000, result.TotalSize);
        Assert.Equal(9000, Child(result.Root, "sub").TotalSize);
    }

    [Fact]
    public async Task A_deleted_subtree_is_subtracted_from_every_ancestor()
    {
        using var fixture = new ScanFixture();
        fixture.File("keep/a.bin", 1000);
        fixture.File("keep/gone/b.bin", 2000);
        fixture.File("keep/gone/deeper/c.bin", 3000);

        var cached = await ScanAndReload(fixture);
        Directory.Delete(Path.Combine(fixture.Root, "keep", "gone"), recursive: true);

        var result = await Revalidate(cached.Root);

        Assert.Equal(1000, result.TotalSize);
        Assert.Equal(1, result.TotalFileCount);
        Assert.Equal(1, result.TotalDirectoryCount);

        var keep = Child(result.Root, "keep");
        Assert.Equal(1000, keep.TotalSize);
        Assert.Equal(0, keep.TotalDirectoryCount);
        Assert.Empty(keep.Children);
    }

    [Fact]
    public async Task A_new_subtree_is_credited_to_every_ancestor()
    {
        using var fixture = new ScanFixture();
        fixture.File("keep/a.bin", 1000);

        var cached = await ScanAndReload(fixture);
        fixture.File("keep/fresh/deeper/b.bin", 5000);

        var result = await Revalidate(cached.Root);

        Assert.Equal(6000, result.TotalSize);
        Assert.Equal(2, result.TotalFileCount);
        Assert.Equal(3, result.TotalDirectoryCount);
        Assert.Equal(6000, Child(result.Root, "keep").TotalSize);
    }

    [Fact]
    public async Task Unchanged_directories_keep_their_node_objects()
    {
        using var fixture = new ScanFixture();
        fixture.File("sub/deep/a.bin", 1000);

        var cached = await ScanAndReload(fixture);

        var before = Child(Child(cached.Root, "sub"), "deep");
        var result = await Revalidate(cached.Root);
        var after = Child(Child(result.Root, "sub"), "deep");

        // The tree view holds these objects in its rows. Replacing them instead of updating them
        // would silently break selection, the treemap and the breadcrumb.
        Assert.Same(before, after);
    }

    [Fact]
    public async Task Trusting_unchanged_folders_adopts_a_subtree_without_listing_it()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 60; i++)
            fixture.File($"stable/branch{i}/leaf.bin", 100);

        StampDirectories(fixture.Root, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var cached = await ScanAndReload(fixture);

        var trusting = new ScanOptions { TrustUnchangedFolders = true };
        await using var scanner = new ProgressiveScanner(trusting);
        await scanner.StartFromAsync(cached.Root);
        var result = await scanner.RunToCompletionAsync();

        Assert.Equal(6000, result.TotalSize);

        // Only the root is listed. Everything below it kept its timestamp, so the whole tree
        // is adopted from the cache without a single further directory being read.
        Assert.Equal(1L, scanner.Snapshot().DirectoriesScanned);

        var stable = Child(result.Root, "stable");
        Assert.True(Child(stable, "branch0").IsFromCache);
    }

    [Fact]
    public async Task Verifying_lists_every_folder_even_when_nothing_changed()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 60; i++)
            fixture.File($"stable/branch{i}/leaf.bin", 100);

        var cached = await ScanAndReload(fixture);

        await using var scanner = new ProgressiveScanner();
        await scanner.StartFromAsync(cached.Root);
        var result = await scanner.RunToCompletionAsync();

        Assert.Equal(6000, result.TotalSize);

        // The default is to measure, not to believe: root, stable, and all sixty branches.
        Assert.Equal(62, scanner.Snapshot().DirectoriesScanned);
    }
}
