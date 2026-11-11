using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;
using DiskSpace.Core.Quarantine;
using DiskSpace.Core.Rules;
using DiskSpace.Core.Safety;

namespace DiskSpace.Core.Tests;

/// <summary>
/// Orphaned Windows Installer packages. A wrong answer here breaks the repair and uninstall of
/// something still installed, so most of these tests are about when nothing is offered.
/// </summary>
public sealed class InstallerCacheTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static string Aged(string path, int days = 60)
    {
        File.SetCreationTimeUtc(path, Now.AddDays(-days));
        File.SetLastWriteTimeUtc(path, Now.AddDays(-days));
        return path;
    }

    private static HashSet<string> Referenced(params string[] paths) =>
        new(paths, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Offers_a_package_nothing_refers_to_and_keeps_one_that_is_registered()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("Installer");
        var registered = Aged(fixture.File("Installer/1a2b3c.msi", 1000));
        var orphanPatch = Aged(fixture.File("Installer/4d5e6f.msp", 2000));
        var orphanPackage = Aged(fixture.File("Installer/778899.msi", 3000));

        var orphans = InstallerCacheProvider.FindOrphans(cache, Referenced(registered), Now);

        Assert.Equal([orphanPatch, orphanPackage], orphans.Order());
    }

    [Fact]
    public void Matches_registered_packages_without_regard_to_case()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("Installer");
        var registered = Aged(fixture.File("Installer/ABCDEF.msi", 1000));

        Assert.Empty(InstallerCacheProvider.FindOrphans(
            cache, Referenced(registered.ToLowerInvariant()), Now));
    }

    [Fact]
    public void Leaves_a_package_that_arrived_recently()
    {
        // The file lands in the cache before the installation that owns it is recorded.
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("Installer");
        fixture.File("Installer/fresh.msi", 1000);
        Aged(fixture.File("Installer/yesterday.msp", 1000), days: 1);

        Assert.Empty(InstallerCacheProvider.FindOrphans(cache, Referenced(), Now));
    }

    [Fact]
    public void Ignores_everything_that_is_not_a_package_and_everything_in_subfolders()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("Installer");
        Aged(fixture.File("Installer/SourceHash{GUID}", 100));
        Aged(fixture.File("Installer/icon.ico", 100));
        Aged(fixture.File("Installer/$PatchCache$/Managed/base.msi", 5000));
        Aged(fixture.File("Installer/{GUID}/setup.msi", 5000));

        Assert.Empty(InstallerCacheProvider.FindOrphans(cache, Referenced(), Now));
    }

    [Fact]
    public void Reads_what_Windows_Installer_has_registered()
    {
        // The current user and the machine, which needs no elevation. Every machine that can
        // build this has installed something with Windows Installer.
        var packages = WindowsInstaller.ReferencedPackages(userSid: null);

        Assert.NotNull(packages);
        Assert.NotEmpty(packages);
        Assert.All(packages, package => Assert.True(Path.IsPathFullyQualified(package), package));
    }

    [Fact]
    public void Offers_nothing_when_it_cannot_ask_about_every_user()
    {
        // Without administrator rights the question about all users is refused, and a partial
        // answer would list other users' products as orphans.
        if (Environment.IsPrivilegedProcess)
            return;

        Assert.Null(WindowsInstaller.ReferencedPackages());
        Assert.Empty(new InstallerCacheProvider().GetRules());
    }

    [Fact]
    public void The_guard_allows_a_cached_package_but_not_the_cache_itself()
    {
        var guard = new PathGuard();
        var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer");

        Assert.True(guard.Check(Path.Combine(cache, "1a2b3c.msp"), cache).Allowed);
        Assert.False(guard.Check(cache, cache).Allowed);
    }

    [Theory]
    [InlineData(QuarantineMode.ArchiveToOtherVolume)]
    [InlineData(QuarantineMode.MoveOnSameVolume)]
    public async Task An_orphaned_package_is_quarantined_and_comes_back_byte_for_byte(QuarantineMode mode)
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("Installer");
        var package = Path.Combine(cache, "4d5e6f.msp");
        var content = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        File.WriteAllBytes(package, content);
        var neighbor = fixture.File("Installer/registered.msi", 700);

        var rule = new CleanupRule
        {
            Id = "msi.orphaned-packages",
            Name = "Orphaned installer package",
            Category = "Orphaned installer packages",
            Risk = RiskLevel.Review,
            Description = "Test",
            WhatBreaks = "Test",
            Root = cache,
            Targets = [package],
            RemoveTargetDirectory = true,
            Quarantine = true,
        };

        var store = new QuarantineStore(new QuarantineOptions
        {
            Location = fixture.Dir("quarantine"),
            Mode = mode,
            SearchAllLocations = false,
        });

        var executor = new CleanupExecutor(store, fixture.Dir("logs"));
        var findings = await new RuleCatalog([new Provider(rule)]).ResolveAsync();
        var plan = executor.Plan(findings);

        Assert.Equal(Disposal.Quarantine, Assert.Single(plan.Items).Disposal);

        var report = await executor.ExecuteAsync(plan);

        Assert.Equal(1, report.SucceededCount);
        Assert.False(File.Exists(package));
        Assert.True(File.Exists(neighbor));

        var manifest = Assert.Single(store.List());
        Assert.True(manifest.IsFile);
        Assert.Equal(5000, manifest.OriginalSize);

        await store.RestoreAsync(manifest);

        Assert.Equal(content, File.ReadAllBytes(package));
        Assert.Empty(store.List());
    }

    [Fact]
    public async Task A_quarantined_file_can_be_purged()
    {
        using var fixture = new ScanFixture();
        var package = fixture.File("Installer/gone.msp", 900);

        var store = new QuarantineStore(new QuarantineOptions
        {
            Location = fixture.Dir("quarantine"),
            Mode = QuarantineMode.MoveOnSameVolume,
            SearchAllLocations = false,
        });

        var manifest = await store.QuarantineAsync(package, new CleanupRule
        {
            Id = "msi.orphaned-packages",
            Name = "Orphaned installer package",
            Category = "Orphaned installer packages",
            Risk = RiskLevel.Review,
            Description = "Test",
            WhatBreaks = "Test",
            Root = Path.GetDirectoryName(package)!,
            Targets = [package],
        });

        QuarantineStore.Purge(manifest);

        Assert.False(File.Exists(manifest.MovedToPath));
        Assert.Empty(store.List());
    }

    private sealed class Provider(params CleanupRule[] rules) : IRuleProvider
    {
        public string Name => "Test";

        public IEnumerable<CleanupRule> GetRules() => rules;
    }
}
