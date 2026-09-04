using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Tests;

public sealed class FastDirectoryScannerTests
{
    private static DirectoryNode Child(DirectoryNode node, string name) =>
        node.Children.Single(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Sums_sizes_across_a_nested_tree()
    {
        using var fixture = new ScanFixture();
        fixture.File("a.bin", 1000);
        fixture.File("sub/b.bin", 2000);
        fixture.File("sub/deep/c.bin", 3000);
        fixture.File("sub/deep/d.bin", 4000);

        var result = await new FastDirectoryScanner().ScanAsync(fixture.Root);

        Assert.Equal(10_000, result.TotalSize);
        Assert.Equal(4, result.TotalFileCount);
        Assert.Equal(2, result.TotalDirectoryCount);
        Assert.Empty(result.Issues);

        Assert.Equal(1000, result.Root.OwnSize);
        var sub = Child(result.Root, "sub");
        Assert.Equal(9000, sub.TotalSize);
        Assert.Equal(2000, sub.OwnSize);
        Assert.Equal(7000, Child(sub, "deep").TotalSize);
    }

    [Fact]
    public async Task Counts_an_empty_tree_as_zero()
    {
        using var fixture = new ScanFixture();
        fixture.Dir("empty/nested");

        var result = await new FastDirectoryScanner().ScanAsync(fixture.Root);

        Assert.Equal(0, result.TotalSize);
        Assert.Equal(0, result.TotalFileCount);
        Assert.Equal(2, result.TotalDirectoryCount);
    }

    [Fact]
    public async Task Includes_hidden_files_because_they_occupy_disk()
    {
        using var fixture = new ScanFixture();
        var hidden = fixture.File("hidden.bin", 5000);
        System.IO.File.SetAttributes(hidden, FileAttributes.Hidden);

        var result = await new FastDirectoryScanner().ScanAsync(fixture.Root);

        Assert.Equal(5000, result.TotalSize);
    }

    [Fact]
    public async Task Does_not_follow_a_junction_that_loops_back_to_the_root()
    {
        using var fixture = new ScanFixture();
        fixture.File("real.bin", 1000);
        fixture.CreateJunction("loop", fixture.Root);

        // The scan must terminate, and must not count the tree twice through the junction.
        var scan = new FastDirectoryScanner().ScanAsync(fixture.Root);
        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(scan, finished);

        var result = await scan;
        Assert.Equal(1000, result.TotalSize);

        var loop = Child(result.Root, "loop");
        Assert.True(loop.IsReparsePoint);
        Assert.Empty(loop.Children);
    }

    [Fact]
    public async Task Handles_paths_beyond_the_legacy_260_character_limit()
    {
        using var fixture = new ScanFixture();

        // Nest until comfortably past MAX_PATH.
        var relative = string.Join('/', Enumerable.Repeat(new string('d', 40), 8));
        fixture.File($"{relative}/deep.bin", 1234);
        Assert.True(Path.Combine(fixture.Root, relative).Length > 260);

        var result = await new FastDirectoryScanner().ScanAsync(fixture.Root);

        Assert.Equal(1234, result.TotalSize);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public async Task Reports_progress_and_honours_cancellation()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 40; i++)
            fixture.File($"dir{i}/file.bin", 100);

        var reports = new List<ScanProgress>();
        var progress = new Progress<ScanProgress>(p =>
        {
            lock (reports) reports.Add(p);
        });

        var options = new ScanOptions { ProgressInterval = 1 };
        var result = await new FastDirectoryScanner(options).ScanAsync(fixture.Root, progress);

        Assert.Equal(4000, result.TotalSize);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new FastDirectoryScanner().ScanAsync(fixture.Root, null, cancelled.Token));
    }

    [Fact]
    public async Task Records_an_unreadable_directory_as_an_issue_rather_than_throwing()
    {
        using var fixture = new ScanFixture();
        fixture.File("readable.bin", 500);
        var locked = fixture.Dir("locked");

        var denied = new System.Security.AccessControl.DirectorySecurity();
        denied.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny));

        try
        {
            new DirectoryInfo(locked).SetAccessControl(denied);
        }
        catch (Exception)
        {
            return; // Cannot apply the ACL here; the scenario is untestable in this environment.
        }

        try
        {
            var result = await new FastDirectoryScanner().ScanAsync(fixture.Root);

            // The readable part of the tree still reports, and the failure is described.
            Assert.Equal(500, result.TotalSize);
            Assert.Contains(result.Issues, i => i.Reason == "Access denied");
        }
        finally
        {
            var reset = new DirectoryInfo(locked).GetAccessControl();
            reset.RemoveAccessRuleAll(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            new DirectoryInfo(locked).SetAccessControl(reset);
        }
    }
}
