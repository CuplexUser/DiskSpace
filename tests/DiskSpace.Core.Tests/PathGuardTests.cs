using DiskSpace.Core.Safety;

namespace DiskSpace.Core.Tests;

/// <summary>
/// The heaviest-covered file in the solution, by intent. The app deletes permanently while
/// elevated, so a gap here has no second line of defense behind it.
/// </summary>
public sealed class PathGuardTests
{
    private readonly PathGuard _guard = new();

    private static string Windows => Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // ---- Things that must always be refused -------------------------------------------------

    public static TheoryData<string, string> AlwaysRefused()
    {
        var data = new TheoryData<string, string>();
        var root = Path.GetPathRoot(Windows)!;

        data.Add(root, root);                                          // volume root
        data.Add(Windows, root);                                       // the Windows directory
        data.Add(Path.Combine(Windows, "System32"), root);             // system binaries
        data.Add(Path.Combine(Windows, "System32", "kernel32.dll"), root);
        data.Add(Profile, root);                                       // the whole profile
        data.Add(Path.GetDirectoryName(Profile)!, root);               // C:\Users
        data.Add(LocalAppData, root);                                  // AppData\Local itself
        data.Add(Path.Combine(Profile, "Documents"), root);
        data.Add(Path.Combine(Profile, "Documents", "taxes.xlsx"), root);
        data.Add(Path.Combine(Profile, "Downloads"), root);
        data.Add(Path.Combine(Profile, "Desktop"), root);
        data.Add(Path.Combine(Profile, ".ssh"), root);
        data.Add(Path.Combine(Profile, ".ssh", "id_ed25519"), root);
        data.Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), root);
        data.Add(Path.Combine(root, "System Volume Information"), root);
        data.Add(@"\\server\share\some\file", @"\\server\share");      // network path
        return data;
    }

    [Theory]
    [MemberData(nameof(AlwaysRefused))]
    public void Refuses_protected_locations(string path, string declaredRoot)
    {
        var verdict = _guard.Check(path, declaredRoot);

        Assert.False(verdict.Allowed, $"Expected refusal for {path}");
        Assert.NotEmpty(verdict.Reason);
    }

    // ---- Traversal, short names and junctions -----------------------------------------------

    [Fact]
    public void Refuses_dot_dot_traversal_that_escapes_into_Windows()
    {
        // The classic bypass: a rule rooted in Temp, handed a path that climbs out of it.
        var temp = Path.GetTempPath();
        var escape = Path.Combine(temp, "..", "..", "..", "Windows", "System32");

        var verdict = _guard.Check(escape, temp);

        Assert.False(verdict.Allowed);
    }

    [Fact]
    public void Refuses_a_short_8_dot_3_name_for_a_protected_location()
    {
        // C:\PROGRA~1 is C:\Program Files. A prefix test against the long name misses it.
        var root = Path.GetPathRoot(Windows)!;
        var shortName = Path.Combine(root, "PROGRA~1");

        if (!Directory.Exists(shortName))
            return; // 8.3 generation disabled on this volume; nothing to assert.

        Assert.False(_guard.Check(Path.Combine(shortName, "anything"), root).Allowed);
    }

    [Fact]
    public void Refuses_a_junction_that_points_out_of_the_declared_root()
    {
        using var fixture = new ScanFixture();
        var cleanable = fixture.Dir("cache");

        fixture.CreateJunction("cache/escape", Windows);

        // The path looks like it is inside the rule's root, and is not.
        var verdict = _guard.Check(
            Path.Combine(cleanable, "escape", "System32"), cleanable);

        Assert.False(verdict.Allowed);
        Assert.Contains("outside", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Refuses_anything_outside_the_rules_declared_root()
    {
        using var fixture = new ScanFixture();
        var mine = fixture.Dir("mine");
        var theirs = fixture.Dir("theirs");

        Assert.False(_guard.Check(Path.Combine(theirs, "file.bin"), mine).Allowed);
    }

    [Fact]
    public void Refuses_a_sibling_whose_name_merely_shares_a_prefix()
    {
        using var fixture = new ScanFixture();
        var root = fixture.Dir("app");
        fixture.Dir("app-backup");

        var sibling = Path.Combine(fixture.Root, "app-backup", "data.bin");

        Assert.False(_guard.Check(sibling, root).Allowed);
    }

    // ---- Depth ------------------------------------------------------------------------------

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\someone")]
    public void Refuses_paths_too_close_to_the_volume_root(string path)
    {
        Assert.False(_guard.Check(path, @"C:\").Allowed);
    }

    // ---- Things that must be allowed ---------------------------------------------------------

    [Fact]
    public void Allows_a_package_cache_inside_local_appdata()
    {
        var cache = Path.Combine(LocalAppData, "npm-cache");
        var verdict = _guard.Check(Path.Combine(cache, "_cacache", "index-v5", "aa"), cache);

        Assert.True(verdict.Allowed, verdict.Reason);
    }

    [Fact]
    public void Allows_the_windows_temp_directory_contents()
    {
        var temp = Path.Combine(Windows, "Temp");
        var verdict = _guard.Check(Path.Combine(temp, "stale.tmp"), temp);

        Assert.True(verdict.Allowed, verdict.Reason);
    }

    [Fact]
    public void Allows_an_orphaned_app_folder_under_appdata()
    {
        var orphan = Path.Combine(LocalAppData, "SomeUninstalledTool");
        var verdict = _guard.Check(orphan, LocalAppData);

        Assert.True(verdict.Allowed, verdict.Reason);
    }

    [Fact]
    public void Allows_a_file_that_no_longer_exists()
    {
        // Plans go stale between preview and execution; a vanished file must still evaluate.
        var gone = Path.Combine(LocalAppData, "npm-cache", "already-deleted.tmp");
        var verdict = _guard.Check(gone, Path.Combine(LocalAppData, "npm-cache"));

        Assert.True(verdict.Allowed, verdict.Reason);
    }

    // ---- Canonicalizer behaviour it all rests on --------------------------------------------

    [Fact]
    public void IsInside_compares_whole_segments()
    {
        Assert.True(PathCanonicalizer.IsInside(@"C:\a\b\c", @"C:\a\b"));
        Assert.True(PathCanonicalizer.IsInside(@"C:\a\b", @"C:\a\b"));
        Assert.False(PathCanonicalizer.IsInside(@"C:\a\bb", @"C:\a\b"));
        Assert.False(PathCanonicalizer.IsInside(@"C:\a", @"C:\a\b"));
    }

    [Theory]
    [InlineData(@"C:\", 1)]
    [InlineData(@"C:\Users", 2)]
    [InlineData(@"C:\Users\me", 3)]
    [InlineData(@"C:\Users\me\AppData", 4)]
    [InlineData(@"C:\Users\me\AppData\Local\npm-cache", 6)]
    public void Depth_counts_the_volume_as_one_segment(string path, int expected)
    {
        Assert.Equal(expected, PathCanonicalizer.Depth(path));
    }

    [Fact]
    public void Canonicalize_resolves_a_junction_to_its_target()
    {
        using var fixture = new ScanFixture();
        var target = fixture.Dir("real");
        fixture.CreateJunction("link", target);

        var resolved = PathCanonicalizer.Canonicalize(Path.Combine(fixture.Root, "link"));

        Assert.True(
            PathCanonicalizer.IsInside(resolved, PathCanonicalizer.Canonicalize(target)),
            $"Expected {resolved} to resolve into {target}");
    }
}
