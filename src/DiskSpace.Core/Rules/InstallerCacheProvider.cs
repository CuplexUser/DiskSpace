using System.Runtime.InteropServices;
using System.Text;
using DiskSpace.Core.Model;
using Microsoft.Win32;

namespace DiskSpace.Core.Rules;

/// <summary>
/// Installer packages in <c>%WINDIR%\Installer</c> that no installed product or patch refers to.
///
/// Windows Installer keeps a copy of every .msi and .msp it has applied, so that the product
/// can later be repaired, patched or removed. When a product is uninstalled or a patch is
/// replaced, the cached copy is often left behind, and on a machine that has seen years of
/// Office or Visual Studio updates the leftovers run to gigabytes.
///
/// A file is offered only when both sources of truth agree nothing uses it: Windows Installer's
/// own API, asked about every user on the machine, and the registry it keeps its records in.
/// If either cannot be read in full, nothing is offered at all, because a cached package
/// wrongly removed breaks the repair and uninstall of a product that is still installed. For
/// the same reason these findings are quarantined, not deleted.
/// </summary>
public sealed class InstallerCacheProvider : IRuleProvider
{
    public string Name => "Windows Installer cache";

    private const string Category = "Orphaned installer packages";

    /// <summary>
    /// A package arrives in the cache before the installation that owns it is registered. A
    /// week is far longer than any installation takes.
    /// </summary>
    private static readonly TimeSpan SettlingPeriod = TimeSpan.FromDays(7);

    public IEnumerable<CleanupRule> GetRules()
    {
        var cache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Installer");

        if (!Directory.Exists(cache))
            yield break;

        var referenced = WindowsInstaller.ReferencedPackages();
        if (referenced is null)
            yield break;

        var orphans = FindOrphans(cache, referenced, DateTime.UtcNow);
        if (orphans.Count == 0)
            yield break;

        yield return new CleanupRule
        {
            Id = "msi.orphaned-packages",
            Name = "Orphaned installer package",
            Category = Category,
            Risk = RiskLevel.Review,
            Description =
                "A cached Windows Installer package (.msi) or patch (.msp) that no installed "
                + "product or applied patch refers to any more. These are left behind when "
                + "software is removed or a patch is replaced by a newer one.",
            WhatBreaks =
                "Nothing, if Windows Installer's records are right, and they were checked two "
                + "ways. If they are wrong, repairing or uninstalling the product this belonged "
                + "to would ask for the original installer. That is why it is quarantined and "
                + "can be put back.",
            Root = cache,
            Targets = orphans,
            RemoveTargetDirectory = true,
            RequiresElevation = true,
            Quarantine = true,
        };
    }

