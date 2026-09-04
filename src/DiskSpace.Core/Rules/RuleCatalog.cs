using DiskSpace.Core.Model;
using DiskSpace.Core.Safety;
using DiskSpace.Core.Scanning;

namespace DiskSpace.Core.Rules;

public interface IRuleProvider
{
    string Name { get; }

    /// <summary>Rules this provider contributes on this machine. Cheap; no disk measurement.</summary>
    IEnumerable<CleanupRule> GetRules();
}

public readonly record struct RuleProgress(int Completed, int Total, string CurrentRule);

/// <summary>
/// Runs every provider, measures what its rules point at, and returns findings.
///
/// Measuring is the expensive half, so it is the only part that touches disk in bulk, and it
/// runs through the same <see cref="FastDirectoryScanner"/> the Explorer page uses.
/// </summary>
public sealed class RuleCatalog(IEnumerable<IRuleProvider>? providers = null)
{
    private readonly List<IRuleProvider> _providers = [.. providers ?? DefaultProviders()];

    public static IEnumerable<IRuleProvider> DefaultProviders() =>
    [
        new PackageManagerCacheProvider(),
        new BrowserAndElectronProvider(),
        new WindowsCacheProvider(),
        new OrphanedAppDataProvider(),
        new LargeItemProvider(),
    ];

    public IReadOnlyList<CleanupRule> GetRules() => Deduplicate([.. _providers.SelectMany(SafeGetRules)]);

    /// <summary>
    /// Drops orphan findings for folders a purpose-built rule already covers.
    ///
    /// The orphan detector works by name and cannot know that, say, %LOCALAPPDATA%\deno is a
    /// package cache with its own rule. Without this the same folder appears twice, in two
    /// categories, at two different risk levels — and the more specific rule is always the
    /// better description.
    /// </summary>
    private static List<CleanupRule> Deduplicate(List<CleanupRule> rules)
    {
        var claimed = rules
            .Where(r => !r.Id.StartsWith("orphan.", StringComparison.Ordinal))
            .SelectMany(r => r.Targets)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return
        [
            .. rules.Where(rule =>
                !rule.Id.StartsWith("orphan.", StringComparison.Ordinal)
                || !rule.Targets.Any(target => IsClaimed(NormalizePath(target), claimed))),
        ];
    }

    private static bool IsClaimed(string target, HashSet<string> claimed)
    {
        if (claimed.Contains(target))
            return true;

        // Also claimed when a more specific rule targets something inside this folder.
        var prefix = target + Path.DirectorySeparatorChar;
        return claimed.Any(c => c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static IEnumerable<CleanupRule> SafeGetRules(IRuleProvider provider)
    {
        try
        {
            return provider.GetRules();
        }
        catch (Exception)
        {
            // A provider that fails to enumerate should cost its own rules, not the whole scan.
            return [];
        }
    }

    /// <summary>
    /// Measures every rule and returns what is actually there. Nothing is deleted, and nothing
    /// is even opened for writing — this is a pure read.
    /// </summary>
    public async Task<IReadOnlyList<CleanupFinding>> ResolveAsync(
        IProgress<RuleProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var rules = GetRules();
        var guard = new PathGuard();
        var scanner = new FastDirectoryScanner();
        var findings = new List<CleanupFinding>();
        var completed = 0;

        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new RuleProgress(completed, rules.Count, rule.Name));

            foreach (var target in rule.Targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var finding = await MeasureAsync(rule, target, guard, scanner, cancellationToken)
                    .ConfigureAwait(false);

                if (finding is not null)
                    findings.Add(finding);
            }

            completed++;
        }

        progress?.Report(new RuleProgress(rules.Count, rules.Count, "Done"));
        return findings;
    }

    private static async Task<CleanupFinding?> MeasureAsync(
        CleanupRule rule,
        string target,
        PathGuard guard,
        FastDirectoryScanner scanner,
        CancellationToken cancellationToken)
    {
        try
        {
            // Report-only findings exist to explain disk usage, so they are measured but never
            // put through the guard — nothing will be deleted from them.
            if (rule.Risk != RiskLevel.ReportOnly)
            {
                var probe = rule.RemoveTargetDirectory
                    ? target
                    : Path.Combine(target, "probe");

                var verdict = guard.Check(probe, rule.Root);
                if (!verdict.Allowed)
                    return null;
            }

            if (File.Exists(target))
            {
                var info = new FileInfo(target);
                return TooRecent(rule, info.LastWriteTimeUtc)
                    ? null
                    : new CleanupFinding
                    {
                        Rule = rule,
                        Path = target,
                        Size = info.Length,
                        FileCount = 1,
                        LastWriteUtc = info.LastWriteTimeUtc,
                    };
            }

            if (!Directory.Exists(target))
                return null;

            var result = await scanner.ScanAsync(target, null, cancellationToken)
                .ConfigureAwait(false);

            if (result.TotalSize == 0 && result.TotalFileCount == 0)
                return null;

            if (result.TotalSize < rule.MinimumSize)
                return null;

            if (TooRecent(rule, result.Root.LastWriteUtc))
                return null;

            return new CleanupFinding
            {
                Rule = rule,
                Path = target,
                Size = result.TotalSize,
                FileCount = result.TotalFileCount,
                LastWriteUtc = result.Root.LastWriteUtc,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // An unreadable target is simply not offered.
            return null;
        }
    }

    private static bool TooRecent(CleanupRule rule, DateTime lastWriteUtc) =>
        rule.MinimumAge is { } age
        && lastWriteUtc != default
        && DateTime.UtcNow - lastWriteUtc < age;
}
