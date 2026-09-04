namespace DiskSpace.App.Theme;

/// <summary>
/// Icon code points from Segoe Fluent Icons / Segoe MDL2 Assets, kept in one place because
/// they are private-use characters: unreadable in source, and easy to lose to a re-encode.
///
/// Each was verified by rendering a sampler rather than trusted from a name — the code points
/// are not mnemonic, and neighbours look nothing like each other (U+E8B7, the obvious guess
/// for "folder", draws a page).
/// </summary>
internal static class Glyphs
{
    /// <summary>Magnifier.</summary>
    public const string Scan = "";

    /// <summary>Folder.</summary>
    public const string Folder = "";

    /// <summary>Four tiles in a grid, the shape Windows uses for an application.</summary>
    public const string Programs = "";

    /// <summary>Filing drawer.</summary>
    public const string Quarantine = "";

    /// <summary>Clock with a counter-clockwise arrow.</summary>
    public const string History = "";

    /// <summary>Gear.</summary>
    public const string Settings = "";

    /// <summary>Filled shield.</summary>
    public const string Shield = "";

    /// <summary>Warning triangle.</summary>
    public const string Warning = "";
}
