using DiskSpace.Core.Model;

namespace DiskSpace.Core.Rules;

/// <summary>
/// Developer package-manager caches — usually the single largest reclaimable category on a
/// development machine, and the safest: every one of these is a local copy of something the
/// tool can fetch again.
/// </summary>
public sealed class PackageManagerCacheProvider : IRuleProvider
{
    public string Name => "Package manager caches";

    private const string Category = "Package manager caches";

    public IEnumerable<CleanupRule> GetRules()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Cache(
            "npm", "npm cache", Path.Combine(local, "npm-cache"),
            "Downloaded npm packages and their metadata index.",
            "Nothing. The next install re-downloads what it needs, so the first install after "
            + "this is slower.",
            new PurgeCommand("npm", "cache clean --force"));

        yield return Cache(
            "pnpm-store", "pnpm store", Path.Combine(local, "pnpm-cache"),
            "pnpm's content-addressable package store.",
            "Existing projects keep working, but pnpm re-fetches packages on the next install. "
            + "Note that pnpm hard-links from this store, so projects on the same volume may "
            + "need reinstalling.",
            new PurgeCommand("pnpm", "store prune"));

        yield return Cache(
            "pnpm-state", "pnpm state", Path.Combine(local, "pnpm-state"),
            "pnpm's internal bookkeeping.",
            "Nothing; pnpm rebuilds it.");

        yield return Cache(
            "yarn", "Yarn cache", Path.Combine(local, "Yarn", "Cache"),
            "Yarn's global package cache.",
            "Nothing. Installs re-download.",
            new PurgeCommand("yarn", "cache clean"));

        yield return Cache(
            "pip", "pip cache", Path.Combine(local, "pip", "cache"),
            "Downloaded Python wheels and HTTP responses.",
            "Nothing. pip re-downloads and rebuilds wheels as needed.",
            new PurgeCommand("pip", "cache purge"));

        yield return Cache(
            "uv", "uv cache", Path.Combine(local, "uv", "cache"),
            "uv's package and wheel cache.",
            "Nothing. uv re-downloads on the next sync.",
            new PurgeCommand("uv", "cache clean"));

        yield return Cache(
            "nuget", "NuGet packages", Path.Combine(profile, ".nuget", "packages"),
            "Extracted NuGet packages shared by every .NET project on this machine.",
            "Nothing permanent, but every .NET project on this machine restores from here — "
            + "the next build of each will re-download its dependencies.",
            new PurgeCommand("dotnet", "nuget locals all --clear"));

        yield return Cache(
            "nuget-http", "NuGet HTTP cache", Path.Combine(local, "NuGet", "v3-cache"),
            "Cached NuGet feed responses.",
            "Nothing. Restores re-query the feed.");

        yield return Cache(
            "nuget-plugins", "NuGet plugin cache", Path.Combine(local, "NuGet", "plugins-cache"),
            "Cached NuGet credential-plugin results.",
            "Nothing; it is rebuilt on demand.");

        yield return Cache(
            "deno", "Deno cache", Path.Combine(local, "deno"),
            "Deno's downloaded modules and compiled artifacts.",
            "Nothing. Deno re-fetches remote modules on the next run.");

        yield return Cache(
            "cargo", "Cargo registry", Path.Combine(profile, ".cargo", "registry"),
            "Downloaded and unpacked Rust crates.",
            "Nothing. Cargo re-downloads crates on the next build.");

        yield return Cache(
            "go", "Go module cache", Path.Combine(profile, "go", "pkg", "mod", "cache", "download"),
            "Downloaded Go modules.",
            "Nothing. Go re-downloads modules on the next build.",
            new PurgeCommand("go", "clean -modcache"));

        yield return Cache(
            "gradle", "Gradle cache", Path.Combine(profile, ".gradle", "caches"),
            "Gradle's dependency and build cache.",
            "Nothing permanent, but the next Gradle build is substantially slower.");

        yield return Cache(
            "maven", "Maven repository", Path.Combine(profile, ".m2", "repository"),
            "Downloaded Maven artifacts.",
            "Nothing, unless you have artifacts installed locally that are not in any remote "
            + "repository — those would need rebuilding.",
            risk: RiskLevel.Review);

        yield return Cache(
            "node-gyp", "node-gyp headers", Path.Combine(local, "node-gyp"),
            "Node.js headers kept for compiling native modules.",
            "Nothing. node-gyp re-downloads headers when a native module is next built.");

        yield return Cache(
            "playwright", "Playwright browsers", Path.Combine(local, "ms-playwright"),
            "Downloaded Chromium, Firefox and WebKit builds for Playwright.",
            "Playwright tests fail until the browsers are reinstalled with "
            + "`playwright install`. Each browser is several hundred megabytes.",
            risk: RiskLevel.Review);

        yield return Cache(
            "playwright-mcp", "Playwright MCP profile", Path.Combine(local, "ms-playwright-mcp"),
            "Browser profile data for the Playwright MCP server.",
            "Any logged-in sessions in that automated browser are lost.",
            risk: RiskLevel.Review);

        yield return Cache(
            "puppeteer", "Puppeteer browsers", Path.Combine(profile, ".cache", "puppeteer"),
            "Chromium builds downloaded by Puppeteer.",
            "Puppeteer re-downloads Chromium on next use.",
            risk: RiskLevel.Review);

        yield return Cache(
            "electron", "Electron builds", Path.Combine(local, "electron", "Cache"),
            "Downloaded Electron binaries.",
            "Nothing. They are re-downloaded when a build needs them.");

        yield return Cache(
            "dotnet-templates", "dotnet template cache",
            Path.Combine(profile, ".templateengine"),
            "The .NET template engine's index.",
            "Nothing; it is rebuilt on the next `dotnet new`.");

        yield return Cache(
            "vs-package-cache", "Visual Studio installer cache",
            Path.Combine(local, "Package Cache"),
            "Installer payloads kept by Visual Studio and other Microsoft installers.",
            "Repair and modify operations will need to re-download their payloads. Uninstall "
            + "may prompt for installation media.",
            risk: RiskLevel.Advanced);

        yield return Cache(
            "npm-roaming", "npm roaming cache", Path.Combine(roaming, "npm-cache"),
            "An older npm cache location.",
            "Nothing. Installs re-download.");
    }

    private static CleanupRule Cache(
        string id,
        string name,
        string path,
        string description,
        string whatBreaks,
        PurgeCommand? purge = null,
        RiskLevel risk = RiskLevel.Safe) => new()
        {
            Id = $"pkg.{id}",
            Name = name,
            Category = Category,
            Risk = risk,
            Description = description,
            WhatBreaks = whatBreaks,
            Root = path,
            Targets = [path],
            // Emptied rather than removed: several of these tools error out if their cache
            // directory is missing rather than empty.
            RemoveTargetDirectory = false,
            Purge = purge,
        };
}
