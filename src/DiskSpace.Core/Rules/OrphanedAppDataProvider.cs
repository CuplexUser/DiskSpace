using DiskSpace.Core.Model;

namespace DiskSpace.Core.Rules;

/// <summary>
/// Leftover configuration and data from software that is no longer installed.
///
/// This is the fuzziest detector in the catalog and the only one whose findings are quarantined
/// rather than deleted outright. Matching a folder name against installed software is a
/// heuristic: a miss looks exactly like a hit, and the damage shows up weeks later when a tool
/// will not start and its licence token is gone. So every finding here is rated
/// <see cref="RiskLevel.Review"/>, is never auto-selected, and carries a note explaining
/// what the evidence actually was.
/// </summary>
public sealed class OrphanedAppDataProvider : IRuleProvider
{
    public string Name => "Orphaned application data";

    private const string Category = "Orphaned application data";

    /// <summary>
    /// Folders that belong to Windows itself or are shared infrastructure. These have no
    /// uninstall entry and would otherwise look abandoned to a name-matching heuristic.
    /// </summary>
    private static readonly HashSet<string> NeverOrphaned = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Windows", "WindowsApps", "Packages", "Temp", "Temporary Internet Files",
        "Programs", "Package Cache", "PackageManagement", "Publishers", "ConnectedDevicesPlatform",
        "Comms", "IdentityNexusIntegration", ".IdentityService", "PlaceholderTileLogoFolder",
        "Application Data", "History", "IconCache.db", "ElevatedDiagnostics", "VirtualStore",
        "CrashDumps", "D3DSCache", "DWriteCore", "Downloaded Installations", "GroupPolicy",
        "Google", "Mozilla", "NVIDIA", "NVIDIA Corporation", "Intel", "AMD", "Adobe",
        "OneDrive", "Steam", "Docker", "SquirrelTemp", "Local", "LocalLow", "Roaming",
        ".thumbnails", ".cache", ".config", ".local", ".dotnet", ".nuget", ".ssh",
    };

    public IEnumerable<CleanupRule> GetRules()
    {
        var installed = InstalledSoftware.Load();
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            profile,
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)))
        {
            // Only dot-directories are considered directly in the profile; everything else
            // there is a known folder or user data.
            var dotOnly = string.Equals(root, profile, StringComparison.OrdinalIgnoreCase);

            foreach (var rule in Candidates(root, installed, dotOnly))
                yield return rule;
        }
    }

    private static IEnumerable<CleanupRule> Candidates(
        string root, InstalledSoftware installed, bool dotDirectoriesOnly)
    {
        IEnumerable<string> directories;

        try
        {
            directories = Directory.EnumerateDirectories(root);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var directory in directories)
        {
            var name = Path.GetFileName(directory);

            if (dotDirectoriesOnly && !name.StartsWith('.'))
                continue;

            if (NeverOrphaned.Contains(name))
                continue;

            // A reparse point is a pointer, not the data; leave it alone.
            if (IsReparsePoint(directory))
                continue;

            if (installed.Matches(name))
                continue;

            if (LastActivityUtc(directory) is not { } lastActivity)
                continue;

            var idleDays = (int)(DateTime.UtcNow - lastActivity).TotalDays;

            yield return new CleanupRule
            {
                Id = $"orphan.{name.ToLowerInvariant()}",
                Name = name,
                Category = Category,
                Risk = RiskLevel.Review,
                Description =
                    $"Left in {Path.GetFileName(root)} by software that no longer appears to "
                    + "be installed.",
                WhatBreaks = Evidence(name, idleDays),
                Root = directory,
                Targets = [directory],
                // The folder itself goes: leaving an empty shell behind helps nobody.
                RemoveTargetDirectory = true,
                // Folders of a few kilobytes are not worth a decision from anyone.
                MinimumSize = 1024 * 1024,
            };
        }
    }

    /// <summary>
    /// States the evidence rather than a verdict.
    ///
    /// Recent activity is reported instead of used as a filter. An earlier version hid anything
    /// touched in the last 90 days, which silently dropped real orphans whose folder had been
    /// brushed by a stray write — including the exact leftovers this detector exists to find.
    /// Showing the candidate with its idle time lets the person decide; hiding it does not.
    /// </summary>
    private static string Evidence(string name, int idleDays)
    {
        var activity = idleDays switch
        {
            <= 1 => "it was written to today, which argues something still uses it",
            < 30 => $"it was last written {idleDays} days ago, so something may still use it",
            < 180 => $"it has not been touched in {idleDays} days",
            _ => $"it has not been touched in over {idleDays / 30} months",
        };

        return $"If \"{name}\" is in fact still installed, it loses its settings and any sign-in "
               + $"or license stored here. Nothing on this machine claims this folder, and "
               + $"{activity}. That is inference, not proof, which is why this is quarantined "
               + "rather than deleted, and can be restored.";
    }

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return new DirectoryInfo(directory).Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            return true; // Cannot tell; assume it is and leave it alone.
        }
    }

    /// <summary>
    /// The most recent write anywhere in the folder — not just on the folder itself, whose
    /// timestamp only reflects direct children being added or removed.
    /// </summary>
    private static DateTime? LastActivityUtc(string directory)
    {
        try
        {
            var latest = Directory.GetLastWriteTimeUtc(directory);
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };

            var inspected = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                // Bounded: this runs for every candidate folder, and the newest few hundred
                // files are enough to tell "actively used" from "abandoned".
                if (++inspected > 500)
                    break;

                var written = File.GetLastWriteTimeUtc(file);
                if (written > latest)
                    latest = written;
            }

            return latest;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
