namespace DiskSpace.Core.Safety;

public readonly record struct GuardVerdict(bool Allowed, string Reason)
{
    public static GuardVerdict Allow() => new(true, string.Empty);
    public static GuardVerdict Deny(string reason) => new(false, reason);
}

/// <summary>
/// The last line of defense before anything is deleted.
///
/// The app runs elevated and deletes permanently, so there is no restricted token and no
/// Recycle Bin to catch a mistake — this class is what stands between a bad rule and a broken
/// machine. It is deliberately enforced at the deletion boundary rather than inside the rules,
/// so no rule can opt out of it, and every check runs against the canonical path
/// (<see cref="PathCanonicalizer"/>) rather than the string the caller supplied.
///
/// The bias is unapologetically toward refusing: a false refusal costs some reclaimable bytes,
/// a false approval can cost the operating system.
/// </summary>
public sealed class PathGuard
{
    /// <summary>
    /// A path must have at least this many segments, counting the volume. It stops a rule
    /// from ever naming a drive root, <c>C:\Users</c>, or a whole user profile.
    /// </summary>
    private const int MinimumDepth = 4;

    private readonly HashSet<string> _protectedExact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _protectedTrees = [];
    private readonly List<string> _windowsAllowlist = [];
    private readonly string _windowsDirectory;

    public PathGuard()
    {
        _windowsDirectory = Canonical(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        AddProtectedTrees();
        AddProtectedExactPaths();
        AddWindowsAllowlist();
    }

    private void AddProtectedTrees()
    {
        // Whole trees that no rule may ever touch, at any depth.
        foreach (var folder in new[]
                 {
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.SystemX86,
                     Environment.SpecialFolder.MyDocuments,
                     Environment.SpecialFolder.MyPictures,
                     Environment.SpecialFolder.MyVideos,
                     Environment.SpecialFolder.MyMusic,
                     Environment.SpecialFolder.Desktop,
                     Environment.SpecialFolder.Favorites,
                     Environment.SpecialFolder.Startup,
                 })
        {
            AddTree(Environment.GetFolderPath(folder));
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            // Irreplaceable user data and credentials. Downloads is included because a cleaner
            // guessing at "old downloads" is exactly the behaviour that loses someone a file
            // they cannot get back.
            foreach (var name in new[] { "Downloads", "OneDrive", ".ssh", ".gnupg", "source", "Documents" })
                AddTree(Path.Combine(profile, name));
        }
    }

    private void AddProtectedExactPaths()
    {
        // Directories that may be cleaned *inside* but must never themselves be removed.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        foreach (var path in new[]
                 {
                     profile,
                     Path.GetDirectoryName(profile),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                     _windowsDirectory,
                 })
        {
            if (!string.IsNullOrEmpty(path))
                _protectedExact.Add(Canonical(path));
        }
    }

    private void AddWindowsAllowlist()
    {
        // The only places under %WINDIR% a rule may reach. Everything else there is refused.
        foreach (var relative in new[]
                 {
                     "Temp",
                     "SystemTemp",
                     @"SoftwareDistribution\Download",
                     "Prefetch",
                     @"Logs\CBS",
                     "Downloaded Program Files",
                     @"ServiceProfiles\LocalService\AppData\Local\Temp",
                 })
        {
            _windowsAllowlist.Add(Canonical(Path.Combine(_windowsDirectory, relative)));
        }
    }

    private void AddTree(string? path)
    {
        if (!string.IsNullOrEmpty(path))
            _protectedTrees.Add(Canonical(path));
    }

    private static string Canonical(string path)
    {
        try
        {
            return PathCanonicalizer.Canonicalize(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>
    /// Decides whether <paramref name="path"/> may be deleted as part of a rule that declared
    /// <paramref name="declaredRoot"/> as its territory.
    /// </summary>
    public GuardVerdict Check(string path, string declaredRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
            return GuardVerdict.Deny("Empty path");

        string canonical;
        string canonicalRoot;

        try
        {
            canonical = PathCanonicalizer.Canonicalize(path);
            canonicalRoot = PathCanonicalizer.Canonicalize(declaredRoot);
        }
        catch (Exception ex)
        {
            return GuardVerdict.Deny($"Path could not be resolved: {ex.Message}");
        }

        // Checked first: everything below reasons about a path that is inside its own rule.
        // A junction pointing out of the root fails here, after resolution, not before.
        if (!PathCanonicalizer.IsInside(canonical, canonicalRoot))
            return GuardVerdict.Deny($"Resolves outside the rule's root ({canonicalRoot})");

        if (PathCanonicalizer.Depth(canonical) < MinimumDepth)
            return GuardVerdict.Deny("Too close to the volume root");

        var root = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(root))
            return GuardVerdict.Deny("Path has no volume");

        if (string.Equals(canonical.TrimEnd(Path.DirectorySeparatorChar),
                          root.TrimEnd(Path.DirectorySeparatorChar),
                          StringComparison.OrdinalIgnoreCase))
        {
            return GuardVerdict.Deny("Volume root");
        }

        if (canonical.StartsWith(@"\\", StringComparison.Ordinal))
            return GuardVerdict.Deny("Network path");

        if (VolumeVerdict(root) is { Allowed: false } volumeDenial)
            return volumeDenial;

        if (canonical.Contains("System Volume Information", StringComparison.OrdinalIgnoreCase))
            return GuardVerdict.Deny("System Volume Information");

        if (_protectedExact.Contains(canonical.TrimEnd(Path.DirectorySeparatorChar)))
            return GuardVerdict.Deny("Protected directory — may be cleaned inside, never removed");

        foreach (var tree in _protectedTrees)
        {
            if (PathCanonicalizer.IsInside(canonical, tree))
                return GuardVerdict.Deny($"Inside a protected location ({tree})");
        }

        if (PathCanonicalizer.IsInside(canonical, _windowsDirectory) && !IsWindowsAllowlisted(canonical))
            return GuardVerdict.Deny("Inside the Windows directory and not on the allowlist");

        return GuardVerdict.Allow();
    }

    private bool IsWindowsAllowlisted(string canonical) =>
        _windowsAllowlist.Any(allowed => PathCanonicalizer.IsInside(canonical, allowed));

    private static GuardVerdict VolumeVerdict(string root)
    {
        try
        {
            var drive = new DriveInfo(root);

            // Removable and network volumes are someone else's data; a cleaner has no business
            // forming opinions about what is stale on them.
            if (drive.DriveType is DriveType.Network or DriveType.Removable or DriveType.CDRom)
                return GuardVerdict.Deny($"{drive.DriveType} volume");
        }
        catch (Exception)
        {
            return GuardVerdict.Deny("Volume could not be inspected");
        }

        return GuardVerdict.Allow();
    }
}
