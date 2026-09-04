using System.Diagnostics;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Tests;

public sealed class ProgressiveScannerTests
{
    /// <summary>
    /// The scanner publishes a tree before it has finished measuring it, so a test cannot simply
    /// await one call. Everything here polls with a deadline instead of sleeping for a guess.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = Stopwatch.StartNew();

        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(30), $"Timed out waiting for {what}.");
            await Task.Delay(1);
        }
    }

    private static DirectoryNode Child(DirectoryNode node, string name) =>
        node.Children.Single(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public async Task Produces_the_same_totals_as_the_blocking_scanner()
    {
        using var fixture = new ScanFixture();
        fixture.File("a.bin", 1000);
        fixture.File("sub/b.bin", 2000);
        fixture.File("sub/deep/c.bin", 3000);
        fixture.File("sub/deep/d.bin", 4000);
        fixture.File("sub/deep/deeper/still/e.bin", 5000);
        fixture.File("other/f.bin", 6000);
        fixture.Dir("empty");

        var blocking = await new FastDirectoryScanner().ScanAsync(fixture.Root);

        await using var scanner = new ProgressiveScanner();
        await scanner.StartAsync(fixture.Root);
        var progressive = await scanner.RunToCompletionAsync();

        Assert.True(progressive.IsComplete);

        // Compared node by node rather than at the root, because a roll-up and an incremental
        // accumulation can agree on the total while disagreeing about where the bytes are.
        var expected = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase);
        Flatten(blocking.Root, expected);

        var actual = new Dictionary<string, DirectoryNode>(StringComparer.OrdinalIgnoreCase);
        Flatten(progressive.Root, actual);

        Assert.Equal(expected.Keys.OrderBy(k => k), actual.Keys.OrderBy(k => k));

        foreach (var (path, node) in expected)
        {
            var other = actual[path];
            Assert.Equal(node.OwnSize, other.OwnSize);
            Assert.Equal(node.OwnFileCount, other.OwnFileCount);
            Assert.Equal(node.TotalSize, other.TotalSize);
            Assert.Equal(node.TotalFileCount, other.TotalFileCount);
            Assert.Equal(node.TotalDirectoryCount, other.TotalDirectoryCount);
            Assert.Equal(node.LastWriteUtc, other.LastWriteUtc);
            Assert.True(other.IsComplete);
            Assert.True(other.IsEnumerated);
        }
    }

    private static void Flatten(DirectoryNode root, Dictionary<string, DirectoryNode> into)
    {
        var stack = new Stack<(DirectoryNode Node, string Relative)>();
        stack.Push((root, string.Empty));

        while (stack.Count > 0)
        {
            var (node, relative) = stack.Pop();
            into[relative] = node;

            foreach (var child in node.Children)
                stack.Push((child, relative.Length == 0 ? child.Name : $"{relative}/{child.Name}"));
        }
    }

    [Fact]
    public async Task Shallow_pass_returns_before_the_deep_tree_is_measured()
    {
        using var fixture = new ScanFixture();

        // Wide enough at the bottom that the deep walk cannot plausibly beat the assertion.
        for (var i = 0; i < 400; i++)
            fixture.File($"top/second/third/branch{i}/leaf.bin", 100);

        var options = new ScanOptions { ShallowDepth = 1, MaxDegreeOfParallelism = 1 };
        await using var scanner = new ProgressiveScanner(options);
        var root = await scanner.StartAsync(fixture.Root);

        Assert.True(root.IsEnumerated);
        Assert.True(Child(root, "top").IsEnumerated);
        Assert.False(root.IsComplete);

        var result = await scanner.RunToCompletionAsync();
        Assert.Equal(40_000, result.TotalSize);
        Assert.True(root.IsComplete);
    }

    [Fact]
    public async Task Does_not_follow_a_junction_that_loops_back_to_the_root()
    {
        using var fixture = new ScanFixture();
        fixture.File("real.bin", 1000);
        fixture.CreateJunction("loop", fixture.Root);

        await using var scanner = new ProgressiveScanner();
        await scanner.StartAsync(fixture.Root);

        var scan = scanner.RunToCompletionAsync();
        var finished = await Task.WhenAny(scan, Task.Delay(TimeSpan.FromSeconds(30)));
        Assert.Same(scan, finished);

        var result = await scan;
        Assert.Equal(1000, result.TotalSize);

        var loop = Child(result.Root, "loop");
        Assert.True(loop.IsReparsePoint);
        Assert.Empty(loop.Children);

        // A skipped junction must still settle, or its parent waits forever for a listing that
        // is never going to happen.
        Assert.True(loop.IsComplete);
    }

    [Fact]
    public async Task Totals_never_shrink_while_a_scan_runs()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 300; i++)
            fixture.File($"branch{i}/nested/leaf.bin", 512);

        await using var scanner = new ProgressiveScanner();
        var root = await scanner.StartAsync(fixture.Root);

        var highest = 0L;
        var sampler = Task.Run(async () =>
        {
            while (!root.IsComplete)
            {
                var seen = root.TotalSize;
                Assert.True(seen >= highest, "A total went backwards during a fresh scan.");
                highest = seen;
                await Task.Delay(1);
            }
        });

        var result = await scanner.RunToCompletionAsync();
        await sampler;

        Assert.Equal(300 * 512, result.TotalSize);
    }

    [Fact]
    public async Task Prioritizing_a_subtree_finishes_it_before_the_rest()
    {
        using var fixture = new ScanFixture();

        // One worker and no shallow pass, so the queue order is the only thing under test.
        for (var i = 0; i < 3000; i++)
            fixture.Dir($"slow/branch{i}");

        fixture.File("target/inner/leaf.bin", 100);

        var options = new ScanOptions { ShallowDepth = 0, MaxDegreeOfParallelism = 1 };
        await using var scanner = new ProgressiveScanner(options);
        var root = await scanner.StartAsync(fixture.Root);

        var target = Child(root, "target");
        scanner.Prioritize(target);

        await WaitUntil(() => target.IsComplete, "the prioritized subtree to finish");
        Assert.False(root.IsComplete);

        var result = await scanner.RunToCompletionAsync();
        Assert.Equal(100, result.TotalSize);
    }

    [Fact]
    public async Task Cancelling_keeps_the_partial_tree_and_reports_it_as_incomplete()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 2000; i++)
            fixture.File($"branch{i}/leaf.bin", 64);

        var options = new ScanOptions { ShallowDepth = 0, MaxDegreeOfParallelism = 1 };
        await using var scanner = new ProgressiveScanner(options);
        var root = await scanner.StartAsync(fixture.Root);

        scanner.Cancel();
        var result = await scanner.RunToCompletionAsync();

        // The point of the progressive scanner: a cancelled walk still hands back what it
        // measured rather than throwing the window back to an empty state.
        Assert.False(result.IsComplete);
        Assert.False(root.IsComplete);
        Assert.Same(root, result.Root);
        Assert.True(result.TotalSize >= 0);
    }

    [Fact]
    public async Task A_token_that_is_already_cancelled_finishes_instead_of_waiting()
    {
        using var fixture = new ScanFixture();
        for (var i = 0; i < 50; i++)
            fixture.File($"branch{i}/leaf.bin", 64);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await using var scanner = new ProgressiveScanner();

        // Both of these wait on the workers draining the queue, so a scan whose workers were
        // never scheduled would hang here rather than fail.
        var start = scanner.StartAsync(fixture.Root, cancelled.Token);
        Assert.Same(start, await Task.WhenAny(start, Task.Delay(TimeSpan.FromSeconds(15))));

        var run = scanner.RunToCompletionAsync();
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15))));

        Assert.False((await run).IsComplete);
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
            await using var scanner = new ProgressiveScanner();
            await scanner.StartAsync(fixture.Root);
            var result = await scanner.RunToCompletionAsync();

            Assert.Equal(500, result.TotalSize);
            Assert.Contains(result.Issues, i => i.Reason == "Access denied");

            // An unreadable directory is still a settled one: it is as measured as it can be.
            Assert.True(result.IsComplete);
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
