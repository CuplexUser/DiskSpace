using DiskSpace.Core.Model;
using DiskSpace.Core.Rules;

namespace DiskSpace.Core.Cleaning;

/// <summary>How an item leaves: deleted outright, or staged where it can be restored.</summary>
public enum Disposal
{
    Delete,
    Quarantine,
}

/// <summary>One item in a plan: a concrete path, its measured size, and how it will be removed.</summary>
public sealed record PlannedItem
{
    public required CleanupFinding Finding { get; init; }
    public required Disposal Disposal { get; init; }

    public string Path => Finding.Path;
    public long Size => Finding.Size;
    public RiskLevel Risk => Finding.Rule.Risk;
    public CleanupRule Rule => Finding.Rule;
}

/// <summary>
/// The exact set of things that will be removed, produced by
/// <see cref="CleanupExecutor.Plan"/> and displayed before anything happens.
///
/// Execution accepts only a plan object, never a raw list of paths, so the UI cannot delete
/// something it did not put in front of the user first.
/// </summary>
public sealed record CleanupPlan
{
    public required IReadOnlyList<PlannedItem> Items { get; init; }

    public long TotalSize => Items.Sum(i => i.Size);

    public long TotalFileCount => Items.Sum(i => i.Finding.FileCount);

    public bool ContainsQuarantine => Items.Any(i => i.Disposal == Disposal.Quarantine);

    /// <summary>
    /// True when the plan holds anything beyond routine caches. The confirmation dialog demands
    /// a typed acknowledgement in that case, because deletion here is permanent.
    /// </summary>
    public bool NeedsExplicitConfirmation =>
        Items.Any(i => i.Risk is RiskLevel.Review or RiskLevel.Advanced);

    public IEnumerable<IGrouping<string, PlannedItem>> ByCategory =>
        Items.GroupBy(i => i.Rule.Category);
}

/// <summary>What happened to one item.</summary>
public sealed record CleanupOutcome
{
    public required string Path { get; init; }
    public required bool Succeeded { get; init; }
    public required long BytesReclaimed { get; init; }
    public required Disposal Disposal { get; init; }
    public string? Error { get; init; }

    /// <summary>Set when a file could not be removed because a process held it open.</summary>
    public string? HeldBy { get; init; }
}

public sealed record CleanupReport
{
    public required IReadOnlyList<CleanupOutcome> Outcomes { get; init; }
    public required TimeSpan Duration { get; init; }

    public long BytesReclaimed => Outcomes.Where(o => o.Succeeded).Sum(o => o.BytesReclaimed);
    public int SucceededCount => Outcomes.Count(o => o.Succeeded);
    public int FailedCount => Outcomes.Count(o => !o.Succeeded);

    public IEnumerable<CleanupOutcome> Failures => Outcomes.Where(o => !o.Succeeded);
}

public readonly record struct CleanupProgress(
    int Completed, int Total, long BytesReclaimed, string CurrentPath);
