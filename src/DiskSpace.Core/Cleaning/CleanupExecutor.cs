using System.Diagnostics;
using DiskSpace.Core.Model;
using DiskSpace.Core.Quarantine;
using DiskSpace.Core.Rules;
using DiskSpace.Core.Safety;

namespace DiskSpace.Core.Cleaning;

/// <summary>
/// Turns selected findings into a plan, then carries the plan out.
///
/// Two phases, strictly. <see cref="Plan"/> produces the exact list of paths, and
/// <see cref="ExecuteAsync"/> accepts only that object — so nothing can be deleted that was not
/// first shown. Every path is re-checked against <see cref="PathGuard"/> immediately before it
/// is touched, because a plan that passed at preview time is not evidence that it still holds.
/// </summary>
public sealed class CleanupExecutor(QuarantineStore? quarantine = null)
{
    private readonly QuarantineStore _quarantine = quarantine ?? new QuarantineStore();
    private readonly PathGuard _guard = new();

    /// <summary>
    /// Decides how each finding will be disposed of. Orphaned application data is quarantined —
    /// its detection is a heuristic — while caches, which regenerate, are deleted outright.
    /// </summary>
    public CleanupPlan Plan(IEnumerable<CleanupFinding> selected)
    {
        var items = selected
            .Where(f => f.IsActionable)
            .Select(finding => new PlannedItem
            {
                Finding = finding,
                Disposal = ShouldQuarantine(finding) ? Disposal.Quarantine : Disposal.Delete,
            })
            .ToList();

        return new CleanupPlan { Items = items };
    }

    private static bool ShouldQuarantine(CleanupFinding finding) =>
        finding.Rule.Risk == RiskLevel.Review
        && finding.Rule.RemoveTargetDirectory
        && finding.Rule.Id.StartsWith("orphan.", StringComparison.Ordinal);

    public async Task<CleanupReport> ExecuteAsync(
        CleanupPlan plan,
        IProgress<CleanupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var outcomes = new List<CleanupOutcome>();
        long reclaimed = 0;
        var completed = 0;

        using var log = AuditLog.StartRun();

        foreach (var item in plan.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new CleanupProgress(completed, plan.Items.Count, reclaimed, item.Path));

            var outcome = await DisposeItemAsync(item, cancellationToken).ConfigureAwait(false);
            outcomes.Add(outcome);

            if (outcome.Succeeded)
                reclaimed += outcome.BytesReclaimed;

            log.Write(new AuditEntry
            {
                Timestamp = DateTimeOffset.Now,
                Path = outcome.Path,
                Bytes = outcome.BytesReclaimed,
                RuleId = item.Rule.Id,
                RuleName = item.Rule.Name,
                Risk = item.Risk.ToString(),
                Disposal = outcome.Disposal.ToString(),
                Succeeded = outcome.Succeeded,
                Error = outcome.Error,
                HeldBy = outcome.HeldBy,
            });

            completed++;
        }

        progress?.Report(new CleanupProgress(completed, plan.Items.Count, reclaimed, "Done"));
        stopwatch.Stop();

        return new CleanupReport { Outcomes = outcomes, Duration = stopwatch.Elapsed };
    }

    private async Task<CleanupOutcome> DisposeItemAsync(
        PlannedItem item, CancellationToken cancellationToken)
    {
        // Re-validated here, not trusted from planning time. Between preview and execution the
        // filesystem can change underneath us, and this is the only check that matters.
        var verdict = _guard.Check(item.Path, item.Rule.Root);
        if (!verdict.Allowed)
        {
            return new CleanupOutcome
            {
                Path = item.Path,
                Succeeded = false,
                BytesReclaimed = 0,
                Disposal = item.Disposal,
                Error = $"Refused by safety check: {verdict.Reason}",
            };
        }

        try
        {
            if (item.Disposal == Disposal.Quarantine)
            {
                var manifest = await _quarantine
                    .QuarantineAsync(item.Path, item.Rule, null, cancellationToken)
                    .ConfigureAwait(false);

                return new CleanupOutcome
                {
                    Path = item.Path,
                    Succeeded = true,
                    BytesReclaimed = item.Size,
                    Disposal = Disposal.Quarantine,
                    Error = null,
                    HeldBy = null,
                };
            }

            var reclaimed = await Task
                .Run(() => DeleteTree(item, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            return new CleanupOutcome
            {
                Path = item.Path,
                Succeeded = true,
                BytesReclaimed = reclaimed,
                Disposal = Disposal.Delete,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CleanupOutcome
            {
                Path = item.Path,
                Succeeded = false,
                BytesReclaimed = 0,
                Disposal = item.Disposal,
                Error = ex.Message,
                HeldBy = RestartManager.DescribeLockers(item.Path),
            };
        }
    }

    /// <summary>
    /// Removes the item. Delegates to <see cref="SafeDelete"/>, which skips reparse points so a
    /// junction inside a cache costs its link and never the data it points at.
    /// </summary>
    private static long DeleteTree(PlannedItem item, CancellationToken cancellationToken)
    {
        if (File.Exists(item.Path))
            return SafeDelete.DeleteFile(item.Path);

        return Directory.Exists(item.Path)
            ? SafeDelete.DeleteDirectory(item.Path, item.Rule.RemoveTargetDirectory, cancellationToken)
            : 0;
    }
}
