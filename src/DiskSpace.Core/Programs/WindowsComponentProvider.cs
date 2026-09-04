using DiskSpace.Core.Model;

namespace DiskSpace.Core.Programs;

/// <summary>
/// The parts of Windows that occupy tens of gigabytes and belong to no application.
///
/// Reported for the same reason <see cref="Rules.LargeItemProvider"/> reports the page file:
/// without them a person accounts for every program on the machine and still cannot explain the
/// disk. Each one names the correct way to deal with it, which is never "delete the folder", and
/// in most of these cases would break the system if it were.
///
/// hiberfil.sys, pagefile.sys and swapfile.sys are deliberately absent: the Scan page already
/// reports those, and one number said twice in two places is worse than one.
/// </summary>
public sealed class WindowsComponentProvider : IProgramProvider
{
    public string Name => "Windows components";

    public IEnumerable<InstalledProgram> GetPrograms()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var system = Path.GetPathRoot(windows) ?? @"C:\";

        yield return Component(
            "winsxs",
            "Component store (WinSxS)",
            Path.Combine(windows, "WinSxS"),
            "Every version of every Windows component that has ever been installed, which is "
            + "what lets an update be rolled back.",
            "Run `Dism /Online /Cleanup-Image /StartComponentCleanup /ResetBase` from an "
            + "elevated prompt, which discards the ability to uninstall existing updates. "
            + "Deleting anything here by hand breaks servicing permanently.",
            "The measured size overstates the real cost: most of WinSxS is hard links to files "
            + "that also live in System32, and those bytes are counted once here and once there.");

        yield return Component(
            "installer",
            "Installer cache",
            Path.Combine(windows, "Installer"),
            "The cached MSI and MSP files every installed product needs in order to repair, "
            + "update or remove itself.",
            "There is no supported way to shrink this. Deleting from it leaves products that "
            + "can no longer be repaired or uninstalled at all.");

        yield return Component(
            "driverstore",
            "Driver store",
            Path.Combine(windows, "System32", "DriverStore", "FileRepository"),
            "Every driver package Windows has staged, including the versions each one replaced.",
            "List them with `pnputil /enum-drivers`, then remove a superseded package with "
            + "`pnputil /delete-driver oemNN.inf`. Never delete the folders directly.");

        yield return Component(
            "dotnet",
            ".NET runtimes and SDKs",
            Path.Combine(programFiles, "dotnet"),
            "Side-by-side .NET runtimes and SDK bands, which accumulate because nothing removes "
            + "the old ones.",
            "Remove old SDK bands with the .NET Uninstall Tool, which knows which ones something "
            + "still depends on. Deleting folders under here breaks every project pinned to them.");

        var windowsOld = Path.Combine(system, "Windows.old");
        if (Directory.Exists(windowsOld))
        {
            yield return Component(
                "windows.old",
                "Previous Windows installation",
                windowsOld,
                "The whole of the previous Windows installation, kept so an upgrade can be "
                + "rolled back.",
                "Use Disk Cleanup's \"Previous Windows installation(s)\" entry, or wait: Windows "
                + "removes it by itself once the rollback window has passed.");
        }
    }

    private static InstalledProgram Component(
        string id, string name, string path, string description, string remedy, string? note = null) =>
        new()
        {
            Id = "windows:" + id,
            Name = name,
            Source = ProgramSource.WindowsComponent,
            Risk = RiskLevel.ReportOnly,
            Locations = [new ProgramLocation(path, LocationKind.Install)],
            Publisher = "Microsoft Windows",
            Remedy = remedy,
            Note = note ?? description,
        };
}
