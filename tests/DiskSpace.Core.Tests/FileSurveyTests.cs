using System.Diagnostics;
using DiskSpace.Core.Model;
using DiskSpace.Core.Rules;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Tests;

public sealed class FileSurveyTests
{
    private const int Megabyte = 1024 * 1024;

    /// <summary>A file of <paramref name="bytes"/> length whose contents depend on <paramref name="seed"/>.</summary>
    private static string Write(ScanFixture fixture, string relative, int bytes, int seed, int? differAt = null)
    {
        var path = Path.GetFullPath(Path.Combine(fixture.Root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var content = new byte[bytes];
        new Random(seed).NextBytes(content);

        if (differAt is { } offset)
            content[offset] ^= 0xFF;

        File.WriteAllBytes(path, content);
        return path;
    }

    private static void HardLink(string link, string existing)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /H \"{link}\" \"{existing}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        process.WaitForExit(10_000);

        if (process.ExitCode != 0 || !File.Exists(link))
            throw new InvalidOperationException($"mklink /H failed for {link}");
    }

    // ---- Large files ---------------------------------------------------------------------------

    [Fact]
    public async Task Lists_files_at_or_above_the_minimum_largest_first()
    {
        using var fixture = new ScanFixture();
        var big = fixture.File("a/big.bin", 5000);
        var medium = fixture.File("b/c/medium.bin", 3000);
        fixture.File("small.bin", 100);

        var files = await FileSurvey.FindLargeFilesAsync(fixture.Root, minimumSize: 3000);

        Assert.Equal([big, medium], files.Select(f => f.Path));
        Assert.Equal(5000, files[0].Size);
    }

    [Fact]
    public async Task Does_not_follow_a_junction()
    {
        using var fixture = new ScanFixture();
        var scanned = fixture.Dir("scanned");
        var outside = fixture.Dir("outside");
        fixture.File("outside/elsewhere.bin", 9000);
        fixture.CreateJunction("scanned/link", outside);

        Assert.Empty(await FileSurvey.FindLargeFilesAsync(scanned, minimumSize: 1));
    }

    [Fact]
    public async Task A_cancelled_survey_stops()
    {
        using var fixture = new ScanFixture();
        fixture.File("a.bin", 5000);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileSurvey.FindLargeFilesAsync(fixture.Root, 1, null, cancelled.Token));
    }

    // ---- Duplicates ----------------------------------------------------------------------------

