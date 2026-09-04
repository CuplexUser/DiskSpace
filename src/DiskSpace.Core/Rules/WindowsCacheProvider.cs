using DiskSpace.Core.Model;

namespace DiskSpace.Core.Rules;

/// <summary>
/// Windows' own caches and temporary directories. Everything here that touches machine-wide
/// state is marked as needing elevation, and the genuinely consequential entries — Prefetch,
/// the component store — are rated Advanced rather than Safe.
/// </summary>
public sealed class WindowsCacheProvider : IRuleProvider
{
    public string Name => "Windows caches";

    private const string Category = "Windows and system";

    public IEnumerable<CleanupRule> GetRules()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userTemp = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        yield return new CleanupRule
        {
            Id = "win.user-temp",
            Name = "Temporary files (user)",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "Files left in your temp directory by installers and applications.",
            WhatBreaks =
                "Nothing, as long as no installer is mid-run. Files still in use are skipped "
                + "and reported rather than forced.",
            Root = userTemp,
            Targets = [userTemp],
            // Anything touched in the last day may belong to something still running.
            MinimumAge = TimeSpan.FromDays(1),
        };

        yield return new CleanupRule
        {
            Id = "win.machine-temp",
            Name = "Temporary files (system)",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "The machine-wide temp directory.",
            WhatBreaks = "Nothing. Files held open by a running process are skipped.",
            Root = Path.Combine(windows, "Temp"),
            Targets = [Path.Combine(windows, "Temp")],
            MinimumAge = TimeSpan.FromDays(1),
            RequiresElevation = true,
        };

        yield return new CleanupRule
        {
            Id = "win.update-download",
            Name = "Windows Update downloads",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "Installer payloads Windows Update has already applied.",
            WhatBreaks =
                "Nothing. Windows re-downloads anything it still needs. Often several "
                + "gigabytes on a machine that has been upgraded in place.",
            Root = Path.Combine(windows, "SoftwareDistribution", "Download"),
            Targets = [Path.Combine(windows, "SoftwareDistribution", "Download")],
            RequiresElevation = true,
        };

        yield return new CleanupRule
        {
            Id = "win.delivery-optimization",
            Name = "Delivery Optimization files",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "Update chunks cached for peer-to-peer distribution.",
            WhatBreaks = "Nothing locally; other machines on the network lose a nearby source.",
            Root = Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization"),
            Targets = [Path.Combine(windows, "SoftwareDistribution", "DeliveryOptimization")],
            RequiresElevation = true,
        };

        yield return new CleanupRule
        {
            Id = "win.crash-dumps",
            Name = "Crash dumps",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "Memory dumps written when an application crashed.",
            WhatBreaks =
                "Nothing, unless you are actively debugging one of these crashes — a dump "
                + "cannot be regenerated after the fact.",
            Root = Path.Combine(local, "CrashDumps"),
            Targets = [Path.Combine(local, "CrashDumps")],
        };

        yield return new CleanupRule
        {
            Id = "win.wer",
            Name = "Windows Error Reporting queue",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "Error reports queued for or already sent to Microsoft.",
            WhatBreaks = "Nothing.",
            Root = Path.Combine(local, "Microsoft", "Windows", "WER"),
            Targets = [Path.Combine(local, "Microsoft", "Windows", "WER")],
        };

        yield return new CleanupRule
        {
            Id = "win.thumbnails",
            Name = "Thumbnail and icon cache",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "Explorer's cached thumbnails and icons.",
            WhatBreaks =
                "Nothing. Explorer rebuilds them, so folders of images redraw slowly once.",
            Root = Path.Combine(local, "Microsoft", "Windows", "Explorer"),
            Targets = [Path.Combine(local, "Microsoft", "Windows", "Explorer")],
        };

        yield return new CleanupRule
        {
            Id = "win.inetcache",
            Name = "Windows internet cache",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "The shared WinINet cache used by Windows components.",
            WhatBreaks = "Nothing; it refills as components fetch content.",
            Root = Path.Combine(local, "Microsoft", "Windows", "INetCache"),
            Targets = [Path.Combine(local, "Microsoft", "Windows", "INetCache")],
        };

        yield return new CleanupRule
        {
            Id = "win.d3d-shader",
            Name = "D3D shader cache",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "Compiled Direct3D shaders.",
            WhatBreaks = "Nothing. Games and apps recompile shaders, stuttering briefly once.",
            Root = Path.Combine(local, "D3DSCache"),
            Targets = [Path.Combine(local, "D3DSCache")],
        };

        yield return new CleanupRule
        {
            Id = "win.prefetch",
            Name = "Prefetch data",
            Category = Category,
            Risk = RiskLevel.Advanced,
            Description = "Windows' record of what each program loads at startup.",
            WhatBreaks =
                "Application launches get slower until Windows relearns the pattern. This "
                + "reclaims very little and is rarely worth doing.",
            Root = Path.Combine(windows, "Prefetch"),
            Targets = [Path.Combine(windows, "Prefetch")],
            RequiresElevation = true,
        };

        yield return new CleanupRule
        {
            Id = "win.cbs-logs",
            Name = "Component servicing logs",
            Category = Category,
            Risk = RiskLevel.Advanced,
            Description = "CBS logs written during Windows servicing operations.",
            WhatBreaks =
                "Diagnostic history for update failures is lost. Harmless unless you are "
                + "investigating a servicing problem right now.",
            Root = Path.Combine(windows, "Logs", "CBS"),
            Targets = [Path.Combine(windows, "Logs", "CBS")],
            RequiresElevation = true,
        };
    }
}
