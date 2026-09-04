using DiskSpace.Core.Model;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Programs;

/// <summary>Where the knowledge that something is installed came from.</summary>
public enum ProgramSource
{
    /// <summary>An uninstall entry in the registry, which is what most installers write.</summary>
    Registry,

    /// <summary>An MSIX or AppX package, installed from the Store or sideloaded.</summary>
    StorePackage,

    /// <summary>Unpacked into the profile and registered nowhere. Scoop, npm, portable apps.</summary>
    UserInstall,

    /// <summary>Part of Windows. Reported for the arithmetic, never removed by this tool.</summary>
    WindowsComponent,
}

/// <summary>What a measured path is to the program that owns it.</summary>
public enum LocationKind
{
    /// <summary>The program itself.</summary>
    Install,

    /// <summary>Settings, profiles and saved state the program accumulated.</summary>
    Data,
}

public readonly record struct ProgramLocation(string Path, LocationKind Kind);

/// <summary>
/// One thing installed on this machine, before anything has been measured.
///
/// Providers produce these cheaply, the same division of labor <see cref="Rules.IRuleProvider"/>
/// uses: describe the territory, and let the catalog do the expensive part.
/// </summary>
public sealed record InstalledProgram
{
    /// <summary>Stable within a source: a registry subkey, a package full name, or a path.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required ProgramSource Source { get; init; }

    public required RiskLevel Risk { get; init; }

    public required IReadOnlyList<ProgramLocation> Locations { get; init; }

    public string? Publisher { get; init; }

    public string? Version { get; init; }

    public DateOnly? InstallDate { get; init; }

    /// <summary>
    /// The command that removes this program, quiet variant preferred. DiskSpace never deletes
    /// program files itself; it hands the job to whoever installed them.
    /// </summary>
    public string? UninstallCommand { get; init; }

    /// <summary>
    /// The size the installer claimed, in bytes. Used only when nothing could be measured, and
    /// marked as an estimate when it is.
    /// </summary>
    public long RegistryEstimatedSize { get; init; }

    /// <summary>For a Windows component, the correct way to shrink it. Never "delete it".</summary>
    public string? Remedy { get; init; }

    /// <summary>Something worth saying about this entry that is not true of its whole source.</summary>
    public string? Note { get; init; }
}

/// <summary>One measured path, or the reason it could not be measured.</summary>
public readonly record struct MeasuredLocation(
    string Path, LocationKind Kind, long Size, long FileCount, string? Error);

/// <summary>What a program actually occupies, as far as this machine will say.</summary>
public sealed record ProgramFootprint
{
    public required InstalledProgram Program { get; init; }

    public required IReadOnlyList<MeasuredLocation> Parts { get; init; }

    /// <summary>Nothing could be measured, so the size shown is the installer's own claim.</summary>
    public required bool SizeIsEstimated { get; init; }

    public long InstallSize => Sum(LocationKind.Install);

    public long DataSize => Sum(LocationKind.Data);

    public long TotalSize => SizeIsEstimated
        ? Program.RegistryEstimatedSize
        : InstallSize + DataSize;

    public long FileCount => Parts.Sum(p => p.FileCount);

    /// <summary>Locations that exist but could not be read, such as the Store app folder.</summary>
    public IEnumerable<MeasuredLocation> Unreadable => Parts.Where(p => p.Error is not null);

    private long Sum(LocationKind kind) => Parts.Where(p => p.Kind == kind).Sum(p => p.Size);
}

/// <summary>Progress through the measuring pass, on the same shape as the rule catalog's.</summary>
public readonly record struct ProgramProgress(int Completed, int Total, string CurrentProgram);

/// <summary>A location a measurement could not read, kept rather than thrown.</summary>
public readonly record struct ProgramIssue(string Path, string Reason)
{
    public static ProgramIssue From(ScanIssue issue) => new(issue.Path, issue.Reason);
}
