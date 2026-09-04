using DiskSpace.Core.Model;

namespace DiskSpace.Core.Rules;

/// <summary>
/// Browser and Electron application caches.
///
/// Chromium-based apps all share a profile layout, so one set of subdirectory names covers
/// Chrome, Edge, and every Electron app on the machine. The names matter enormously: <c>Cache</c>
/// and <c>GPUCache</c> are disposable, while their siblings <c>Local Storage</c>,
/// <c>IndexedDB</c> and <c>Cookies</c> hold logins and application data. Only the disposable
/// ones are ever named here.
/// </summary>
public sealed class BrowserAndElectronProvider : IRuleProvider
{
    public string Name => "Browser and Electron caches";

    private const string Category = "Browser and app caches";

    /// <summary>Subdirectories of a Chromium profile that regenerate on demand.</summary>
    private static readonly string[] DisposableCacheDirectories =
    [
        "Cache",
        "Code Cache",
        "GPUCache",
        "DawnCache",
        "DawnGraphiteCache",
        "DawnWebGPUCache",
        "ShaderCache",
        "GrShaderCache",
        "component_crx_cache",
        Path.Combine("Service Worker", "CacheStorage"),
        Path.Combine("Service Worker", "ScriptCache"),
    ];

    public IEnumerable<CleanupRule> GetRules()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var rule in ChromiumBrowser(
                     "chrome", "Google Chrome", Path.Combine(local, "Google", "Chrome", "User Data")))
        {
            yield return rule;
        }

        foreach (var rule in ChromiumBrowser(
                     "edge", "Microsoft Edge", Path.Combine(local, "Microsoft", "Edge", "User Data")))
        {
            yield return rule;
        }

        foreach (var rule in FirefoxProfiles(roaming, local))
            yield return rule;

        foreach (var rule in ElectronApps(roaming))
            yield return rule;
    }

    private static IEnumerable<CleanupRule> ChromiumBrowser(string id, string name, string userData)
    {
        if (!Directory.Exists(userData))
            yield break;

        // Each browser profile ("Default", "Profile 1", …) carries its own cache directories.
        var targets = new List<string>();

        foreach (var profile in EnumerateDirectories(userData))
        {
            var profileName = Path.GetFileName(profile);
            if (!IsChromiumProfile(profileName))
                continue;

            targets.AddRange(
                DisposableCacheDirectories
                    .Select(cache => Path.Combine(profile, cache))
                    .Where(Directory.Exists));
        }

        // Shared, profile-independent caches.
        foreach (var shared in new[] { "ShaderCache", "GrShaderCache", "GraphiteDawnCache" })
        {
            var path = Path.Combine(userData, shared);
            if (Directory.Exists(path))
                targets.Add(path);
        }

        if (targets.Count == 0)
            yield break;

        yield return new CleanupRule
        {
            Id = $"browser.{id}",
            Name = $"{name} cache",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = $"Cached web content, compiled scripts and shaders for {name}.",
            WhatBreaks =
                "Nothing. You stay signed in and keep your history, bookmarks and extensions — "
                + "pages simply reload from the network the first time you revisit them.",
            Root = userData,
            Targets = targets,
        };
    }

    private static bool IsChromiumProfile(string name) =>
        name.Equals("Default", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<CleanupRule> FirefoxProfiles(string roaming, string local)
    {
        // Firefox splits its profile: settings under Roaming, the disk cache under Local.
        var profilesRoot = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        if (!Directory.Exists(profilesRoot))
            yield break;

        var targets = EnumerateDirectories(profilesRoot)
            .Select(profile => Path.Combine(profile, "cache2"))
            .Where(Directory.Exists)
            .ToList();

        if (targets.Count == 0)
            yield break;

        yield return new CleanupRule
        {
            Id = "browser.firefox",
            Name = "Firefox cache",
            Category = Category,
            Risk = RiskLevel.Safe,
            Description = "Firefox's on-disk network cache.",
            WhatBreaks = "Nothing. Sessions, bookmarks and history live elsewhere and are untouched.",
            Root = profilesRoot,
            Targets = targets,
        };
    }

    private static IEnumerable<CleanupRule> ElectronApps(string roaming)
    {
        if (!Directory.Exists(roaming))
            yield break;

        foreach (var appDirectory in EnumerateDirectories(roaming))
        {
            var targets = DisposableCacheDirectories
                .Select(cache => Path.Combine(appDirectory, cache))
                .Where(Directory.Exists)
                .ToList();

            // A directory is only treated as an Electron app if it actually has these caches;
            // that check is what keeps this from guessing at unrelated folders.
            if (targets.Count == 0)
                continue;

            var appName = Path.GetFileName(appDirectory);

            yield return new CleanupRule
            {
                Id = $"electron.{appName.ToLowerInvariant().Replace(' ', '-')}",
                Name = $"{appName} cache",
                Category = Category,
                Risk = RiskLevel.Safe,
                Description = $"Cached web content and compiled scripts for {appName}.",
                WhatBreaks =
                    $"Nothing. {appName} keeps its settings and sign-in; it re-fetches cached "
                    + "content on next launch.",
                Root = appDirectory,
                Targets = targets,
            };
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path);
        }
        catch (Exception)
        {
            return [];
        }
    }
}
