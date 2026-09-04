using DiskSpace.Core.Model;

namespace DiskSpace.Core.Programs;

/// <summary>
/// Programs Windows has no record of at all.
///
/// A developer machine accumulates these faster than anything else: per-user installers that
/// skip the uninstall registry, package managers that unpack into the profile, and archives
/// somebody extracted. They are invisible to Add/Remove Programs, which is exactly why they are
/// worth listing here.
/// </summary>
public sealed class UserInstallProgramProvider : IProgramProvider
{
    public string Name => "User-directory apps";

    public IEnumerable<InstalledProgram> GetPrograms()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var program in FromDirectories(
                     Path.Combine(local, "Programs"), "per-user install", null))
        {
            yield return program;
        }

        foreach (var program in FromDirectories(ScoopApps(), "scoop", "scoop uninstall {0}"))
            yield return program;

        foreach (var program in FromDirectories(
                     Path.Combine(profile, ".dotnet", "tools", ".store"),
                     ".NET global tool",
                     "dotnet tool uninstall --global {0}"))
        {
            yield return program;
        }

        foreach (var program in FromDirectories(
                     Path.Combine(local, "pipx", "venvs"), "pipx", "pipx uninstall {0}"))
        {
            yield return program;
        }

        foreach (var program in FromDirectories(
                     Path.Combine(roaming, "npm", "node_modules"),
                     "npm global package",
                     "npm uninstall -g {0}"))
        {
            yield return program;
        }
    }

    private static string ScoopApps()
    {
        // Scoop installs into the profile and registers nothing with Windows, so it is invisible
        // to every other source here.
        var root = Environment.GetEnvironmentVariable("SCOOP")
                   ?? Path.Combine(
                       Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop");

        return Path.Combine(root, "apps");
    }

    private static IEnumerable<InstalledProgram> FromDirectories(
        string parent, string kind, string? uninstallFormat)
    {
        foreach (var directory in SafeDirectories(parent))
        {
            var name = Path.GetFileName(directory);

            // npm keeps its own bookkeeping alongside the packages it installed.
            if (name.StartsWith('.'))
                continue;

            yield return new InstalledProgram
            {
                Id = "user:" + directory.ToLowerInvariant(),
                Name = name,
                Source = ProgramSource.UserInstall,
                Risk = RiskLevel.Review,
                Locations = [new ProgramLocation(directory, LocationKind.Install)],
                Publisher = null,
                UninstallCommand = uninstallFormat is null
                    ? null
                    : string.Format(System.Globalization.CultureInfo.InvariantCulture, uninstallFormat, name),
                Note = $"Found as a {kind}. Windows has no uninstall entry for it, so it does not "
                       + "appear in Add/Remove Programs.",
            };
        }
    }

    private static IEnumerable<string> SafeDirectories(string parent)
    {
        try
        {
            return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent) : [];
        }
        catch (Exception)
        {
            // Not installed, or unreadable. Either way it contributes nothing.
            return [];
        }
    }
}
