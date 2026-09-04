using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;
using DiskSpace.Core.Quarantine;
using DiskSpace.Core.Rules;

namespace DiskSpace.Core.Tests;

public sealed class CleanupExecutorTests
{
    private static CleanupRule CacheRule(string root, bool removeRoot = false) => new()
    {
        Id = "pkg.test",
        Name = "Test cache",
        Category = "Package manager caches",
        Risk = RiskLevel.Safe,
        Description = "Test",
        WhatBreaks = "Nothing",
        Root = root,
        Targets = [root],
        RemoveTargetDirectory = removeRoot,
    };

    private static CleanupFinding Finding(CleanupRule rule, string path, long size) => new()
    {
        Rule = rule,
        Path = path,
        Size = size,
        FileCount = 1,
        LastWriteUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task Empties_a_cache_directory_but_keeps_the_directory()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("cache");
        fixture.File("cache/a.bin", 1000);
        fixture.File("cache/sub/b.bin", 2000);

        var rule = CacheRule(cache);
        var executor = new CleanupExecutor();
        var plan = executor.Plan([Finding(rule, cache, 3000)]);

        var report = await executor.ExecuteAsync(plan);

        Assert.Equal(1, report.SucceededCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(3000, report.BytesReclaimed);

        // The directory survives: several package managers error out if it is missing.
        Assert.True(Directory.Exists(cache));
        Assert.Empty(Directory.GetFiles(cache, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Removes_the_directory_itself_when_the_rule_says_so()
    {
        using var fixture = new ScanFixture();
        var leftover = fixture.Dir("leftover");
        fixture.File("leftover/a.bin", 500);

        var rule = CacheRule(leftover, removeRoot: true);
        var executor = new CleanupExecutor();

        await executor.ExecuteAsync(executor.Plan([Finding(rule, leftover, 500)]));

        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public async Task Refuses_an_item_the_guard_rejects_even_when_it_is_in_the_plan()
    {
        // A plan is not a licence: the guard runs again at execution time.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var rule = new CleanupRule
        {
            Id = "pkg.evil",
            Name = "Malformed rule",
            Category = "Test",
            Risk = RiskLevel.Safe,
            Description = "Points somewhere it must not",
            WhatBreaks = "Everything",
            Root = Path.GetPathRoot(windows)!,
            Targets = [Path.Combine(windows, "System32")],
        };

        var executor = new CleanupExecutor();
        var plan = executor.Plan([Finding(rule, Path.Combine(windows, "System32"), 1)]);

        var report = await executor.ExecuteAsync(plan);

        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Contains("safety check", report.Failures.Single().Error!, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(Path.Combine(windows, "System32")));
    }

    [Fact]
    public async Task Deletes_a_junction_link_without_touching_its_target()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("cache");
        var outside = fixture.Dir("outside");
        File.WriteAllText(Path.Combine(outside, "precious.txt"), "must survive");
        fixture.File("cache/own.bin", 100);
        fixture.CreateJunction("cache/link", outside);

        var rule = CacheRule(cache);
        var executor = new CleanupExecutor();

        await executor.ExecuteAsync(executor.Plan([Finding(rule, cache, 100)]));

        // The link is gone, the data it pointed at is not.
        Assert.False(Directory.Exists(Path.Combine(cache, "link")));
        Assert.True(File.Exists(Path.Combine(outside, "precious.txt")));
        Assert.Equal("must survive", File.ReadAllText(Path.Combine(outside, "precious.txt")));
    }

    [Fact]
    public async Task Reports_the_process_holding_a_file_open()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("cache");
        var locked = fixture.File("cache/locked.bin", 100);
        fixture.File("cache/free.bin", 200);

        var rule = CacheRule(cache, removeRoot: true);
        var executor = new CleanupExecutor();

        using (var hold = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = await executor.ExecuteAsync(
                executor.Plan([Finding(rule, cache, 300)]));

            // The unlocked file still goes; the locked one simply stays.
            Assert.False(File.Exists(Path.Combine(cache, "free.bin")));
            Assert.True(File.Exists(locked));
            Assert.Equal(200, report.BytesReclaimed);
        }
    }

    [Fact]
    public void Plans_orphaned_data_for_quarantine_and_caches_for_deletion()
    {
        using var fixture = new ScanFixture();
        var orphan = fixture.Dir("orphan");
        var cache = fixture.Dir("cache");

        var orphanRule = new CleanupRule
        {
            Id = "orphan.something",
            Name = "something",
            Category = "Orphaned application data",
            Risk = RiskLevel.Review,
            Description = "Test",
            WhatBreaks = "Test",
            Root = orphan,
            Targets = [orphan],
            RemoveTargetDirectory = true,
        };

        var plan = new CleanupExecutor().Plan(
        [
            Finding(orphanRule, orphan, 10),
            Finding(CacheRule(cache), cache, 20),
        ]);

        Assert.Equal(
            Disposal.Quarantine,
            plan.Items.Single(i => i.Path == orphan).Disposal);
        Assert.Equal(
            Disposal.Delete,
            plan.Items.Single(i => i.Path == cache).Disposal);
        Assert.True(plan.ContainsQuarantine);
        Assert.True(plan.NeedsExplicitConfirmation);
    }

    [Fact]
    public void Excludes_report_only_findings_from_any_plan()
    {
        var rule = new CleanupRule
        {
            Id = "large.pagefile",
            Name = "Page file",
            Category = "Large items",
            Risk = RiskLevel.ReportOnly,
            Description = "Test",
            WhatBreaks = "Never delete this",
            Root = @"C:\pagefile.sys",
            Targets = [@"C:\pagefile.sys"],
        };

        var plan = new CleanupExecutor().Plan([Finding(rule, @"C:\pagefile.sys", 999)]);

        Assert.Empty(plan.Items);
    }

    [Fact]
    public async Task Writes_an_audit_entry_for_every_item()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("cache");
        fixture.File("cache/a.bin", 1500);

        var before = DateTimeOffset.Now.AddSeconds(-5);
        var executor = new CleanupExecutor();
        await executor.ExecuteAsync(executor.Plan([Finding(CacheRule(cache), cache, 1500)]));

        var latest = AuditLog.ListRuns().FirstOrDefault();
        Assert.NotNull(latest);

        var entries = AuditLog.Read(latest!);
        var entry = entries.LastOrDefault(e => e.Path == cache);

        Assert.NotNull(entry);
        Assert.True(entry!.Succeeded);
        Assert.Equal(1500, entry.Bytes);
        Assert.Equal("pkg.test", entry.RuleId);
        Assert.Equal("Safe", entry.Risk);
        Assert.True(entry.Timestamp >= before);
    }

    [Fact]
    public async Task Quarantines_through_the_executor_and_restores()
    {
        using var fixture = new ScanFixture();
        var orphan = fixture.Dir("orphan");
        File.WriteAllText(Path.Combine(orphan, "settings.json"), "{}");

        var rule = new CleanupRule
        {
            Id = "orphan.tool",
            Name = "tool",
            Category = "Orphaned application data",
            Risk = RiskLevel.Review,
            Description = "Test",
            WhatBreaks = "Test",
            Root = orphan,
            Targets = [orphan],
            RemoveTargetDirectory = true,
        };

        var store = new QuarantineStore(new QuarantineOptions
        {
            Location = fixture.Dir("quarantine"),
            Mode = QuarantineMode.ArchiveToOtherVolume,
        });

        var executor = new CleanupExecutor(store);
        var report = await executor.ExecuteAsync(executor.Plan([Finding(rule, orphan, 2)]));

        Assert.Equal(1, report.SucceededCount);
        Assert.False(Directory.Exists(orphan));

        var manifest = store.List().Single(m => m.OriginalPath == orphan);
        await store.RestoreAsync(manifest);

        Assert.Equal("{}", File.ReadAllText(Path.Combine(orphan, "settings.json")));
    }
}
