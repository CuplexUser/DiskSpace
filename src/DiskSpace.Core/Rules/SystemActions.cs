using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DiskSpace.Core.Model;

namespace DiskSpace.Core.Rules;

/// <summary>
/// The things a person can do about a large item this tool will not delete: turn hibernation
/// off, compact a virtual disk, drop old restore points.
///
/// Each one runs the Windows tool whose job it is (powercfg, diskpart, System Restore's own
/// API) and reports what changed on disk afterwards, measured, instead of trusting the tool's
/// output, which arrives in the display language. All of them need administrator rights.
/// </summary>
public static class SystemActions
{
    /// <summary>Removes hiberfil.sys by turning hibernation off.</summary>
    public static async Task<ActionResult> TurnOffHibernationAsync(
        string hibernationFile, CancellationToken cancellationToken = default)
    {
        var size = FileLength(hibernationFile);
        var run = await RunSystemToolAsync("powercfg.exe", "/hibernate off", null, cancellationToken)
            .ConfigureAwait(false);

        if (run.ExitCode != 0)
            return new ActionResult(false, $"powercfg could not turn hibernation off (exit code {run.ExitCode}).");

        return File.Exists(hibernationFile)
            ? new ActionResult(false, "Hibernation was turned off, but the hibernation file is still there.")
            : new ActionResult(
                true,
                $"Hibernation is off. {ByteSize.Format(size)} reclaimed. "
                + "Turn it back on with powercfg /hibernate on.",
                size);
    }

    /// <summary>
    /// Releases the unused space inside a WSL or Docker virtual disk. The disk has to be
    /// closed first, so WSL is shut down; diskpart then does the compaction.
    /// </summary>
    public static async Task<ActionResult> CompactVirtualDiskAsync(
        string diskFile, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(diskFile))
            return new ActionResult(false, "The virtual disk is no longer there.");

        var before = FileLength(diskFile);

        if (File.Exists(Path.Combine(Environment.SystemDirectory, "wsl.exe")))
            await RunSystemToolAsync("wsl.exe", "--shutdown", null, cancellationToken).ConfigureAwait(false);

        // The virtual machine lets go of the file a few seconds after it is told to stop.
        if (!await WaitUntilClosedAsync(diskFile, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false))
        {
            return new ActionResult(
                false,
                "The virtual disk is still in use. Quit Docker Desktop and close any WSL "
                + "sessions, then try again.");
        }

        var script = new StringBuilder()
            .AppendLine($"select vdisk file=\"{diskFile}\"")
            .AppendLine("attach vdisk readonly")
            .AppendLine("compact vdisk")
            .AppendLine("detach vdisk")
            .AppendLine("exit")
            .ToString();

        // Not cancellable once started: a disk left attached halfway through is worse than
        // waiting for it to finish.
        var run = await RunSystemToolAsync("diskpart.exe", string.Empty, script, CancellationToken.None)
            .ConfigureAwait(false);

        var after = FileLength(diskFile);

        if (after < before)
        {
            return new ActionResult(
                true,
                $"Compacted {Path.GetFileName(diskFile)}: {ByteSize.Format(before)} down to "
                + $"{ByteSize.Format(after)}, {ByteSize.Format(before - after)} reclaimed.",
                before - after);
        }

        return run.ExitCode == 0
            ? new ActionResult(
                true,
                "Nothing to release: the disk is already as small as its contents allow. Free "
                + "space inside it first, for example with docker system prune.")
            : new ActionResult(
                false,
                $"diskpart could not compact the disk (exit code {run.ExitCode}). A disk set "
                + "to sparse mode cannot be compacted this way.");
    }

    private static async Task<bool> WaitUntilClosedAsync(
        string file, TimeSpan limit, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();

        while (true)
        {
            try
            {
                using var probe = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                return true;
            }
            catch (IOException) when (waited.Elapsed < limit)
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    private static long FileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    internal readonly record struct ToolRun(int ExitCode, string Output);

    /// <summary>
    /// Runs a tool from System32 by its full path, with this process's rights. Only for tools
    /// that ship with Windows; anything found on the PATH goes through
    /// <see cref="Cleaning.ToolRunner"/>, which refuses to run elevated.
    /// </summary>
    internal static async Task<ToolRun> RunSystemToolAsync(
        string systemTool, string arguments, string? input, CancellationToken cancellationToken)
    {
        var file = Path.IsPathFullyQualified(systemTool)
            ? systemTool
            : Path.Combine(Environment.SystemDirectory, systemTool);

        try
        {
            using var process = Process.Start(new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = input is not null,
                RedirectStandardOutput = true,
            });

            if (process is null)
                return new ToolRun(-1, string.Empty);

            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception)
                {
                    // Already gone.
                }

                throw;
            }

            return new ToolRun(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return new ToolRun(-1, string.Empty);
        }
    }
}