    [Fact]
    public async Task Groups_files_with_identical_contents()
    {
        using var fixture = new ScanFixture();
        var first = Write(fixture, "photos/original.bin", 3000, seed: 1);
        var second = Write(fixture, "backup/copy.bin", 3000, seed: 1);
        var third = Write(fixture, "backup/deeper/another.bin", 3000, seed: 1);
        Write(fixture, "unrelated.bin", 3000, seed: 2);
        Write(fixture, "other-size.bin", 4000, seed: 1);

        var group = Assert.Single(await FileSurvey.FindDuplicatesAsync(fixture.Root, minimumSize: 1));

        Assert.Equal(3000, group.Size);
        Assert.Equal(6000, group.Wasted);
        Assert.Equal(
            new[] { first, second, third }.Order(StringComparer.OrdinalIgnoreCase),
            group.Files.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Files_that_differ_only_in_the_middle_are_not_duplicates()
    {
        // Larger than both samples put together, with the one differing byte between them.
        // Only reading the whole file can tell these apart.
        using var fixture = new ScanFixture();
        Write(fixture, "one.bin", Megabyte, seed: 7);
        Write(fixture, "two.bin", Megabyte, seed: 7, differAt: Megabyte / 2);

        Assert.Empty(await FileSurvey.FindDuplicatesAsync(fixture.Root, minimumSize: 1));
    }

    [Fact]
    public async Task Large_identical_files_are_found_when_read_in_full()
    {
        using var fixture = new ScanFixture();
        Write(fixture, "one.bin", 3 * Megabyte, seed: 9);
        Write(fixture, "two.bin", 3 * Megabyte, seed: 9);

        var group = Assert.Single(await FileSurvey.FindDuplicatesAsync(fixture.Root, minimumSize: Megabyte));

        Assert.Equal(2, group.Files.Count);
        Assert.Equal(3L * Megabyte, group.Wasted);
    }

    [Fact]
    public async Task A_hard_link_is_one_file_not_a_copy()
    {
        using var fixture = new ScanFixture();
        var original = Write(fixture, "data.bin", 3000, seed: 3);
        HardLink(Path.Combine(fixture.Root, "second-name.bin"), original);

        Assert.Empty(await FileSurvey.FindDuplicatesAsync(fixture.Root, minimumSize: 1));
    }

    [Fact]
    public async Task A_hard_link_does_not_hide_a_real_copy()
    {
        using var fixture = new ScanFixture();
        var original = Write(fixture, "data.bin", 3000, seed: 3);
        HardLink(Path.Combine(fixture.Root, "second-name.bin"), original);
        Write(fixture, "real-copy.bin", 3000, seed: 3);

        var group = Assert.Single(await FileSurvey.FindDuplicatesAsync(fixture.Root, minimumSize: 1));

        // Two files on disk, three names: one copy's worth is wasted, not two.
        Assert.Equal(2, group.Files.Count);
        Assert.Equal(3000, group.Wasted);
    }

    [Fact]
    public async Task Files_below_the_minimum_are_not_compared()
    {
        using var fixture = new ScanFixture();
        Write(fixture, "a.bin", 500, seed: 4);
        Write(fixture, "b.bin", 500, seed: 4);

        Assert.Empty(await FileSurvey.FindDuplicatesAsync(fixture.Root, minimumSize: 1000));
    }

    [Fact]
    public async Task The_oldest_copy_is_listed_first()
    {
        using var fixture = new ScanFixture();
        var newer = Write(fixture, "a-newer.bin", 3000, seed: 5);
        var older = Write(fixture, "z-older.bin", 3000, seed: 5);
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddYears(-2));

        var group = Assert.Single(await FileSurvey.FindDuplicatesAsync(fixture.Root, minimumSize: 1));

        Assert.Equal([older, newer], group.Files.Select(f => f.Path));
    }

    // ---- The actions offered beside report-only findings --------------------------------------

    [Fact]
    public void Reads_the_storage_figure_and_the_restore_points_Windows_reports()
    {
        var snapshot = RestorePointStorage.Parse("used=5368709120\r\npoints=14,12,13\r\n");

        Assert.NotNull(snapshot);
        Assert.Equal(5368709120, snapshot.Value.UsedBytes);
        Assert.Equal([12, 13, 14], snapshot.Value.RestorePoints);
    }

    [Theory]
    [InlineData("")]
    [InlineData("points=1,2")]
    [InlineData("used=not-a-number")]
    public void Without_a_storage_figure_there_is_nothing_to_report(string output)
    {
        // What an unelevated run produces: the query is refused and prints nothing usable.
        Assert.Null(RestorePointStorage.Parse(output));
    }

    [Fact]
    public void A_storage_figure_with_no_restore_points_is_still_a_reading()
    {
        var snapshot = RestorePointStorage.Parse("used=1024\npoints=\n");

        Assert.NotNull(snapshot);
        Assert.Empty(snapshot.Value.RestorePoints);
    }

    [Fact]
    public void Report_only_items_offer_an_action_without_becoming_removable()
    {
        var rules = new LargeItemProvider().GetRules().ToList();

        var hibernation = Assert.Single(rules, r => r.Id == "large.hiberfil");
        var restorePoints = Assert.Single(rules, r => r.Id == "large.restore-points");

        Assert.All([hibernation, restorePoints], rule =>
        {
            Assert.Equal(RiskLevel.ReportOnly, rule.Risk);
            Assert.NotNull(rule.Action);
            Assert.False(string.IsNullOrWhiteSpace(rule.Action!.Consequence));
        });

        // The page and swap files have nothing a person can safely do from here.
        Assert.Null(Assert.Single(rules, r => r.Id == "large.pagefile").Action);
    }
}
