using DiskSpace.Core.Model;

namespace DiskSpace.Core.Rules;

/// <summary>
/// Large things this tool will not delete, surfaced anyway so the disk arithmetic adds up.
///
/// Without these, a user reclaims every cache on the machine and still cannot account for
/// tens of gigabytes. Each finding names the correct way to deal with it — which is never
/// "delete the file", and in several cases would break the system if it were.
/// </summary>
public sealed class LargeItemProvider : IRuleProvider
{
    public string Name => "Large items (report only)";

    private const string Category = "Large items (not removed)";

    public IEnumerable<CleanupRule> GetRules()
    {
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
                     ?? @"C:\";
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Report(
            "hiberfil",
            "Hibernation file",
            Path.Combine(system, "hiberfil.sys"),
            "Memory contents saved when the machine hibernates, sized after installed RAM.",
            "Deleting this file directly is not possible and would break hibernation and fast "
            + "startup. To reclaim it, disable hibernation: run `powercfg /hibernate off` from "
            + "an elevated prompt.");

        yield return Report(
            "pagefile",
            "Page file",
            Path.Combine(system, "pagefile.sys"),
            "Virtual memory backing store.",
            "Never delete this. Resize or relocate it through System Properties → Advanced → "
            + "Performance → Virtual memory. Moving it to another volume frees this one.");

        yield return Report(
            "swapfile",
            "Swap file",
            Path.Combine(system, "swapfile.sys"),
            "Backing store for suspended Store applications.",
            "Managed by Windows alongside the page file; it is not separately removable.");

        foreach (var vhdx in DockerAndWslDisks(local))
            yield return vhdx;
    }

    private static IEnumerable<CleanupRule> DockerAndWslDisks(string local)
    {
        var packages = Path.Combine(local, "Packages");
        var candidates = new List<string>();

        // WSL distributions store their whole filesystem in one sparse virtual disk.
        if (Directory.Exists(packages))
        {
            try
            {
                candidates.AddRange(
                    Directory.EnumerateFiles(packages, "ext4.vhdx", SearchOption.AllDirectories));
            }
            catch (Exception)
            {
                // Unreadable package store; skip.
            }
        }

        foreach (var relative in new[]
                 {
                     Path.Combine("Docker", "wsl", "data", "ext4.vhdx"),
                     Path.Combine("Docker", "wsl", "disk", "docker_data.vhdx"),
                     Path.Combine("DockerDesktop", "vm-data", "DockerDesktop.vhdx"),
                 })
        {
            var path = Path.Combine(local, relative);
            if (File.Exists(path))
                candidates.Add(path);
        }

        foreach (var path in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Report(
                $"vhdx.{Path.GetFileNameWithoutExtension(path)}.{path.GetHashCode():x8}",
                $"Virtual disk: {Path.GetFileName(path)}",
                path,
                "A WSL or Docker virtual disk. These grow as data is written and do not shrink "
                + "when it is deleted, so this file is often far larger than its contents.",
                "Deleting it destroys that distribution or all Docker data. Reclaim space "
                + "inside it instead (`docker system prune -a` for Docker), then compact the "
                + "disk with `wsl --manage <distro> --set-sparse true`, or `Optimize-VHD "
                + "-Mode Full` on a stopped VM.");
        }
    }

    private static CleanupRule Report(
        string id, string name, string path, string description, string whatBreaks) => new()
        {
            Id = $"large.{id}",
            Name = name,
            Category = Category,
            Risk = RiskLevel.ReportOnly,
            Description = description,
            WhatBreaks = whatBreaks,
            Root = path,
            Targets = [path],
        };
}