/// <summary>
/// System Restore's storage: how much the restore points and other shadow copies occupy, and
/// a way to drop all but the newest restore point, which is what Disk Cleanup's "More Options"
/// tab does.
///
/// Measuring goes through Windows PowerShell, because the figures live in WMI and nowhere a
/// file walk can reach: System Volume Information is closed even to administrators. Removing
/// goes through System Restore's own API.
/// </summary>
public sealed class RestorePointStorage : ISystemCleaner
{
    /// <summary>What Windows reported: bytes used by shadow copies, and the restore points on record.</summary>
    public readonly record struct Snapshot(long UsedBytes, IReadOnlyList<int> RestorePoints);

    private const string Query =
        """
        $ProgressPreference = 'SilentlyContinue'
        try { 'used=' + [long](Get-CimInstance Win32_ShadowStorage -ErrorAction Stop | Measure-Object -Property UsedSpace -Sum).Sum } catch { }
        try { 'points=' + ((Get-ComputerRestorePoint -ErrorAction Stop | Sort-Object SequenceNumber | ForEach-Object { $_.SequenceNumber }) -join ',') } catch { }
        """;

    [DllImport("srclient.dll")]
    private static extern uint SRRemoveRestorePoint(uint sequenceNumber);

    public string Description =>
        "Measured by Windows. Restore points are removed through System Restore itself, and "
        + "the newest one is always kept.";

    public async Task<long?> MeasureAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await ReadAsync(cancellationToken).ConfigureAwait(false);
        return snapshot?.UsedBytes;
    }

    /// <summary>Not part of a cleanup run. Trimming is offered as an action on the finding.</summary>
    public Task<long> CleanAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Restore points are trimmed through the finding's action.");

    /// <summary>Removes every restore point except the newest and reports what that freed.</summary>
    public async Task<ActionResult> KeepOnlyNewestAsync(CancellationToken cancellationToken = default)
    {
        var before = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (before is not { } start)
            return new ActionResult(false, "System Restore could not be read. This needs administrator rights.");

        if (start.RestorePoints.Count < 2)
        {
            return new ActionResult(
                true,
                start.RestorePoints.Count == 0
                    ? "There are no restore points. The space is held by other shadow copies, which are left alone."
                    : "There is only one restore point, and the newest is always kept.");
        }

        var removed = 0;

        // Oldest first, and never the last entry: that is the one to keep.
        foreach (var sequenceNumber in start.RestorePoints.Take(start.RestorePoints.Count - 1))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (await Task.Run(() => SRRemoveRestorePoint((uint)sequenceNumber), cancellationToken).ConfigureAwait(false) == 0)
                removed++;
        }

        var after = await ReadAsync(cancellationToken).ConfigureAwait(false);
        var freed = Math.Max(0, start.UsedBytes - (after?.UsedBytes ?? start.UsedBytes));

        return removed == 0
            ? new ActionResult(false, "Windows refused to remove the restore points.")
            : new ActionResult(
                true,
                $"Removed {removed} older restore point(s), kept the newest. {ByteSize.Format(freed)} reclaimed.",
                freed);
    }

    private static async Task<Snapshot?> ReadAsync(CancellationToken cancellationToken)
    {
        var powershell = Path.Combine(
            Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        if (!File.Exists(powershell))
            return null;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Query));

        var run = await SystemActions
            .RunSystemToolAsync(
                powershell, $"-NoProfile -NonInteractive -EncodedCommand {encoded}", null, cancellationToken)
            .ConfigureAwait(false);

        return Parse(run.Output);
    }

    /// <summary>
    /// Reads the two lines the query prints. Null when the storage figure is missing, which is
    /// what an unelevated run produces: without it there is nothing to report.
    /// </summary>
    public static Snapshot? Parse(string output)
    {
        long? used = null;
        var points = new List<int>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("used=", StringComparison.Ordinal)
                && long.TryParse(line["used=".Length..], NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            {
                used = bytes;
            }
            else if (line.StartsWith("points=", StringComparison.Ordinal))
            {
                foreach (var part in line["points=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                        points.Add(number);
                }
            }
        }

        points.Sort();
        return used is { } value ? new Snapshot(value, points) : null;
    }
}
