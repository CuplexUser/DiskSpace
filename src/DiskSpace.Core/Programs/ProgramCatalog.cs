using DiskSpace.Core.Safety;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Programs;

public interface IProgramProvider
{
    string Name { get; }

    /// <summary>What this provider knows is installed. Cheap; no disk measurement.</summary>
    IEnumerable<InstalledProgram> GetPrograms();
}

/// <summary>
/// Finds what is installed and measures it.
///
/// Deliberately shaped like <see cref="Rules.RuleCatalog"/>: providers describe territory,
/// the catalog does the expensive part, and a provider that throws costs only its own entries.
/// Measuring goes through <see cref="FastDirectoryScanner"/> like everything else, because a
/// second directory walker would be a second set of rules about junctions and hidden files.
/// </summary>
public sealed class ProgramCatalog(IEnumerable<IProgramProvider>? providers = null)
{
    /// <summary>
    /// Roots that belong to everything and so to nothing. An installer that recorded
    /// <c>InstallLocation=C:\Program Files</c> would otherwise be credited with the whole tree.
    ///
    /// Compared as resolved paths rather than by folder name: a program legitimately called
    /// "Packages" or "Programs" exists, and rejecting it by name would lose it entirely.
    /// </summary>
    private static readonly Lazy<HashSet<string>> SharedRoots = new(ResolveSharedRoots);

    /// <summary>
    /// Below this a path is a shared root by construction. <c>C:\Program Files\Vendor</c> counts
    /// three, which is also the shallowest a real install directory gets.
    /// </summary>
    private const int MinimumLocationDepth = 3;

    private readonly List<IProgramProvider> _providers = [.. providers ?? DefaultProviders()];

    public static IEnumerable<IProgramProvider> DefaultProviders() =>
    [
        new RegistryProgramProvider(),
        new StorePackageProvider(),
        new UserInstallProgramProvider(),
        new WindowsComponentProvider(),
    ];

    public IReadOnlyList<InstalledProgram> GetPrograms() =>
        [.. _providers.SelectMany(SafeGetPrograms)];

    /// <summary>
    /// Measures every program. Nothing is deleted and nothing is opened for writing; this is a
    /// pure read, like the rule catalog's resolve pass.
    /// </summary>
    public async Task<IReadOnlyList<ProgramFootprint>> MeasureAsync(
        IProgress<ProgramProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var programs = GetPrograms();
        var claims = AssignClaims(programs);
        var completed = 0;

        var footprints = new ProgramFootprint[programs.Count];

        // Half the cores rather than all of them: several of these walk small directories, and
        // the rule catalog's one-at-a-time shape is the slow part it is worth not copying.
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForAsync(0, programs.Count, parallel, async (index, token) =>
        {
            var program = programs[index];

            progress?.Report(new ProgramProgress(
                Volatile.Read(ref completed), programs.Count, program.Name));

            footprints[index] = await MeasureAsync(program, claims, token).ConfigureAwait(false);
            Interlocked.Increment(ref completed);
        }).ConfigureAwait(false);

        progress?.Report(new ProgramProgress(programs.Count, programs.Count, "Done"));
        return footprints;
    }

