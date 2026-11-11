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

    /// <summary>
    /// Only take files untouched for at least this long, judged one file at a time. A temp
    /// directory always holds something written a minute ago, so a test on the directory as a
    /// whole would never let it be cleaned at all. With this set the target directory is
    /// never removed, only thinned.
    /// </summary>
    public TimeSpan? MinimumAge { get; init; }

    /// <summary>
    /// Stage what this rule removes where it can be restored, instead of deleting it. For
    /// findings that rest on inference, where being wrong shows up weeks later.
    /// </summary>
    public bool Quarantine { get; init; }

    /// <summary>
    /// Something the user can do about a finding this tool will not remove: turn a feature
    /// off, compact a disk image. Offered as a button beside the finding, never run by a scan
    /// or a cleanup.
    /// </summary>
    public RuleAction? Action { get; init; }

    /// <summary>Ignore findings smaller than this. Keeps byte-sized noise out of the results.</summary>
    public long MinimumSize { get; init; }

    public bool RequiresElevation { get; init; }

    /// <summary>
    /// A first-party purge command, preferred over deleting files ourselves. A package manager
    /// knows its own index; deleting underneath it can leave the tool believing in files that
    /// are gone.
    /// </summary>
    public PurgeCommand? Purge { get; init; }

    /// <summary>
    /// Set when the data belongs to a Windows component that measures and removes it itself.
    /// Such a rule proposes no paths of its own, so <see cref="Targets"/> is empty and
    /// <see cref="Root"/> only says where the data lives, for display.
    /// </summary>
    public ISystemCleaner? Cleaner { get; init; }
}

/// <summary>
/// Something that owns its data and has its own way of measuring and removing it, where
/// deleting the files underneath it would be wrong or impossible. The component store is the
/// plain example: only Windows can say which of its files are superseded.
/// </summary>
public interface ISystemCleaner
{
    /// <summary>What does the removing, in a sentence. Shown in the detail pane.</summary>
    string Description { get; }

    /// <summary>Bytes it would free right now, or null when it has nothing to offer.</summary>
    Task<long?> MeasureAsync(CancellationToken cancellationToken = default);

    /// <summary>Runs the cleanup and returns the bytes freed.</summary>
    Task<long> CleanAsync(IProgress<long>? progress = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// A tool's own cache-clearing command, e.g. <c>npm cache clean --force</c>.
///
/// The command is pinned to the folder the rule lists, either by <c>{path}</c> in
/// <paramref name="Arguments"/> or by setting <paramref name="PathVariable"/> in the tool's
/// environment. Without that a tool clears wherever its own configuration points, which need
/// not be the folder that was shown and approved.
/// </summary>
public sealed record PurgeCommand(string Executable, string Arguments, string? PathVariable = null)
{
    private const string PathToken = "{path}";

    /// <summary>The arguments as run against <paramref name="target"/>.</summary>
    public string ArgumentsFor(string target) =>
        Arguments.Replace(PathToken, $"\"{target.TrimEnd(Path.DirectorySeparatorChar)}\"", StringComparison.Ordinal);

    /// <summary>The environment the tool is given so that it works on <paramref name="target"/>.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentFor(string target) =>
        PathVariable is null ? null : new Dictionary<string, string> { [PathVariable] = target };

    public override string ToString() =>
        $"{Executable} {Arguments.Replace(PathToken, "<this folder>", StringComparison.Ordinal)}";
}

/// <summary>
/// What <see cref="RuleAction.RunAsync"/> reports back: in words for the status line, and the
/// space it freed, measured on disk, for the audit log.
/// </summary>
public readonly record struct ActionResult(bool Succeeded, string Message, long BytesReclaimed = 0);

/// <summary>An action offered beside a finding. See <see cref="CleanupRule.Action"/>.</summary>
public sealed record RuleAction
{
    /// <summary>The button text, e.g. "Turn off hibernation".</summary>
    public required string Label { get; init; }

    /// <summary>What it does and what it costs, shown before it runs.</summary>
    public required string Consequence { get; init; }

    public required Func<CancellationToken, Task<ActionResult>> RunAsync { get; init; }
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
