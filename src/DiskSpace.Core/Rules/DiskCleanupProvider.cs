using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;
using DiskSpace.Core.Safety;
using Microsoft.Win32;

namespace DiskSpace.Core.Rules;

/// <summary>
/// Everything Windows Disk Cleanup offers, by construction rather than by imitation: this reads
/// the same registry list Disk Cleanup reads and hosts the same handlers
/// (<see cref="DiskCleanupHandler"/>). That covers the things only Windows can remove safely,
/// such as superseded Windows Update components, a previous Windows installation and old
/// driver packages, and it picks up a handler added by a later Windows version without a
/// change here.
///
/// What this class adds is the part Disk Cleanup leaves out: a plain statement of what each
/// one costs you, and a risk level.
/// </summary>
public sealed class DiskCleanupProvider : IRuleProvider
{
    public string Name => "Windows Disk Cleanup";

    /// <summary>
    /// Never offered, whatever the registry says. Disk Cleanup can empty the Downloads folder;
    /// this tool treats that folder as the user's own files, the same as Documents.
    /// </summary>
    private static readonly HashSet<string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        "DownloadsFolder",
    };

    /// <summary>
    /// Handlers left to a rule of this tool's own, which lists the same data with its exact
    /// path and file count. Offering both would count the same bytes twice.
    /// </summary>
    private static readonly HashSet<string> CoveredByOwnRule = new(StringComparer.OrdinalIgnoreCase)
    {
        "D3D Shader Cache",
        "Internet Cache Files",
        "Thumbnail Cache",

        // Covered more thoroughly: the temp rules take what has gone unused for a day, where
        // this handler waits a week. It also sweeps old files out of two Windows log folders,
        // which is the one thing given up by leaving it out.
        "Temporary Files",
    };

    private sealed record Known(
        string Name, RiskLevel Risk, string Description, string WhatBreaks, string? Location = null);

    private static readonly Dictionary<string, Known> Catalog = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Update Cleanup"] = new(
            "Windows Update cleanup",
            RiskLevel.Advanced,
            "Older versions of system components that Windows Update has replaced and kept in "
            + "the component store.",
            "Updates that are already installed can no longer be uninstalled. Nothing else "
            + "changes. Windows does the removing itself, which can take several minutes.",
            @"{windows}\WinSxS"),

        ["Previous Installations"] = new(
            "Previous Windows installation",
            RiskLevel.Advanced,
            "The Windows.old folder left behind by an upgrade or an in-place reinstall. It "
            + "holds the previous system, its programs and its user folders.",
            "You can no longer go back to the previous version of Windows, and any file you "
            + "still wanted from the old installation goes with it.",
            @"{volume}Windows.old"),

        ["Temporary Setup Files"] = new(
            "Temporary Windows installation files",
            RiskLevel.Safe,
            "Files Windows Setup downloaded and unpacked in order to install an upgrade.",
            "Nothing once the upgrade has finished. If a feature update is downloaded and "
            + "waiting for a restart, Windows downloads it again."),

        ["Windows ESD installation files"] = new(
            "Windows ESD installation files",
            RiskLevel.Advanced,
            "The compressed Windows image kept after installation.",
            "Resetting or refreshing this PC may ask for installation media, because this is "
            + "the copy Windows would have used."),

        ["Windows Upgrade Log Files"] = new(
            "Windows upgrade log files",
            RiskLevel.Safe,
            "Logs written while Windows was installed or upgraded.",
            "Nothing, unless you are working out why an upgrade failed."),

        ["Upgrade Discarded Files"] = new(
            "Files discarded by Windows upgrade",
            RiskLevel.Review,
            "Files from the previous installation that the upgrade did not carry over and set "
            + "aside instead.",
            "Possibly something of yours. The upgrade keeps what it could not migrate, and "
            + "that can include personal files, so look before removing it."),

        ["Device Driver Packages"] = new(
            "Old device driver packages",
            RiskLevel.Advanced,
            "Earlier versions of device drivers, kept in the driver store after a newer "
            + "version was installed.",
            "A device can no longer be rolled back to its previous driver. Drivers in use "
            + "are kept.",
            @"{windows}\System32\DriverStore\FileRepository"),

        ["Delivery Optimization Files"] = new(
            "Delivery Optimization files",
            RiskLevel.Safe,
            "Update files cached so that this PC can pass them on to others.",
            "Nothing locally; other machines on the network lose a nearby source.",
            @"{windows}\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache"),

        ["Recycle Bin"] = new(
            "Recycle Bin",
            RiskLevel.Review,
            "Files you deleted that are still being held in the Recycle Bin on this drive.",
            "Everything in the Recycle Bin is gone for good. It is the one place a deleted "
            + "file can still be restored from, so check it before emptying it.",
            @"{volume}$Recycle.Bin"),

        ["System error memory dump files"] = new(
            "System crash memory dump",
            RiskLevel.Safe,
            "The memory dump Windows wrote the last time the system itself crashed.",
            "Nothing, unless you are investigating that crash. A dump cannot be recreated "
            + "after the fact."),

        ["System error minidump files"] = new(
            "System crash minidumps",
            RiskLevel.Safe,
            "The small dumps Windows writes each time the system crashes.",
            "Nothing, unless you are investigating those crashes. A dump cannot be recreated "
            + "after the fact."),

        ["Windows Error Reporting Files"] = new(
            "Windows Error Reporting (system)",
            RiskLevel.Safe,
            "Machine-wide error reports and the diagnostics attached to them.",
            "Nothing. Reports that have not been sent to Microsoft yet are dropped."),

        ["Windows Defender"] = new(
            "Microsoft Defender non-critical files",
            RiskLevel.Safe,
            "Support logs and local copies kept by Microsoft Defender Antivirus.",
            "Nothing. Protection and its definitions are not touched."),

        ["Setup Log Files"] = new(
            "Setup log files",
            RiskLevel.Safe,
            "Old log files written by Windows Setup.",
            "Nothing, unless you are diagnosing an installation problem."),

        ["Downloaded Program Files"] = new(
            "Downloaded program files",
            RiskLevel.Safe,
            "ActiveX controls and Java applets that Internet Explorer downloaded.",
            "Nothing. A page that still needs one downloads it again.",
            @"{windows}\Downloaded Program Files"),

        ["Offline Pages Files"] = new(
            "Offline web pages",
            RiskLevel.Safe,
            "Web pages Internet Explorer stored so they could be read without a connection.",
            "Those pages need a connection again until they are next synchronized."),

        ["Active Setup Temp Folders"] = new(
            "Old setup temp folders",
            RiskLevel.Safe,
            "Temporary files left by a setup program that is no longer running.",
            "Nothing."),

        ["Content Indexer Cleaner"] = new(
            "Old search index catalogs",
            RiskLevel.Safe,
            "Catalog files left behind by an earlier version of Windows search indexing.",
            "Nothing. Current versions of Windows do not use them."),

        ["Old ChkDsk Files"] = new(
            "Recovered file fragments",
            RiskLevel.Review,
            "Pieces of files that a disk check salvaged into FOUND.000-style folders.",
            "Usually nothing, but these fragments are all that is left of whatever was "
            + "damaged. If files went missing after a disk error, look here first."),

        ["Diagnostic Data Viewer database files"] = new(
            "Diagnostic Data Viewer database",
            RiskLevel.Safe,
            "The local copy of diagnostic data kept for the Diagnostic Data Viewer app.",
            "Nothing. The viewer starts over with an empty history."),

        ["RetailDemo Offline Content"] = new(
            "Retail demo content",
            RiskLevel.Safe,
            "Demonstration content used when a PC is on display in a store.",
            "Nothing, unless this PC is a store display unit."),

        ["Language Pack"] = new(
            "Unused language resources",
            RiskLevel.Advanced,
            "Resource files for display languages, keyboards and speech that are not in use.",
            "A removed language has to be downloaded again before you can switch to it."),

        ["User file versions"] = new(
            "File History staging copies",
            RiskLevel.Advanced,
            "Versions of your files held on this disk until File History copies them to its "
            + "backup drive.",
            "Versions that have not reached the backup drive yet are lost, leaving gaps in "
            + "your file history."),

        ["BranchCache"] = new(
            "BranchCache files",
            RiskLevel.Safe,
            "Content cached by BranchCache to save bandwidth on a corporate network.",
            "Nothing locally. The content is fetched from the server again when needed."),

        ["Feedback Hub Archive log files"] = new(
            "Feedback Hub archive logs",
            RiskLevel.Safe,
            "Diagnostic logs the Feedback Hub collected for feedback you sent.",
            "Nothing."),
    };

    public IEnumerable<CleanupRule> GetRules()
    {
        using var root = Registry.LocalMachine.OpenSubKey(DiskCleanupHandler.VolumeCachesPath);
        if (root is null)
            yield break;

        var guard = new PathGuard();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemVolume = Path.GetPathRoot(windows) ?? @"C:\";
        var volumes = FixedVolumes(systemVolume);

        foreach (var keyName in root.GetSubKeyNames())
        {
            if (Refused.Contains(keyName) || CoveredByOwnRule.Contains(keyName))
                continue;

            using var key = root.OpenSubKey(keyName);
            if (key is null)
                continue;

            var folders = DeclaredFolders(key);

            // Disk Cleanup runs each handler once per drive, and so does this. A handler with
            // nothing on a drive says so when it is measured.
            foreach (var volume in volumes)
            {
                var onVolume = folders
                    .Select(f => OnVolume(f, volume))
                    .Where(f => f.StartsWith(volume, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // It names its folders and none of them are on this drive.
                if (folders.Count > 0 && onVolume.Count == 0)
                    continue;

                // A handler that names its folders is held to the same line as every other
                // rule: the user's own files are not a cache.
                if (onVolume.Any(guard.TouchesUserData))
                    continue;

                yield return BuildRule(keyName, key, volume, systemVolume, windows, onVolume);
            }
        }
    }

    private static CleanupRule BuildRule(
        string keyName,
        RegistryKey key,
        string volume,
        string systemVolume,
        string windows,
        List<string> folders)
    {
        var drive = volume.TrimEnd(Path.DirectorySeparatorChar);
        var isSystemVolume = string.Equals(volume, systemVolume, StringComparison.OrdinalIgnoreCase);

        Catalog.TryGetValue(keyName, out var known);

        var name = known?.Name ?? keyName;

        var location = known?.Location is { } template
            ? template.Replace("{windows}", windows).Replace("{volume}", volume)
            : folders.Count > 0
                ? string.Join("; ", folders)
                : $"Chosen by Windows (Disk Cleanup handler \"{keyName}\") on {drive}";

        return new CleanupRule
        {
            Id = $"cleanmgr.{Slug(keyName)}.{char.ToLowerInvariant(volume[0])}",

            // The other drives are named; the system drive, where nearly all of this lives,
            // is not.
            Name = isSystemVolume ? name : $"{name} ({drive})",
            Category = WindowsCacheProvider.Category,
            Risk = known?.Risk ?? RiskLevel.Review,
            Description = known?.Description ?? UnknownDescription(key),
            WhatBreaks = known?.WhatBreaks
                ?? "DiskSpace has no knowledge of its own about this one. Windows Disk Cleanup "
                + $"lists it as \"{keyName}\", and the component that registered it decides "
                + "what is removed.",
            Root = location,
            Targets = [],
            RequiresElevation = true,
            Cleaner = new DiskCleanupHandler(keyName, volume),
        };
    }

    /// <summary>
    /// Whatever the handler says about itself in the registry, when that is plain text. Most
    /// descriptions are references into a resource DLL and are skipped rather than resolved.
    /// </summary>
    private static string UnknownDescription(RegistryKey key)
    {
        const string Fallback = "A cleanup another component has registered with Windows Disk Cleanup.";

        return key.GetValue("Description") is string text
               && !string.IsNullOrWhiteSpace(text)
               && !text.StartsWith('@')
            ? text.Trim()
            : Fallback;
    }

    /// <summary>
    /// The folders a data-driven handler lists in its registration. Handlers with code of
    /// their own usually list none.
    /// </summary>
    private static List<string> DeclaredFolders(RegistryKey key)
    {
        IEnumerable<string> raw = key.GetValue("Folder") switch
        {
            string text => text.Split('|'),
            string[] many => many.SelectMany(m => m.Split('|')),
            _ => [],
        };

        return [.. raw.Select(f => f.Trim()).Where(f => f.Length > 0)];
    }

    /// <summary>Resolves the "any drive" form, <c>?:\FOUND.000</c>, against one drive.</summary>
    private static string OnVolume(string folder, string volume) =>
        folder.StartsWith("?:", StringComparison.Ordinal) ? volume[..1] + folder[1..] : folder;

    private static List<string> FixedVolumes(string systemVolume)
    {
        var volumes = new List<string> { systemVolume };

        try
        {
            volumes.AddRange(DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.Name)
                .Where(n => !string.Equals(n, systemVolume, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception)
        {
            // Drives that cannot be listed are not offered; the system volume still is.
        }

        return volumes;
    }

    private static string Slug(string keyName) =>
        new([.. keyName.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]);
}
