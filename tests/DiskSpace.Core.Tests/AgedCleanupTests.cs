using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;
using DiskSpace.Core.Rules;
using DiskSpace.Core.Safety;

namespace DiskSpace.Core.Tests;

/// <summary>
/// Rules that take only what has gone unused for a while, one file at a time. This is what
/// lets a temp directory be cleaned at all: something in it is always a minute old.
/// </summary>
public sealed class AgedCleanupTests
{
    private static readonly DateTime LongAgo = DateTime.UtcNow.AddDays(-30);
    private static readonly DateTime Cutoff = DateTime.UtcNow.AddDays(-1);

    private static string Old(string path)
    {
        File.SetCreationTimeUtc(path, LongAgo);
        File.SetLastWriteTimeUtc(path, LongAgo);
        return path;
    }

    private static string OldDirectory(string path)
    {
        Directory.SetCreationTimeUtc(path, LongAgo);
        Directory.SetLastWriteTimeUtc(path, LongAgo);
        return path;
    }

    private static CleanupRule TempRule(string root) => new()
    {
        Id = "win.test-temp",
        Name = "Test temp",
        Category = "Windows and system",
        Risk = RiskLevel.Safe,
        Description = "Test",
        WhatBreaks = "Nothing",
        Root = root,
        Targets = [root],
        MinimumAge = TimeSpan.FromDays(1),
    };

    [Fact]
    public void Takes_old_files_and_leaves_new_ones()
    {
        using var fixture = new ScanFixture();
        var temp = fixture.Dir("temp");
        var old = Old(fixture.File("temp/old.bin", 1000));
        var deep = Old(fixture.File("temp/sub/deep.bin", 2000));
        var fresh = fixture.File("temp/fresh.bin", 4000);

        var reclaimed = SafeDelete.DeleteOlderThan(temp, Cutoff);

        Assert.Equal(3000, reclaimed);
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(deep));
        Assert.True(File.Exists(fresh));
        Assert.True(Directory.Exists(temp));
    }

    [Fact]
    public void A_file_copied_in_today_is_new_even_with_an_old_write_time()
    {
        // Copying keeps the source's write time, so the write time alone would call a file
        // that arrived a minute ago years old.
        using var fixture = new ScanFixture();
        var temp = fixture.Dir("temp");
        var copied = fixture.File("temp/copied.bin", 1000);
        File.SetLastWriteTimeUtc(copied, LongAgo);

        Assert.Empty(SafeDelete.EnumerateOlderThan(temp, Cutoff));
        Assert.Equal(0, SafeDelete.DeleteOlderThan(temp, Cutoff));
        Assert.True(File.Exists(copied));
    }

    [Fact]
    public void Removes_an_old_folder_it_emptied_but_not_a_new_empty_one()
    {
        using var fixture = new ScanFixture();
        var temp = fixture.Dir("temp");
        Old(fixture.File("temp/stale/a.bin", 100));
        var stale = Path.Combine(temp, "stale");
        var working = fixture.Dir("temp/just-created");

        // Set after the file inside it, which would otherwise bump the folder's write time.
        OldDirectory(stale);

        // Deleting the file touches the folder again, exactly as it would on a real run, so
        // the folder is aged once more before the pass that is allowed to remove it.
        SafeDelete.DeleteOlderThan(temp, Cutoff);
        if (Directory.Exists(stale))
        {
            OldDirectory(stale);
            SafeDelete.DeleteOlderThan(temp, Cutoff);
        }

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(working));
    }

    [Fact]
    public void Does_not_follow_a_junction_out_of_the_folder()
    {
        using var fixture = new ScanFixture();
        var temp = fixture.Dir("temp");
        var outside = fixture.Dir("outside");
        var precious = Old(fixture.File("outside/precious.bin", 500));
        fixture.CreateJunction("temp/link", outside);

        Assert.Empty(SafeDelete.EnumerateOlderThan(temp, Cutoff));
        SafeDelete.DeleteOlderThan(temp, Cutoff);

        Assert.True(File.Exists(precious));
    }

    [Fact]
    public async Task The_catalog_measures_only_what_the_cleanup_will_take()
    {
        using var fixture = new ScanFixture();
        var temp = fixture.Dir("temp");
        Old(fixture.File("temp/old.bin", 1000));
        fixture.File("temp/fresh.bin", 4000);

        var rule = TempRule(temp);
        var finding = Assert.Single(await new RuleCatalog([new Provider(rule)]).ResolveAsync());

        Assert.Equal(1000, finding.Size);
        Assert.Equal(1, finding.FileCount);

        var executor = new CleanupExecutor(auditLogDirectory: fixture.Dir("logs"));
        var report = await executor.ExecuteAsync(executor.Plan([finding]));

        Assert.Equal(1000, report.BytesReclaimed);
        Assert.True(File.Exists(Path.Combine(temp, "fresh.bin")));
    }

    [Fact]
    public async Task A_folder_holding_only_new_files_is_not_offered()
    {
        using var fixture = new ScanFixture();
        var temp = fixture.Dir("temp");
        fixture.File("temp/fresh.bin", 4000);

        Assert.Empty(await new RuleCatalog([new Provider(TempRule(temp))]).ResolveAsync());
    }

    private sealed class Provider(params CleanupRule[] rules) : IRuleProvider
    {
        public string Name => "Test";

        public IEnumerable<CleanupRule> GetRules() => rules;
    }
}
