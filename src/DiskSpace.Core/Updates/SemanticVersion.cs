namespace DiskSpace.Core.Updates;

/// <summary>
/// Just enough of semver to order two release tags: major.minor.patch, with any
/// pre-release label or build metadata dropped before comparing. That mirrors the installer
/// script's own version comparison, so a tag that looks newer to Inno Setup looks newer here
/// too.
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch) : IComparable<SemanticVersion>
{
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().TrimStart('v', 'V');

        var dash = trimmed.IndexOf('-');
        if (dash >= 0)
            trimmed = trimmed[..dash];

        var plus = trimmed.IndexOf('+');
        if (plus >= 0)
            trimmed = trimmed[..plus];

        var parts = trimmed.Split('.');
        if (parts.Length < 2 || parts.Length > 3)
            return false;

        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor))
            return false;

        var patch = 0;
        if (parts.Length == 3 && !int.TryParse(parts[2], out patch))
            return false;

        version = new SemanticVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
