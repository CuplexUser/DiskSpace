using System.Drawing.Text;

namespace DiskSpace.App.Theme;

/// <summary>
/// Picks the first font family that is actually installed.
///
/// Constructing a <see cref="Font"/> with a missing family does not throw — GDI+ quietly
/// substitutes something else — and the resolved <c>Name</c> is not a dependable way to detect
/// that. So the installed set is enumerated once and consulted directly. This matters here
/// because the fallbacks are not cosmetic: Segoe UI Variable and Segoe Fluent Icons ship on
/// Windows 11, while Windows 10 has only Segoe UI and Segoe MDL2 Assets, and asking a text
/// font for an icon code point yields a row of empty boxes.
/// </summary>
internal static class FontResolver
{
    private static readonly HashSet<string> Installed = LoadInstalled();

    private static HashSet<string> LoadInstalled()
    {
        var families = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var collection = new InstalledFontCollection();
            foreach (var family in collection.Families)
                families.Add(family.Name);
        }
        catch (Exception)
        {
            // Enumeration failed; every lookup then falls through to the generic default.
        }

        return families;
    }

    public static bool IsInstalled(string family) => Installed.Contains(family);

    /// <summary>Creates a font from the first installed candidate, or a generic fallback.</summary>
    public static Font Create(IEnumerable<string> candidates, float size, FontStyle style)
    {
        foreach (var family in candidates)
        {
            if (!Installed.Contains(family))
                continue;

            try
            {
                return new Font(family, size, style);
            }
            catch (ArgumentException)
            {
                // Installed but unusable at this style; try the next candidate.
            }
        }

        return new Font(FontFamily.GenericSansSerif, size, style);
    }

    /// <summary>The UI text face: Windows 11's variable face, else the Windows 10 one.</summary>
    public static Font Ui(float size, FontStyle style) =>
        Create(["Segoe UI Variable Text", "Segoe UI"], size, style);

    /// <summary>The icon face. Both candidates share code points in the ranges used here.</summary>
    public static Font Icons(float size) =>
        Create(["Segoe Fluent Icons", "Segoe MDL2 Assets"], size, FontStyle.Regular);

    public static Font Mono(float size) =>
        Create(["Cascadia Mono", "Consolas", "Courier New"], size, FontStyle.Regular);
}
