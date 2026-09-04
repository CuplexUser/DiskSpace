using Microsoft.Win32;

namespace DiskSpace.Core.Rules;

/// <summary>
/// What is installed on this machine, gathered from every place Windows records it: the
/// uninstall registry (both hives, both registry views), per-user program directories, Start
/// Menu shortcut targets, and installed MSIX packages.
///
/// Breadth matters more than precision here. This set is used to decide that a leftover folder
/// is <em>not</em> associated with anything installed, so a missed entry produces a false
/// accusation against a folder that is actually in use.
/// </summary>
public sealed class InstalledSoftware
{
    private readonly HashSet<string> _tokens = new(StringComparer.OrdinalIgnoreCase);

    private InstalledSoftware()
    {
    }

    public static InstalledSoftware Load()
    {
        var software = new InstalledSoftware();

        software.AddUninstallEntries(RegistryHive.LocalMachine, RegistryView.Registry64);
        software.AddUninstallEntries(RegistryHive.LocalMachine, RegistryView.Registry32);
        software.AddUninstallEntries(RegistryHive.CurrentUser, RegistryView.Default);
        software.AddMsixPackages();
        software.AddProgramDirectories();
        software.AddStartMenuShortcuts();
        software.AddScoopApps();
        software.AddPathExecutables();

        return software;
    }

    /// <summary>Every recorded name, for display and debugging.</summary>
    public IReadOnlyCollection<string> Tokens => _tokens;

    /// <summary>
    /// True when a folder name plausibly belongs to installed software.
    ///
    /// The two containment directions are not symmetric, and treating them as though they were
    /// is what makes this kind of matcher useless. When an installed name *contains* the folder
    /// name, the installed name is simply more specific — "Logitech G HUB" covers a "G HUB"
    /// folder — and that is reliable. The reverse is treacherous: a folder named ".codex" was
    /// being credited to an installed "code", because one is a prefix of the other and they are
    /// unrelated programs. So that direction demands a substantial token that accounts for most
    /// of the folder name.
    /// </summary>
    public bool Matches(string folderName)
    {
        foreach (var candidate in NameForms(folderName))
        {
            if (MatchesExactForm(candidate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The folder name, plus the same name with tooling suffixes removed. Node and Python CLIs
    /// scatter directories like "claude-cli-nodejs" and "prisma-dev-nodejs" that belong to a
    /// tool recorded under its bare name.
    /// </summary>
    private static IEnumerable<string> NameForms(string folderName)
    {
        yield return folderName;

        var trimmed = folderName.TrimStart('.');
        if (!string.Equals(trimmed, folderName, StringComparison.Ordinal))
            yield return trimmed;

        foreach (var suffix in new[] { "-nodejs", "-cli", "-cache", "-state", "-tray", "-client" })
        {
            while (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed[..^suffix.Length];
        }

        if (trimmed.Length >= 3 && !string.Equals(trimmed, folderName, StringComparison.Ordinal))
            yield return trimmed;
    }

    private bool MatchesExactForm(string folderName)
    {
        var folder = Normalize(folderName);
        if (folder.Length < 3)
            return true; // Too short to judge; treat as in use.

        foreach (var token in _tokens)
        {
            var installed = Normalize(token);
            if (installed.Length < 3)
                continue;

            if (installed.Equals(folder, StringComparison.Ordinal))
                return true;

            // Installed name is the more specific one: "claudecode" covers "claude".
            if (folder.Length >= 4 && installed.Contains(folder, StringComparison.Ordinal))
                return true;

            // Folder is the more specific one. Only credible when the installed name is
            // substantial and accounts for at least 70% of the folder name, which keeps
            // "code" from claiming "codex" while letting "pgadmin" claim "pgadmin4".
            if (installed.Length >= 6
                && folder.Contains(installed, StringComparison.Ordinal)
                && installed.Length * 10 >= folder.Length * 7)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reduces a name to comparable form: lower case, no spaces, punctuation or version noise,
    /// so "Node.js", "node js" and "nodejs" all collapse together.
    /// </summary>
    private static string Normalize(string value)
    {
        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
                buffer[length++] = char.ToLowerInvariant(c);
        }

        return new string(buffer[..length]);
    }

    private void Add(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            _tokens.Add(value.Trim());
    }

    private void AddUninstallEntries(RegistryHive hive, RegistryView view)
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(path);
            if (uninstall is null)
                return;

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                using var entry = uninstall.OpenSubKey(subKeyName);
                if (entry is null)
                    continue;

                Add(entry.GetValue("DisplayName") as string);
                Add(entry.GetValue("Publisher") as string);

                // The install directory's own name is often what the AppData folder is called.
                if (entry.GetValue("InstallLocation") as string is { Length: > 0 } location)
                    Add(SafeFileName(location));

                // A key named like a GUID tells us nothing; anything else might.
                if (!subKeyName.StartsWith('{'))
                    Add(subKeyName);
            }
        }
        catch (Exception)
        {
            // An unreadable hive costs coverage, not correctness of the rest.
        }
    }

    private void AddMsixPackages()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");

            if (key is null)
                return;

            foreach (var packageName in key.GetSubKeyNames())
            {
                // Winget's source packages index everything winget could install, not what is
                // installed. Treating them as evidence made every catalogued name look present.
                if (packageName.Contains("Winget.Source", StringComparison.OrdinalIgnoreCase))
                    continue;

                // "Microsoft.WindowsTerminal_1.18.3181.0_x64__8wekyb3d8bbwe" -> the family name.
                var family = packageName.Split('_')[0];
                Add(family);

                foreach (var part in family.Split('.'))
                    Add(part);
            }
        }
        catch (Exception)
        {
            // Package repository unreadable; skip.
        }
    }

    private void AddProgramDirectories()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(
                         Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Programs"),
                 })
        {
            if (string.IsNullOrEmpty(root))
                continue;

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                    Add(Path.GetFileName(directory));
            }
            catch (Exception)
            {
                // Unreadable program directory; skip it.
            }
        }
    }

    private void AddStartMenuShortcuts()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                 })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                continue;

            try
            {
                foreach (var shortcut in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    Add(Path.GetFileNameWithoutExtension(shortcut));
            }
            catch (Exception)
            {
                // Unreadable Start Menu; skip it.
            }
        }
    }

    /// <summary>
    /// Scoop installs into the profile and registers nothing with Windows, so its apps are
    /// invisible to every other source here.
    /// </summary>
    private void AddScoopApps()
    {
        var scoop = Environment.GetEnvironmentVariable("SCOOP")
                    ?? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop");

        try
        {
            var apps = Path.Combine(scoop, "apps");
            if (!Directory.Exists(apps))
                return;

            foreach (var app in Directory.EnumerateDirectories(apps))
                Add(Path.GetFileName(app));
        }
        catch (Exception)
        {
            // No scoop installation, or it is unreadable.
        }
    }

    /// <summary>
    /// Anything runnable on PATH counts as installed. This catches tools that were unzipped or
    /// installed by a language package manager and never registered anywhere.
    /// </summary>
    private void AddPathExecutables()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            return;

        var options = new EnumerationOptions { IgnoreInaccessible = true };

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (!Directory.Exists(directory))
                    continue;

                foreach (var file in Directory.EnumerateFiles(directory, "*", options))
                {
                    // Case-insensitively: shims are often written as .CMD, and missing those
                    // makes genuinely installed tools look abandoned.
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension is ".exe" or ".cmd" or ".bat" or ".ps1")
                        Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch (Exception)
            {
                // A PATH entry that cannot be listed contributes nothing.
            }
        }
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
