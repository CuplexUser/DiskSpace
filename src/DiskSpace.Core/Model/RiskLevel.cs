namespace DiskSpace.Core.Model;

/// <summary>
/// How much judgement a finding needs before it is removed. Drives both the UI colour
/// and, for <see cref="Review"/>, whether the item is quarantined rather than deleted.
/// </summary>
public enum RiskLevel
{
    /// <summary>Regenerates on demand. Package and browser caches, temp files.</summary>
    Safe,

    /// <summary>Detection is fuzzy or the data is not reproducible. Never auto-selected.</summary>
    Review,

    /// <summary>Affects system state or recovery options. Never auto-selected.</summary>
    Advanced,

    /// <summary>Surfaced to explain disk usage; the tool will not remove it.</summary>
    ReportOnly,
}
