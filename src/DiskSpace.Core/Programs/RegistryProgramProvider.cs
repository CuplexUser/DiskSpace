using System.Globalization;
using DiskSpace.Core.Model;
using DiskSpace.Core.Safety;
using Microsoft.Win32;

namespace DiskSpace.Core.Programs;

/// <summary>
/// What Windows records under Add/Remove Programs: both machine hives in both registry views,
/// plus the current user's.
///
/// The same keys <see cref="Rules.InstalledSoftware"/> reads, for a different purpose. That one
/// wants breadth, so a leftover folder is never wrongly accused; this one wants each entry's
/// install directory, which that one throws away. Merging them would make a matcher tuned for
/// breadth also responsible for precision.
/// </summary>
public sealed class RegistryProgramProvider : IProgramProvider
{
    private const string UninstallPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Shallowest a real install directory gets: C:\Program Files\Vendor counts three.</summary>
    private const int MinimumFallbackDepth = 3;

    public string Name => "Installed programs";

    public IEnumerable<InstalledProgram> GetPrograms()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (hive, view) in new[]
                 {
                     (RegistryHive.LocalMachine, RegistryView.Registry64),
                     (RegistryHive.LocalMachine, RegistryView.Registry32),
                     (RegistryHive.CurrentUser, RegistryView.Default),
                 })
        {
            foreach (var program in Read(hive, view))
            {
                // The same product is often written into more than one view. First wins.
                if (seen.Add(program.Id))
                    yield return program;
            }
        }
    }

    private static List<InstalledProgram> Read(RegistryHive hive, RegistryView view)
    {
        var programs = new List<InstalledProgram>();

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null)
                return programs;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName);
                if (entry is null)
                    continue;

                if (Read(entry, subKeyName) is { } program)
                    programs.Add(program);
            }
        }
        catch (Exception)
        {
            // An unreadable hive costs coverage, not correctness of the rest.
        }

        return programs;
    }

    private static InstalledProgram? Read(RegistryKey entry, string subKeyName)
    {
        if (entry.GetValue("DisplayName") as string is not { Length: > 0 } displayName)
            return null;

        // Hidden from Add/Remove Programs, which means Windows itself does not consider it a
        // separate thing a person installed.
        if (entry.GetValue("SystemComponent") is int and not 0)
            return null;

        // An update hanging off a parent product, not an install of its own. Listing these
        // separately makes a page of forty security updates out of one application.
        if (entry.GetValue("ParentKeyName") as string is { Length: > 0 })
            return null;

        var uninstall = entry.GetValue("QuietUninstallString") as string
                        ?? entry.GetValue("UninstallString") as string;

        var locations = new List<ProgramLocation>();

        var installLocation = entry.GetValue("InstallLocation") as string;
        if (Usable(installLocation))
            locations.Add(new ProgramLocation(installLocation!.Trim(), LocationKind.Install));
        else if (DirectoryOfUninstaller(uninstall) is { } derived)
            locations.Add(new ProgramLocation(derived, LocationKind.Install));

        foreach (var data in DataLocations(displayName))
            locations.Add(data);

        return new InstalledProgram
        {
            Id = "registry:" + subKeyName,
            Name = displayName.Trim(),
            Source = ProgramSource.Registry,
            Risk = RiskLevel.Review,
            Locations = locations,
            Publisher = (entry.GetValue("Publisher") as string)?.Trim(),
            Version = (entry.GetValue("DisplayVersion") as string)?.Trim(),
            InstallDate = ParseInstallDate(entry.GetValue("InstallDate") as string),
            UninstallCommand = uninstall,

            // Recorded in kilobytes, and only ever a claim by the installer.
            RegistryEstimatedSize = entry.GetValue("EstimatedSize") is int kilobytes and > 0
                ? kilobytes * 1024L
                : 0,
        };
    }

    /// <summary>
    /// Where a program's settings and saved state accumulate, which for anything long-lived is
    /// often larger than the install itself. Matched by folder name against the display name.
    /// </summary>
    private static IEnumerable<ProgramLocation> DataLocations(string displayName)
    {
        var folder = SafeFolderName(displayName);
        if (folder.Length < 3)
            yield break;

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;

            var candidate = Path.Combine(root, folder);
            if (Directory.Exists(candidate))
                yield return new ProgramLocation(candidate, LocationKind.Data);
        }
    }

    /// <summary>
    /// A display name is not a folder name: it carries versions, architectures and punctuation
    /// that no installer puts on disk. Trimmed back to the part that plausibly is one.
    /// </summary>
    private static string SafeFolderName(string displayName)
    {
        var name = displayName;

        var bracket = name.IndexOf('(', StringComparison.Ordinal);
        if (bracket > 0)
            name = name[..bracket];

        name = name.Trim();

        // Drop a trailing version, so "GIMP 3.2.4" looks for "GIMP".
        var lastSpace = name.LastIndexOf(' ');
        if (lastSpace > 0 && name[(lastSpace + 1)..].All(c => char.IsDigit(c) || c == '.'))
            name = name[..lastSpace];

        return Path.GetInvalidFileNameChars().Aggregate(name.Trim(), (current, c) => current.Replace(c, '_'));
    }

    /// <summary>
    /// Many installers record no install location at all. The uninstaller almost always lives in
    /// the directory it removes, which recovers most of them: "Mozilla Maintenance Service" has
    /// no InstallLocation but uninstalls from its own folder under Program Files (x86).
    /// </summary>
    private static string? DirectoryOfUninstaller(string? uninstallCommand)
    {
        var executable = SplitCommand(uninstallCommand).Executable;
        if (executable is null)
            return null;

        try
        {
            // msiexec removes a product by GUID from System32; its directory says nothing.
            if (string.Equals(
                    Path.GetFileNameWithoutExtension(executable), "msiexec",
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var directory = Path.GetDirectoryName(Path.GetFullPath(executable));
            if (directory is null || PathCanonicalizer.Depth(directory) < MinimumFallbackDepth)
                return null;

            return Directory.Exists(directory) ? directory : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Splits a registry command line into an executable and its arguments. These are quoted
    /// about half the time, and the unquoted half may still contain spaces in the path.
    /// </summary>
    internal static (string? Executable, string Arguments) SplitCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return (null, string.Empty);

        var text = command.Trim();

        if (text[0] == '"')
        {
            var closing = text.IndexOf('"', 1);
            return closing < 0
                ? (text.Trim('"'), string.Empty)
                : (text[1..closing], text[(closing + 1)..].Trim());
        }

        // Unquoted: walk forward to the first space that ends something that exists on disk,
        // which is the only way to tell an argument from the rest of a path with spaces in it.
        for (var i = text.IndexOf(' '); i > 0; i = text.IndexOf(' ', i + 1))
        {
            var candidate = text[..i];
            if (File.Exists(candidate))
                return (candidate, text[(i + 1)..].Trim());
        }

        var space = text.IndexOf(' ');
        return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
    }

    private static bool Usable(string? location) =>
        !string.IsNullOrWhiteSpace(location) && Directory.Exists(location.Trim());

    /// <summary>Recorded as yyyyMMdd when it is recorded at all, which is often not.</summary>
    private static DateOnly? ParseInstallDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
}
