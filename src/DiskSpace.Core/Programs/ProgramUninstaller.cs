using System.Diagnostics;
using DiskSpace.Core.Model;

namespace DiskSpace.Core.Programs;

/// <summary>
/// Hands a removal to whoever installed the program.
///
/// This is the only thing the Programs page can do to a machine, and it deliberately does not
/// delete anything itself. <see cref="Safety.PathGuard"/> refuses Program Files outright, and
/// rightly: an installer knows which of its files are shared, which services to stop and what to
/// unregister, and none of that is recoverable from a directory listing.
/// </summary>
public static class ProgramUninstaller
{
    /// <summary>Whether there is a command to run at all. Most user-directory apps have none.</summary>
    public static bool CanUninstall(InstalledProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);

        return program.Risk != RiskLevel.ReportOnly
               && !string.IsNullOrWhiteSpace(program.UninstallCommand);
    }

    /// <summary>
    /// The command line as it would be run, for the confirmation dialog. Nothing is removed
    /// without this being shown first, in the same spirit as the cleanup dialog listing every
    /// path it will touch.
    /// </summary>
    public static string Describe(InstalledProgram program)
    {
        ArgumentNullException.ThrowIfNull(program);
        return program.UninstallCommand ?? string.Empty;
    }

    /// <summary>
    /// Starts the vendor's own uninstaller and returns it, or null when there is nothing to run.
    /// Shell execution on purpose, so the installer's own interface appears the way it would if
    /// it had been started from Add/Remove Programs.
    /// </summary>
    public static Process? Start(InstalledProgram program)
    {
        if (!CanUninstall(program))
            return null;

        var (executable, arguments) =
            RegistryProgramProvider.SplitCommand(program.UninstallCommand);

        if (executable is null)
            return null;

        return Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
        });
    }
}
