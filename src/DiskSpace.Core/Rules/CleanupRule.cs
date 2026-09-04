using DiskSpace.Core.Model;

namespace DiskSpace.Core.Rules;

/// <summary>
/// One thing the tool knows how to reclaim.
///
/// A rule describes territory and intent; it never deletes and never decides safety. Resolution
/// turns it into concrete <see cref="CleanupFinding"/> items, and every one of those is checked
/// against <see cref="Safety.PathGuard"/> before anything happens to it.
/// </summary>
public sealed record CleanupRule
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Grouping shown in the results tree, e.g. "Package manager caches".</summary>
    public required string Category { get; init; }

    public required RiskLevel Risk { get; init; }

    /// <summary>What this rule removes, in one line.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// The consequence of removing it, in plain language. Shown in the detail pane, because
    /// "is this safe?" is the only question that actually matters to someone deciding.
    /// </summary>
    public required string WhatBreaks { get; init; }

    /// <summary>
    /// The rule's declared territory. Nothing it resolves may fall outside this once canonical,
    /// which is what stops a junction inside a cache from reaching the rest of the disk.
    /// </summary>
    public required string Root { get; init; }

    /// <summary>Absolute paths this rule proposes. Non-existent entries are dropped silently.</summary>
    public required IReadOnlyList<string> Targets { get; init; }

    /// <summary>
    /// When false — the default — the target directory is emptied but kept. Tools often break
    /// if their cache directory is missing rather than empty.
    /// </summary>
    public bool RemoveTargetDirectory { get; init; }

    /// <summary>Only touch items untouched for at least this long.</summary>
    public TimeSpan? MinimumAge { get; init; }

    /// <summary>Ignore findings smaller than this. Keeps byte-sized noise out of the results.</summary>
    public long MinimumSize { get; init; }

    public bool RequiresElevation { get; init; }

    /// <summary>
    /// A first-party purge command, preferred over deleting files ourselves. A package manager
    /// knows its own index; deleting underneath it can leave the tool believing in files that
    /// are gone.
    /// </summary>
    public PurgeCommand? Purge { get; init; }
}

/// <summary>A tool's own cache-clearing command, e.g. <c>npm cache clean --force</c>.</summary>
public sealed record PurgeCommand(string Executable, string Arguments)
{
    public override string ToString() => $"{Executable} {Arguments}";
}

/// <summary>One resolved, measured item a rule proposes to remove.</summary>
public sealed record CleanupFinding
{
    public required CleanupRule Rule { get; init; }
    public required string Path { get; init; }
    public required long Size { get; init; }
    public required long FileCount { get; init; }
    public required DateTime LastWriteUtc { get; init; }

    /// <summary>Extra context for this specific item, e.g. why a folder looks orphaned.</summary>
    public string? Note { get; init; }

    /// <summary>Report-only findings explain disk usage but are never removed by this tool.</summary>
    public bool IsActionable => Rule.Risk != RiskLevel.ReportOnly;
}
