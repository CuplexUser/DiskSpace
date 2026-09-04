using System.Runtime.InteropServices;
using System.Text;
using DiskSpace.Core.Model;
using Microsoft.Win32;

namespace DiskSpace.Core.Programs;

/// <summary>
/// MSIX and AppX packages, read from the package repository the app model keeps in the registry.
///
/// <c>C:\Program Files\WindowsApps</c> refuses to be listed, so packages cannot be discovered by
/// walking it. Each package folder underneath can still be opened by its own path, which is what
/// the repository supplies and why the install sizes here come out at all. A package that denies
/// even that is reported as unreadable rather than as zero bytes.
///
/// Measured alongside it is <c>%LOCALAPPDATA%\Packages\&lt;family&gt;</c>, which is where a Store
/// app's saved state and caches accumulate and is usually the part worth acting on.
/// </summary>
public sealed class StorePackageProvider : IProgramProvider
{
    private const string RepositoryPath =
        @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public string Name => "Store apps";

    public IEnumerable<InstalledProgram> GetPrograms()
    {
        RegistryKey? repository = null;

        try
        {
            repository = Registry.CurrentUser.OpenSubKey(RepositoryPath);
        }
        catch (Exception)
        {
            // Package repository unreadable; skip.
        }

        if (repository is null)
            yield break;

        using (repository)
        {
            foreach (var packageName in SafeSubKeyNames(repository))
            {
                // Winget's source packages index everything winget could install, not what is
                // installed. Treating them as programs fills the page with a catalog.
                if (packageName.Contains("Winget.Source", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Read(repository, packageName) is { } program)
                    yield return program;
            }
        }
    }

    private static InstalledProgram? Read(RegistryKey repository, string packageFullName)
    {
        try
        {
            using var entry = repository.OpenSubKey(packageFullName);
            if (entry is null)
                return null;

            var root = entry.GetValue("PackageRootFolder") as string;
            var familyName = FamilyName(packageFullName);

            var locations = new List<ProgramLocation>();
            if (!string.IsNullOrWhiteSpace(root))
                locations.Add(new ProgramLocation(root, LocationKind.Install));

            var data = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages",
                familyName);

            if (Directory.Exists(data))
                locations.Add(new ProgramLocation(data, LocationKind.Data));

            if (locations.Count == 0)
                return null;

            return new InstalledProgram
            {
                Id = "msix:" + packageFullName,
                Name = DisplayName(entry, packageFullName),
                Source = ProgramSource.StorePackage,
                Risk = RiskLevel.Review,
                Locations = locations,
                Publisher = entry.GetValue("PublisherDisplayName") as string,
                Version = VersionOf(packageFullName),
                UninstallCommand = UninstallCommand(packageFullName),
                Note = "Removing a Store app is Windows' job, not this tool's: the install lives "
                       + "under WindowsApps, which refuses to be listed and refuses to be "
                       + "written to. The data folder is separate, and is usually what grows.",
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The recorded display name is usually an indirect string, of the form
    /// <c>@{PackageId?ms-resource://...}</c>, which has to be resolved through the package's own
    /// resource map. When that fails the package family name is at least readable.
    /// </summary>
    private static string DisplayName(RegistryKey entry, string packageFullName)
    {
        var recorded = entry.GetValue("DisplayName") as string;

        if (!string.IsNullOrWhiteSpace(recorded) && !recorded.StartsWith('@'))
            return recorded;

        if (!string.IsNullOrWhiteSpace(recorded) && Resolve(recorded) is { Length: > 0 } resolved)
            return resolved;

        return ReadableName(packageFullName);
    }

    private static string? Resolve(string indirect)
    {
        try
        {
            var buffer = new StringBuilder(1024);
            return SHLoadIndirectString(indirect, buffer, buffer.Capacity, 0) == 0
                ? buffer.ToString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// "Microsoft.WindowsTerminal_1.18.0.0_x64__8wekyb3d8bbwe" becomes "WindowsTerminal": the
    /// publisher prefix carries no information a person wants in a list.
    /// </summary>
    private static string ReadableName(string packageFullName)
    {
        var identity = packageFullName.Split('_')[0];
        var dot = identity.LastIndexOf('.');
        return dot > 0 && dot < identity.Length - 1 ? identity[(dot + 1)..] : identity;
    }

    private static string FamilyName(string packageFullName)
    {
        // Name_Version_Architecture_ResourceId_PublisherId, and the family is the first and last.
        var parts = packageFullName.Split('_');
        return parts.Length >= 5 ? $"{parts[0]}_{parts[^1]}" : packageFullName;
    }

    private static string? VersionOf(string packageFullName)
    {
        var parts = packageFullName.Split('_');
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static string UninstallCommand(string packageFullName) =>
        "powershell.exe -NoProfile -Command \"Remove-AppxPackage -Package "
        + $"'{packageFullName}'\"";

    private static string[] SafeSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception)
        {
            return [];
        }
    }

    // Classic DllImport rather than the LibraryImport generator, which cannot marshal a
    // StringBuilder. The same reason RestartManager uses it.
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(
        string source, StringBuilder outBuffer, int outBufferSize, nint reserved);
}
