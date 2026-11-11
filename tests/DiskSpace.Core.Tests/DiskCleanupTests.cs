using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;
using DiskSpace.Core.Rules;
using DiskSpace.Core.Safety;
using Microsoft.Win32;

namespace DiskSpace.Core.Tests;

/// <summary>
/// The Windows Disk Cleanup handlers, hosted.
///
/// The handler tests run Windows' real data-driven cleaner, the one behind most of Disk
/// Cleanup's list, but registered under a throwaway key in HKEY_CURRENT_USER and pointed at a
/// fixture folder. That exercises the whole COM path, measure and purge, without going near
/// anything a real handler would remove.
/// </summary>
public sealed class DiskCleanupTests
{
    /// <summary>The CLSID of the data-driven cleaner that ships with Windows (dataclen.dll).</summary>
    private const string DataDrivenCleaner = "{C0E13E61-0CC6-11d1-BBB6-0060978B2AE6}";

    private const int IncludeSubdirectories = 0x1;
    private const int RemoveDirectories = 0x40;

    /// <summary>A handler registration that lives only as long as one test.</summary>
    private sealed class Registration : IDisposable
    {
        private const string Home = @"Software\DiskSpace.Tests";

        private readonly string _parent = $@"{Home}\{Guid.NewGuid():N}";

        public Registration(string folder)
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{_parent}\{KeyName}");
            key.SetValue(null, DataDrivenCleaner);
            key.SetValue("Folder", folder);
            key.SetValue("FileList", "*.*");
            key.SetValue("Flags", IncludeSubdirectories | RemoveDirectories, RegistryValueKind.DWord);
        }

        public static string KeyName => "Fixture";

        public DiskCleanupHandler Handler(string volume) =>
            new(Registry.CurrentUser, _parent, KeyName, volume);