    /// <summary>
    /// The packages directly inside <paramref name="cacheDirectory"/> that are not in
    /// <paramref name="referenced"/> and have been there longer than the settling period.
    /// Subfolders are not looked at: they hold other things, the patch baseline cache among
    /// them, that follow different rules.
    /// </summary>
    public static IReadOnlyList<string> FindOrphans(
        string cacheDirectory, IReadOnlySet<string> referenced, DateTime nowUtc)
    {
        var orphans = new List<string>();

        try
        {
            foreach (var file in new DirectoryInfo(cacheDirectory).EnumerateFiles())
            {
                if (!file.Extension.Equals(".msi", StringComparison.OrdinalIgnoreCase)
                    && !file.Extension.Equals(".msp", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (referenced.Contains(file.FullName))
                    continue;

                if (nowUtc - file.CreationTimeUtc < SettlingPeriod
                    || nowUtc - file.LastWriteTimeUtc < SettlingPeriod)
                {
                    continue;
                }

                orphans.Add(file.FullName);
            }
        }
        catch (Exception)
        {
            // A cache that cannot be listed in full is not judged in part.
            return [];
        }

        return orphans;
    }
}

/// <summary>What Windows Installer has on record, read through msi.dll and the registry.</summary>
public static class WindowsInstaller
{
    /// <summary>The well-known SID that asks the installer API about every user's products.</summary>
    private const string Everyone = "s-1-1-0";

    private const uint AllContexts = 7;
    private const uint AllPatchStates = 15;
    private const int GuidLength = 39;

    private const uint Success = 0;
    private const uint MoreData = 234;
    private const uint NoMoreItems = 259;
    private const uint UnknownProperty = 1608;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumProductsExW(
        string? productCode, string? userSid, uint context, uint index,
        StringBuilder installedProductCode, out uint installedContext,
        StringBuilder? sid, ref uint sidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetProductInfoExW(
        string productCode, string? userSid, uint context, string property,
        StringBuilder? value, ref uint valueLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumPatchesExW(
        string? productCode, string? userSid, uint context, uint filter, uint index,
        StringBuilder patchCode, StringBuilder targetProductCode, out uint targetContext,
        StringBuilder? targetUserSid, ref uint targetUserSidLength);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiGetPatchInfoExW(
        string patchCode, string productCode, string? userSid, uint context, string property,
        StringBuilder? value, ref uint valueLength);

    /// <summary>
    /// Every cached package some product or patch still refers to, for all users, as full
    /// paths. Null when that cannot be established with certainty, which includes running
    /// without administrator rights: the answer would then leave out other users' products.
    /// </summary>
    public static IReadOnlySet<string>? ReferencedPackages() => ReferencedPackages(Everyone);

    /// <summary>
    /// As above, for the users <paramref name="userSid"/> selects. Null asks about the current
    /// user and the machine only, which needs no elevation and is what the tests use.
    /// </summary>
    public static IReadOnlySet<string>? ReferencedPackages(string? userSid)
    {
        try
        {
            var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!AddFromApi(packages, userSid) || !AddFromRegistry(packages))
                return null;

            return packages;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool AddFromApi(HashSet<string> packages, string? userSid)
    {
        for (uint index = 0; ; index++)
        {
            var product = new StringBuilder(GuidLength);
            var sid = new StringBuilder(256);
            var sidLength = (uint)sid.Capacity;

            var result = MsiEnumProductsExW(
                null, userSid, AllContexts, index, product, out var context, sid, ref sidLength);

            if (result == NoMoreItems)
                break;

            if (result != Success)
                return false;

            var owner = sid.Length > 0 ? sid.ToString() : null;

            if (!TryRead(
                    (StringBuilder? buffer, ref uint length) => MsiGetProductInfoExW(
                        product.ToString(), owner, context, "LocalPackage", buffer, ref length),
                    out var package))
            {
                return false;
            }

            Add(packages, package);
        }

        for (uint index = 0; ; index++)
        {
            var patch = new StringBuilder(GuidLength);
            var target = new StringBuilder(GuidLength);
            var sid = new StringBuilder(256);
            var sidLength = (uint)sid.Capacity;

            var result = MsiEnumPatchesExW(
                null, userSid, AllContexts, AllPatchStates, index, patch, target, out var context,
                sid, ref sidLength);

            if (result == NoMoreItems)
                break;

            if (result != Success)
                return false;

            var owner = sid.Length > 0 ? sid.ToString() : null;

            if (!TryRead(
                    (StringBuilder? buffer, ref uint length) => MsiGetPatchInfoExW(
                        patch.ToString(), target.ToString(), owner, context, "LocalPackage", buffer, ref length),
                    out var package))
            {
                return false;
            }

            Add(packages, package);
        }

        return true;
    }

    private delegate uint PropertyReader(StringBuilder? buffer, ref uint length);

    /// <summary>
    /// Reads one property. False when the installer would not say, which the caller treats as
    /// not knowing; a product with no cached package at all is a true with an empty value.
    /// </summary>
    private static bool TryRead(PropertyReader read, out string value)
    {
        value = string.Empty;

        var buffer = new StringBuilder(260);
        var length = (uint)buffer.Capacity;
        var result = read(buffer, ref length);

        if (result == MoreData)
        {
            buffer = new StringBuilder((int)length + 1);
            length = (uint)buffer.Capacity;
            result = read(buffer, ref length);
        }

        if (result == UnknownProperty)
            return true;

        if (result != Success)
            return false;

        value = buffer.ToString();
        return true;
    }

    /// <summary>
    /// The same records, read from where the installer stores them. Belt and braces: a package
    /// either source names is kept.
    /// </summary>
    private static bool AddFromRegistry(HashSet<string> packages)
    {
        using var userData = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData");

        if (userData is null)
            return false;

        foreach (var sid in userData.GetSubKeyNames())
        {
            using var products = userData.OpenSubKey($@"{sid}\Products");
            foreach (var product in products?.GetSubKeyNames() ?? [])
            {
                using var properties = products!.OpenSubKey($@"{product}\InstallProperties");
                Add(packages, properties?.GetValue("LocalPackage") as string);
            }

            using var patches = userData.OpenSubKey($@"{sid}\Patches");
            foreach (var patch in patches?.GetSubKeyNames() ?? [])
            {
                using var properties = patches!.OpenSubKey(patch);
                Add(packages, properties?.GetValue("LocalPackage") as string);
            }
        }

        return true;
    }

    private static void Add(HashSet<string> packages, string? package)
    {
        if (string.IsNullOrWhiteSpace(package))
            return;

        try
        {
            packages.Add(Path.GetFullPath(package));
        }
        catch (Exception)
        {
            packages.Add(package);
        }
    }
}