    private static async Task<ProgramFootprint> MeasureAsync(
        InstalledProgram program,
        IReadOnlyList<Claim> claims,
        CancellationToken cancellationToken)
    {
        var parts = new List<MeasuredLocation>();

        foreach (var claim in claims.Where(c => c.Owner == program.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            parts.Add(await MeasureAsync(claim, claims, cancellationToken).ConfigureAwait(false));
        }

        var measuredAnything = parts.Any(p => p.Error is null);

        return new ProgramFootprint
        {
            Program = program,
            Parts = parts,
            SizeIsEstimated = !measuredAnything && program.RegistryEstimatedSize > 0,
        };
    }

    private static async Task<MeasuredLocation> MeasureAsync(
        Claim claim, IReadOnlyList<Claim> claims, CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(claim.Path))
            {
                var info = new FileInfo(claim.Path);
                return new MeasuredLocation(claim.Path, claim.Kind, info.Length, 1, null);
            }

            if (!Directory.Exists(claim.Path))
                return new MeasuredLocation(claim.Path, claim.Kind, 0, 0, "No longer present");

            // Anything a more specific claim owns is excluded here, so a suite and one of its
            // components never both count the bytes they share.
            var options = new ScanOptions();
            foreach (var nested in claims)
            {
                if (nested.Path != claim.Path && PathCanonicalizer.IsInside(nested.Path, claim.Path))
                    options.ExcludedPaths.Add(nested.Path);
            }

            var result = await new FastDirectoryScanner(options)
                .ScanAsync(claim.Path, null, cancellationToken)
                .ConfigureAwait(false);

            // A folder listed as denied is a very different statement from a folder of zero
            // bytes, and WindowsApps makes that distinction routine rather than exotic.
            var error = result.Root.Error is not null && result.TotalSize == 0
                ? DescribeDenial(result)
                : null;

            return new MeasuredLocation(
                claim.Path, claim.Kind, result.TotalSize, result.TotalFileCount, error);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MeasuredLocation(claim.Path, claim.Kind, 0, 0, ex.Message);
        }
    }

    private static string DescribeDenial(ScanResult result) =>
        result.Issues.FirstOrDefault().Reason is { Length: > 0 } reason ? reason : "Unreadable";

    private readonly record struct Claim(string Path, LocationKind Kind, string Owner);

    /// <summary>
    /// Decides which program owns which path.
    ///
    /// Overlap is the norm, not the exception: an MSIX package also has a registry entry, and a
    /// suite shares a directory with its own components. Claims are handed out deepest first, so
    /// the most specific description of a path wins, and a shallower claimant then measures
    /// around what it lost rather than double-counting it.
    /// </summary>
    private static List<Claim> AssignClaims(IReadOnlyList<InstalledProgram> programs)
    {
        var candidates = new List<Claim>();

        foreach (var program in programs)
        {
            foreach (var location in program.Locations)
            {
                var canonical = Canonicalize(location.Path);
                if (canonical is null || IsSharedRoot(canonical))
                    continue;

                candidates.Add(new Claim(canonical, location.Kind, program.Id));
            }
        }

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claims = new List<Claim>();

        foreach (var candidate in candidates
                     .OrderByDescending(c => PathCanonicalizer.Depth(c.Path))
                     .ThenBy(c => c.Owner, StringComparer.Ordinal))
        {
            if (claimed.Add(candidate.Path))
                claims.Add(candidate);
        }

        return claims;
    }

    private static string? Canonicalize(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : PathCanonicalizer.Canonicalize(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool IsSharedRoot(string path) =>
        PathCanonicalizer.Depth(path) < MinimumLocationDepth || SharedRoots.Value.Contains(path);

    private static HashSet<string> ResolveSharedRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            var canonical = Canonicalize(path ?? string.Empty);
            if (canonical is not null)
                roots.Add(canonical);
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Add(programFiles);
        Add(programFilesX86);
        Add(local);
        Add(windows);
        Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Add(Path.Combine(programFiles, "Common Files"));
        Add(Path.Combine(programFilesX86, "Common Files"));
        Add(Path.Combine(programFiles, "WindowsApps"));
        Add(Path.Combine(local, "Programs"));
        Add(Path.Combine(local, "Packages"));
        Add(Path.Combine(windows, "System32"));

        try
        {
            foreach (var drive in DriveInfo.GetDrives())
                Add(drive.RootDirectory.FullName);
        }
        catch (Exception)
        {
            // The depth rule already covers a drive root; this only makes the set explicit.
        }

        return roots;
    }

    private static IEnumerable<InstalledProgram> SafeGetPrograms(IProgramProvider provider)
    {
        try
        {
            return provider.GetPrograms();
        }
        catch (Exception)
        {
            // A provider that fails to enumerate costs its own entries, not the whole page.
            return [];
        }
    }
}