        public void Dispose()
        {
            Registry.CurrentUser.DeleteSubKeyTree(_parent, throwOnMissingSubKey: false);

            try
            {
                // Leaves nothing behind once the last test's registration has gone.
                Registry.CurrentUser.DeleteSubKey(Home, throwOnMissingSubKey: false);
            }
            catch (InvalidOperationException)
            {
                // Another test still has a registration under it.
            }
        }
    }

    [Fact]
    public async Task A_hosted_handler_measures_what_it_would_remove()
    {
        using var fixture = new ScanFixture();
        var target = fixture.Dir("target");
        fixture.File("target/a.bin", 1000);
        fixture.File("target/sub/b.bin", 2000);

        using var registration = new Registration(target);
        var handler = registration.Handler(Path.GetPathRoot(target)!);

        Assert.Equal(3000, await handler.MeasureAsync());

        // Measuring is a read: everything is still there.
        Assert.True(File.Exists(Path.Combine(target, "a.bin")));
        Assert.True(File.Exists(Path.Combine(target, "sub", "b.bin")));
    }

    [Fact]
    public async Task A_hosted_handler_removes_its_files_and_reports_the_bytes()
    {
        using var fixture = new ScanFixture();
        var target = fixture.Dir("target");
        fixture.File("target/a.bin", 1000);
        fixture.File("target/sub/b.bin", 2000);

        // Next to the target, not inside it: the handler was told about one folder only.
        var bystander = fixture.File("bystander.bin", 500);

        using var registration = new Registration(target);
        var handler = registration.Handler(Path.GetPathRoot(target)!);

        var freed = await handler.CleanAsync();

        Assert.Equal(3000, freed);
        Assert.False(File.Exists(Path.Combine(target, "a.bin")));
        Assert.False(File.Exists(Path.Combine(target, "sub", "b.bin")));
        Assert.True(File.Exists(bystander));
    }

    [Fact]
    public async Task A_handler_that_is_not_registered_has_nothing_to_offer()
    {
        var handler = new DiskCleanupHandler(
            Registry.CurrentUser, @"Software\DiskSpace.Tests\missing", "Nothing here", @"C:\");

        Assert.Null(await handler.MeasureAsync());
        Assert.Equal(0, await handler.CleanAsync());
    }

    [Fact]
    public async Task A_cancelled_measurement_is_reported_as_cancelled()
    {
        using var fixture = new ScanFixture();
        var target = fixture.Dir("target");
        fixture.File("target/a.bin", 1000);

        using var registration = new Registration(target);
        var handler = registration.Handler(Path.GetPathRoot(target)!);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => handler.MeasureAsync(cancelled.Token));

        Assert.True(File.Exists(Path.Combine(target, "a.bin")));
    }

    // ---- The catalog built from the real registry ---------------------------------------------

    [Fact]
    public void Never_offers_the_Downloads_folder()
    {
        // Disk Cleanup lists it. This tool treats Downloads as the user's own files.
        var rules = new DiskCleanupProvider().GetRules().ToList();

        Assert.DoesNotContain(rules, r => r.Id.StartsWith("cleanmgr.downloadsfolder", StringComparison.Ordinal));

        var guard = new PathGuard();
        var downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        Assert.True(guard.TouchesUserData(downloads));
        Assert.DoesNotContain(rules, r => r.Root.Contains(downloads, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Every_handler_rule_is_owned_by_Windows_and_names_no_paths_of_its_own()
    {
        var rules = new DiskCleanupProvider().GetRules().ToList();

        // Disk Cleanup is part of every desktop edition of Windows, so an empty list here
        // means the registry was not read, not that there was nothing to find.
        Assert.NotEmpty(rules);

        Assert.All(rules, rule =>
        {
            Assert.NotNull(rule.Cleaner);
            Assert.Empty(rule.Targets);
            Assert.NotEqual(RiskLevel.ReportOnly, rule.Risk);
            Assert.False(string.IsNullOrWhiteSpace(rule.WhatBreaks));
        });

        Assert.Equal(rules.Count, rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Handlers_this_tool_does_not_recognize_are_never_rated_Safe()
    {
        // Anything outside the catalog gets Review, so the "All safe" preset cannot select a
        // cleanup this tool knows nothing about.
        var rules = new DiskCleanupProvider().GetRules();

        Assert.All(
            rules.Where(r => r.WhatBreaks.Contains("no knowledge of its own", StringComparison.Ordinal)),
            rule => Assert.Equal(RiskLevel.Review, rule.Risk));
    }

    // ---- Through the catalog and the executor --------------------------------------------------

    private sealed class FakeCleaner(long? size, Exception? failure = null) : ISystemCleaner
    {
        public int Cleaned { get; private set; }

        public string Description => "A stand-in for a Windows component.";

        public Task<long?> MeasureAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(size);

        public Task<long> CleanAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default)
        {
            Cleaned++;
            return failure is null ? Task.FromResult(size ?? 0) : Task.FromException<long>(failure);
        }
    }

    private sealed class FixedProvider(params CleanupRule[] rules) : IRuleProvider
    {
        public string Name => "Test";

        public IEnumerable<CleanupRule> GetRules() => rules;
    }

    private static CleanupRule OwnedRule(ISystemCleaner cleaner, RiskLevel risk = RiskLevel.Advanced) => new()
    {
        Id = "cleanmgr.test.c",
        Name = "Test cleanup",
        Category = "Windows and system",
        Risk = risk,
        Description = "Test",
        WhatBreaks = "Test",

        // Deliberately somewhere the path guard would refuse outright. A rule like this is
        // never deleted by path, so the guard is not what decides it.
        Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS"),
        Targets = [],
        Cleaner = cleaner,
    };

    [Fact]
    public async Task The_catalog_offers_what_a_cleaner_measures()
    {
        var rule = OwnedRule(new FakeCleaner(4096));

        var findings = await new RuleCatalog([new FixedProvider(rule)]).ResolveAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(4096, finding.Size);
        Assert.Equal(rule.Root, finding.Path);
        Assert.True(finding.IsActionable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public async Task The_catalog_drops_a_cleaner_with_nothing_to_remove(long? size)
    {
        var rule = OwnedRule(new FakeCleaner(size));

        Assert.Empty(await new RuleCatalog([new FixedProvider(rule)]).ResolveAsync());
    }

    [Fact]
    public async Task The_executor_hands_an_owned_item_to_its_cleaner_and_logs_it()
    {
        using var fixture = new ScanFixture();
        var cleaner = new FakeCleaner(4096);
        var rule = OwnedRule(cleaner);
        var logs = fixture.Dir("logs");

        var executor = new CleanupExecutor(auditLogDirectory: logs);
        var plan = executor.Plan(await new RuleCatalog([new FixedProvider(rule)]).ResolveAsync());

        Assert.Equal(Disposal.Windows, Assert.Single(plan.Items).Disposal);
        Assert.False(plan.ContainsQuarantine);
        Assert.True(plan.GoesBeyondCaches);

        var report = await executor.ExecuteAsync(plan);

        Assert.Equal(1, cleaner.Cleaned);
        Assert.Equal(1, report.SucceededCount);
        Assert.Equal(4096, report.BytesReclaimed);

        var entry = Assert.Single(AuditLog.Read(AuditLog.ListRuns(logs).First()));
        Assert.Equal("Windows", entry.Disposal);
        Assert.Equal("cleanmgr.test.c", entry.RuleId);
        Assert.True(entry.Succeeded);
    }

    [Fact]
    public async Task A_cleaner_that_fails_is_reported_and_does_not_stop_the_run()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("cache");
        fixture.File("cache/a.bin", 700);

        var failing = OwnedRule(new FakeCleaner(4096, new IOException("The service is not running.")));
        var cacheRule = new CleanupRule
        {
            Id = "pkg.test",
            Name = "Test cache",
            Category = "Package manager caches",
            Risk = RiskLevel.Safe,
            Description = "Test",
            WhatBreaks = "Nothing",
            Root = cache,
            Targets = [cache],
        };

        var executor = new CleanupExecutor(auditLogDirectory: fixture.Dir("logs"));
        var findings = await new RuleCatalog([new FixedProvider(failing, cacheRule)]).ResolveAsync();

        var report = await executor.ExecuteAsync(executor.Plan(findings));

        Assert.Equal(1, report.FailedCount);
        Assert.Contains("The service is not running.", report.Failures.Single().Error!, StringComparison.Ordinal);

        // The item after the failure still ran.
        Assert.Equal(700, report.BytesReclaimed);
        Assert.Empty(Directory.GetFiles(cache));
    }
}
