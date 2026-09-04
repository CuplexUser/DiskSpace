using System.IO.Compression;
using DiskSpace.Core.Model;
using DiskSpace.Core.Quarantine;
using DiskSpace.Core.Rules;

namespace DiskSpace.Core.Tests;

public sealed class QuarantineStoreTests
{
    private static CleanupRule Rule(string root) => new()
    {
        Id = "orphan.test",
        Name = "Test orphan",
        Category = "Orphaned application data",
        Risk = RiskLevel.Review,
        Description = "Test",
        WhatBreaks = "Test",
        Root = root,
        Targets = [root],
        RemoveTargetDirectory = true,
    };

    // SearchAllLocations is off everywhere in this file on purpose: a store that also scans
    // the default location and every fixed volume would see the real quarantined items on the
    // machine running the tests, and PurgeExpired would delete them.
    private static QuarantineStore StoreAt(string location, QuarantineMode mode) =>
        new(new QuarantineOptions { Location = location, Mode = mode, SearchAllLocations = false });

    [Fact]
    public async Task Archives_a_folder_then_restores_it_identically()
    {
        using var fixture = new ScanFixture();
        var source = fixture.Dir("orphan");
        File.WriteAllText(Path.Combine(source, "config.json"), """{"token":"abc"}""");
        Directory.CreateDirectory(Path.Combine(source, "nested", "deeper"));
        File.WriteAllText(Path.Combine(source, "nested", "deeper", "data.txt"), "payload");
        File.WriteAllText(Path.Combine(source, "unicode-ä-日本.txt"), "unicode content");
        Directory.CreateDirectory(Path.Combine(source, "empty-on-purpose"));

        var store = StoreAt(fixture.Dir("quarantine"), QuarantineMode.ArchiveToOtherVolume);
        var manifest = await store.QuarantineAsync(source, Rule(source));

        // Source is gone, archive is present.
        Assert.False(Directory.Exists(source));
        Assert.True(File.Exists(manifest.ArchivePath));
        Assert.True(File.Exists(manifest.ManifestPath));
        Assert.Equal(3, manifest.FileCount);

        await store.RestoreAsync(manifest);

        Assert.True(Directory.Exists(source));
        Assert.Equal("""{"token":"abc"}""", File.ReadAllText(Path.Combine(source, "config.json")));
        Assert.Equal("payload", File.ReadAllText(Path.Combine(source, "nested", "deeper", "data.txt")));
        Assert.Equal("unicode content", File.ReadAllText(Path.Combine(source, "unicode-ä-日本.txt")));
        Assert.True(Directory.Exists(Path.Combine(source, "empty-on-purpose")));

        // A restored item is no longer quarantined.
        Assert.False(File.Exists(manifest.ArchivePath));
    }

    [Fact]
    public async Task Leaves_the_source_untouched_when_archiving_is_cancelled()
    {
        using var fixture = new ScanFixture();
        var source = fixture.Dir("orphan");
        for (var i = 0; i < 200; i++)
            fixture.File($"orphan/file{i}.bin", 4096);

        var store = StoreAt(fixture.Dir("quarantine"), QuarantineMode.ArchiveToOtherVolume);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.QuarantineAsync(source, Rule(source), null, cancellation.Token));

        // This is the property the ordering exists to guarantee.
        Assert.True(Directory.Exists(source));
        Assert.Equal(200, Directory.GetFiles(source).Length);

        // And no partial archive is left lying around.
        var archives = Directory.GetFiles(Path.Combine(fixture.Root, "quarantine"), "*.zip");
        Assert.Empty(archives);
    }

    [Fact]
    public async Task Moving_aside_on_the_same_volume_preserves_everything()
    {
        using var fixture = new ScanFixture();
        var source = fixture.Dir("orphan");
        File.WriteAllText(Path.Combine(source, "keep.txt"), "kept");

        var store = StoreAt(fixture.Dir("quarantine"), QuarantineMode.MoveOnSameVolume);
        var manifest = await store.QuarantineAsync(source, Rule(source));

        Assert.False(Directory.Exists(source));
        Assert.NotNull(manifest.MovedToPath);
        Assert.True(File.Exists(Path.Combine(manifest.MovedToPath!, "keep.txt")));

        await store.RestoreAsync(manifest);

        Assert.Equal("kept", File.ReadAllText(Path.Combine(source, "keep.txt")));
    }

    [Fact]
    public async Task Does_not_follow_a_junction_out_of_the_quarantined_folder()
    {
        using var fixture = new ScanFixture();
        var source = fixture.Dir("orphan");
        var outside = fixture.Dir("outside");
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "must survive");
        File.WriteAllText(Path.Combine(source, "own.txt"), "mine");
        fixture.CreateJunction("orphan/link", outside);

        var store = StoreAt(fixture.Dir("quarantine"), QuarantineMode.ArchiveToOtherVolume);
        var manifest = await store.QuarantineAsync(source, Rule(source));

        // Only the folder's own file was archived, not the junction's target.
        Assert.Equal(1, manifest.FileCount);
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public async Task Lists_and_purges_staged_items()
    {
        using var fixture = new ScanFixture();
        var location = fixture.Dir("quarantine");
        var source = fixture.Dir("orphan");
        fixture.File("orphan/a.bin", 100);

        var store = StoreAt(location, QuarantineMode.ArchiveToOtherVolume);
        var manifest = await store.QuarantineAsync(source, Rule(source));

        Assert.Contains(store.List(), m => m.Id == manifest.Id);

        QuarantineStore.Purge(manifest);

        Assert.DoesNotContain(store.List(), m => m.Id == manifest.Id);
        Assert.False(File.Exists(manifest.ArchivePath));
    }

    [Fact]
    public async Task Purges_only_items_past_their_retention_date()
    {
        using var fixture = new ScanFixture();
        var location = fixture.Dir("quarantine");
        var source = fixture.Dir("orphan");
        fixture.File("orphan/a.bin", 100);

        // Retention already elapsed, so this item is due immediately.
        var store = new QuarantineStore(new QuarantineOptions
        {
            Location = location,
            Mode = QuarantineMode.ArchiveToOtherVolume,
            Retention = TimeSpan.FromDays(-1),
            SearchAllLocations = false,
        });

        await store.QuarantineAsync(source, Rule(source));
        Assert.Single(store.List());

        Assert.Equal(1, store.PurgeExpired());
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task Archive_entries_carry_relative_paths()
    {
        using var fixture = new ScanFixture();
        var source = fixture.Dir("orphan");
        fixture.File("orphan/sub/leaf.bin", 10);

        var store = StoreAt(fixture.Dir("quarantine"), QuarantineMode.ArchiveToOtherVolume);
        var manifest = await store.QuarantineAsync(source, Rule(source));

        using var archive = ZipFile.OpenRead(manifest.ArchivePath);

        // Relative, so a restore cannot write outside the original folder.
        Assert.Contains(archive.Entries, e => e.FullName.Replace('\\', '/') == "sub/leaf.bin");
        Assert.DoesNotContain(archive.Entries, e => Path.IsPathRooted(e.FullName));
    }
}
