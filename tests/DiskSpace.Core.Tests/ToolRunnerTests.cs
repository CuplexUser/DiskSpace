using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;
using DiskSpace.Core.Rules;

namespace DiskSpace.Core.Tests;

/// <summary>
/// Running a tool's own purge command. A stand-in tool, a .cmd script in the fixture, plays
/// the part of npm or pip: like them it is a script, not an executable, and like them it is
/// told which folder to work on.
/// </summary>
public sealed class ToolRunnerTests
{
    private static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void Reports_the_exit_code_of_the_tool()
    {
        var result = ToolRunner.Run(Cmd, "/c exit 3");

        Assert.Equal(ToolOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void Passes_the_environment_it_was_given()
    {
        var environment = new Dictionary<string, string> { ["DISKSPACE_TEST_VALUE"] = "expected" };

        var result = ToolRunner.Run(
            Cmd, "/c if \"%DISKSPACE_TEST_VALUE%\"==\"expected\" (exit 0) else (exit 9)", environment);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Says_so_when_the_tool_is_not_installed()
    {
        var result = ToolRunner.Run("diskspace-no-such-tool", "--version");

        Assert.Equal(ToolOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public void Runs_a_script_shim_through_the_command_interpreter()
    {
        // npm, pnpm and yarn are all .cmd files on Windows, which cannot be started directly.
        using var fixture = new ScanFixture();
        var shim = Path.Combine(fixture.Root, "tool with space.cmd");
        File.WriteAllText(shim, "@echo off\r\nif \"%~1\"==\"go\" (exit /b 0)\r\nexit /b 4\r\n");

        Assert.True(ToolRunner.Run(shim, "go").Succeeded);
        Assert.Equal(4, ToolRunner.Run(shim, "stop").ExitCode);
    }

    [Fact]
    public void Stops_a_tool_that_overruns_its_time_limit()
    {
        var result = ToolRunner.Run(
            Cmd, "/c ping -n 30 127.0.0.1 >nul", timeout: TimeSpan.FromMilliseconds(400));

        Assert.Equal(ToolOutcome.TimedOut, result.Outcome);
    }

    [Fact]
    public void A_purge_command_is_pinned_to_the_folder_the_rule_lists()
    {
        var byArgument = new PurgeCommand("npm", "cache clean --force --cache {path}");
        var byVariable = new PurgeCommand("dotnet", "nuget locals global-packages --clear", "NUGET_PACKAGES");

        Assert.Equal(
            "cache clean --force --cache \"C:\\Some Folder\\npm-cache\"",
            byArgument.ArgumentsFor(@"C:\Some Folder\npm-cache"));
        Assert.Null(byArgument.EnvironmentFor(@"C:\x"));

        Assert.Equal(@"C:\x", byVariable.EnvironmentFor(@"C:\x")!["NUGET_PACKAGES"]);
        Assert.Equal("npm cache clean --force --cache <this folder>", byArgument.ToString());
    }

    [Fact]
    public void Every_shipped_purge_command_names_its_folder()
    {
        // A command with neither clears wherever the tool is configured to look, which need
        // not be the folder the rule showed.
        var rules = new PackageManagerCacheProvider().GetRules().Where(r => r.Purge is not null).ToList();

        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
            Assert.True(
                rule.Purge!.PathVariable is not null
                || rule.Purge.Arguments.Contains("{path}", StringComparison.Ordinal),
                $"{rule.Id} runs '{rule.Purge}' without saying where"));
    }

    private static CleanupRule CacheRule(string cache, PurgeCommand purge) => new()
    {
        Id = "pkg.test",
        Name = "Test cache",
        Category = "Package manager caches",
        Risk = RiskLevel.Safe,
        Description = "Test",
        WhatBreaks = "Nothing",
        Root = cache,
        Targets = [cache],
        Purge = purge,
    };

    private static CleanupFinding Finding(CleanupRule rule, long size) => new()
    {
        Rule = rule,
        Path = rule.Root,
        Size = size,
        FileCount = 2,
        LastWriteUtc = DateTime.UtcNow,
    };

    [Fact]
    public async Task The_executor_runs_the_tool_then_removes_whatever_it_left()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("cache dir");
        fixture.File("cache dir/index.bin", 1000);
        fixture.File("cache dir/kept-by-tool/blob.bin", 2000);

        // Clears its index and records that it was called, leaving the rest for the fallback.
        var marker = Path.Combine(fixture.Root, "called.txt");
        var tool = Path.Combine(fixture.Root, "faketool.cmd");
        File.WriteAllText(
            tool,
            "@echo off\r\n"
            + "del /q \"%~1\\index.bin\"\r\n"
            + $"echo %~1> \"{marker}\"\r\n");

        var rule = CacheRule(cache, new PurgeCommand(tool, "{path}"));
        var executor = new CleanupExecutor(auditLogDirectory: fixture.Dir("logs"));

        var report = await executor.ExecuteAsync(executor.Plan([Finding(rule, 3000)]));

        // The tool was handed the listed folder, and nothing else.
        Assert.Equal(cache, File.ReadAllText(marker).Trim());

        // Both halves are counted: what the tool removed and what was deleted after it.
        Assert.Equal(3000, report.BytesReclaimed);
        Assert.True(Directory.Exists(cache));
        Assert.Empty(Directory.GetFiles(cache, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_missing_tool_falls_back_to_deleting_the_folder()
    {
        using var fixture = new ScanFixture();
        var cache = fixture.Dir("cache");
        fixture.File("cache/a.bin", 1500);

        var rule = CacheRule(cache, new PurgeCommand("diskspace-no-such-tool", "cache clean {path}"));
        var executor = new CleanupExecutor(auditLogDirectory: fixture.Dir("logs"));

        var report = await executor.ExecuteAsync(executor.Plan([Finding(rule, 1500)]));

        Assert.Equal(0, report.FailedCount);
        Assert.Equal(1500, report.BytesReclaimed);
        Assert.Empty(Directory.GetFiles(cache));
    }
}
