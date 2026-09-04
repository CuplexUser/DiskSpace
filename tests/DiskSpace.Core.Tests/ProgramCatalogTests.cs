using DiskSpace.Core.Model;
using DiskSpace.Core.Programs;

namespace DiskSpace.Core.Tests;

public sealed class ProgramCatalogTests
{
    private sealed class FakeProvider(params InstalledProgram[] programs) : IProgramProvider
    {
        public string Name => "Fake";

        public IEnumerable<InstalledProgram> GetPrograms() => programs;
    }

    private sealed class BrokenProvider : IProgramProvider
    {
        public string Name => "Broken";

        public IEnumerable<InstalledProgram> GetPrograms() =>
            throw new InvalidOperationException("Registry hive unreadable.");
    }

    private static InstalledProgram Program(string id, params ProgramLocation[] locations) => new()
    {
        Id = id,
        Name = id,
        Source = ProgramSource.Registry,
        Risk = RiskLevel.Review,
        Locations = locations,
    };

    private static ProgramFootprint Find(IReadOnlyList<ProgramFootprint> footprints, string id) =>
        footprints.Single(f => f.Program.Id == id);

    [Fact]
    public async Task Bytes_shared_by_two_entries_are_counted_once()
    {
        using var fixture = new ScanFixture();
        fixture.File("vendor/loose.bin", 1000);
        fixture.File("vendor/suite/inner.bin", 2000);

        // The ordinary case, not an exotic one: a suite and one of its components both name a
        // directory, and one contains the other.
        var catalog = new ProgramCatalog(
        [
            new FakeProvider(Program(
                "vendor",
                new ProgramLocation(Path.Combine(fixture.Root, "vendor"), LocationKind.Install))),
            new FakeProvider(Program(
                "suite",
                new ProgramLocation(
                    Path.Combine(fixture.Root, "vendor", "suite"), LocationKind.Install))),
        ]);

        var footprints = await catalog.MeasureAsync();

        Assert.Equal(2000, Find(footprints, "suite").TotalSize);

        // The outer entry keeps only what the inner one did not claim, so the two add up to the
        // 3000 bytes actually on disk rather than to 5000.
        Assert.Equal(1000, Find(footprints, "vendor").TotalSize);
        Assert.Equal(3000, footprints.Sum(f => f.TotalSize));
    }

    [Fact]
    public async Task The_same_path_claimed_twice_is_measured_for_one_of_them()
    {
        using var fixture = new ScanFixture();
        fixture.File("app/data.bin", 4000);

        var location = new ProgramLocation(Path.Combine(fixture.Root, "app"), LocationKind.Install);

        // A Store app usually also has a registry entry pointing at the same folder.
        var catalog = new ProgramCatalog(
        [
            new FakeProvider(Program("first", location), Program("second", location)),
        ]);

        var footprints = await catalog.MeasureAsync();

        Assert.Equal(4000, footprints.Sum(f => f.TotalSize));
        Assert.Equal(1, footprints.Count(f => f.TotalSize > 0));
    }

    [Fact]
    public async Task A_shared_root_is_never_credited_to_one_program()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        var catalog = new ProgramCatalog(
        [
            new FakeProvider(Program(
                "greedy", new ProgramLocation(programFiles, LocationKind.Install))),
        ]);

        var footprints = await catalog.MeasureAsync();

        // An installer that recorded InstallLocation=C:\Program Files would otherwise be handed
        // every program on the machine.
        Assert.Empty(Find(footprints, "greedy").Parts);
    }

    [Fact]
    public async Task A_provider_that_throws_costs_only_its_own_entries()
    {
        using var fixture = new ScanFixture();
        fixture.File("app/data.bin", 500);

        var catalog = new ProgramCatalog(
        [
            new BrokenProvider(),
            new FakeProvider(Program(
                "survivor",
                new ProgramLocation(Path.Combine(fixture.Root, "app"), LocationKind.Install))),
        ]);

        var footprints = await catalog.MeasureAsync();

        Assert.Equal(500, Find(footprints, "survivor").TotalSize);
    }

    [Fact]
    public async Task A_location_that_is_gone_reports_why_rather_than_zero()
    {
        using var fixture = new ScanFixture();

        var catalog = new ProgramCatalog(
        [
            new FakeProvider(Program(
                "stale",
                new ProgramLocation(
                    Path.Combine(fixture.Root, "removed", "app"), LocationKind.Install))),
        ]);

        var footprints = await catalog.MeasureAsync();
        var part = Find(footprints, "stale").Parts.Single();

        Assert.NotNull(part.Error);
        Assert.Equal(0, part.Size);
    }

    [Fact]
    public async Task Install_and_data_are_reported_separately()
    {
        using var fixture = new ScanFixture();
        fixture.File("app/program.bin", 1000);
        fixture.File("appdata/profile.bin", 7000);

        var catalog = new ProgramCatalog(
        [
            new FakeProvider(Program(
                "app",
                new ProgramLocation(Path.Combine(fixture.Root, "app"), LocationKind.Install),
                new ProgramLocation(Path.Combine(fixture.Root, "appdata"), LocationKind.Data))),
        ]);

        var footprint = Find(await catalog.MeasureAsync(), "app");

        // Worth keeping apart: a program whose saved state dwarfs its install is a different
        // decision from one that does not.
        Assert.Equal(1000, footprint.InstallSize);
        Assert.Equal(7000, footprint.DataSize);
        Assert.Equal(8000, footprint.TotalSize);
        Assert.False(footprint.SizeIsEstimated);
    }

    [Fact]
    public async Task The_installers_own_claim_is_used_only_when_nothing_can_be_measured()
    {
        using var fixture = new ScanFixture();

        var program = Program("claimed", new ProgramLocation(
            Path.Combine(fixture.Root, "missing", "app"), LocationKind.Install)) with
        {
            RegistryEstimatedSize = 12345,
        };

        var footprint = Find(await new ProgramCatalog([new FakeProvider(program)]).MeasureAsync(),
            "claimed");

        Assert.True(footprint.SizeIsEstimated);
        Assert.Equal(12345, footprint.TotalSize);
    }

    [Fact]
    public void The_default_providers_produce_a_readable_inventory_of_this_machine()
    {
        var programs = new ProgramCatalog().GetPrograms();

        // Any Windows machine has installed software and the Windows components themselves, so
        // an empty result here means a provider is silently failing rather than finding nothing.
        Assert.NotEmpty(programs);
        Assert.Contains(programs, p => p.Source == ProgramSource.WindowsComponent);
        Assert.All(programs, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));
        Assert.All(programs, p => Assert.False(string.IsNullOrWhiteSpace(p.Id)));
    }
}
