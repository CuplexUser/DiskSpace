using System.Drawing;

namespace DiskSpace.App.Theme;

/// <summary>
/// The complete set of colour tokens for one theme. Controls never name a literal colour;
/// they read a token, so a theme switch is a single palette swap plus an invalidate.
/// </summary>
public sealed record Palette
{
    public required Color Bg { get; init; }
    public required Color Surface { get; init; }
    public required Color SurfaceAlt { get; init; }
    public required Color SurfaceHover { get; init; }
    public required Color Border { get; init; }
    public required Color BorderStrong { get; init; }
    public required Color Text { get; init; }
    public required Color TextMuted { get; init; }
    public required Color TextFaint { get; init; }
    public required Color Accent { get; init; }
    public required Color AccentHover { get; init; }
    public required Color AccentText { get; init; }
    public required Color RiskSafe { get; init; }
    public required Color RiskReview { get; init; }
    public required Color RiskAdvanced { get; init; }
    public required Color RiskReport { get; init; }
    public required Color BarTrack { get; init; }
    public required bool IsDark { get; init; }

    /// <summary>
    /// Categorical slots for the treemap, in fixed order. Cell area already encodes size, so
    /// colour carries identity instead: which subtree a region belongs to. Assigned in order
    /// and never cycled — past the eighth, cells fold into <see cref="SeriesOther"/>.
    /// </summary>
    public required Color[] Series { get; init; }

    /// <summary>Neutral fill for everything past the eighth categorical slot.</summary>
    public required Color SeriesOther { get; init; }

    public static readonly Palette Dark = new()
    {
        IsDark = true,
        Bg = Color.FromArgb(0x14, 0x16, 0x1A),
        Surface = Color.FromArgb(0x1B, 0x1E, 0x24),
        SurfaceAlt = Color.FromArgb(0x20, 0x24, 0x2B),
        SurfaceHover = Color.FromArgb(0x28, 0x2D, 0x36),
        Border = Color.FromArgb(0x2C, 0x31, 0x3A),
        BorderStrong = Color.FromArgb(0x3B, 0x42, 0x4D),
        Text = Color.FromArgb(0xE4, 0xE7, 0xEC),
        TextMuted = Color.FromArgb(0x95, 0x9C, 0xA8),
        TextFaint = Color.FromArgb(0x6B, 0x73, 0x80),
        Accent = Color.FromArgb(0x4C, 0x9A, 0xFF),
        AccentHover = Color.FromArgb(0x6D, 0xAD, 0xFF),
        AccentText = Color.FromArgb(0xFF, 0xFF, 0xFF),
        RiskSafe = Color.FromArgb(0x3F, 0xB9, 0x50),
        RiskReview = Color.FromArgb(0xD2, 0x99, 0x22),
        RiskAdvanced = Color.FromArgb(0xF8, 0x51, 0x49),
        RiskReport = Color.FromArgb(0x8B, 0x94, 0x9E),
        BarTrack = Color.FromArgb(0x27, 0x2B, 0x33),
        // Categorical slots stepped for a dark surface.
        Series =
        [
            Color.FromArgb(0x39, 0x87, 0xE5), // blue
            Color.FromArgb(0xD9, 0x59, 0x26), // orange
            Color.FromArgb(0x19, 0x9E, 0x70), // aqua
            Color.FromArgb(0xC9, 0x85, 0x00), // yellow
            Color.FromArgb(0xD5, 0x51, 0x81), // magenta
            Color.FromArgb(0x00, 0x83, 0x00), // green
            Color.FromArgb(0x90, 0x85, 0xE9), // violet
            Color.FromArgb(0xE6, 0x67, 0x67), // red
        ],
        SeriesOther = Color.FromArgb(0x5A, 0x62, 0x6E),
    };

    public static readonly Palette Light = new()
    {
        IsDark = false,
        Bg = Color.FromArgb(0xF4, 0xF6, 0xF8),
        Surface = Color.FromArgb(0xFF, 0xFF, 0xFF),
        SurfaceAlt = Color.FromArgb(0xF0, 0xF2, 0xF5),
        SurfaceHover = Color.FromArgb(0xE6, 0xEA, 0xEF),
        Border = Color.FromArgb(0xD8, 0xDC, 0xE2),
        BorderStrong = Color.FromArgb(0xBA, 0xC1, 0xCA),
        Text = Color.FromArgb(0x1A, 0x1D, 0x21),
        TextMuted = Color.FromArgb(0x5B, 0x64, 0x70),
        TextFaint = Color.FromArgb(0x86, 0x8F, 0x9B),
        Accent = Color.FromArgb(0x0B, 0x62, 0xD6),
        AccentHover = Color.FromArgb(0x27, 0x78, 0xE3),
        AccentText = Color.FromArgb(0xFF, 0xFF, 0xFF),
        RiskSafe = Color.FromArgb(0x1A, 0x7F, 0x37),
        RiskReview = Color.FromArgb(0x9A, 0x67, 0x00),
        RiskAdvanced = Color.FromArgb(0xCF, 0x22, 0x2E),
        RiskReport = Color.FromArgb(0x6E, 0x77, 0x81),
        BarTrack = Color.FromArgb(0xE4, 0xE8, 0xED),
        // The same eight hues, stepped for a light surface.
        Series =
        [
            Color.FromArgb(0x2A, 0x78, 0xD6), // blue
            Color.FromArgb(0xEB, 0x68, 0x34), // orange
            Color.FromArgb(0x1B, 0xAF, 0x7A), // aqua
            Color.FromArgb(0xED, 0xA1, 0x00), // yellow
            Color.FromArgb(0xE8, 0x7B, 0xA4), // magenta
            Color.FromArgb(0x00, 0x83, 0x00), // green
            Color.FromArgb(0x4A, 0x3A, 0xA7), // violet
            Color.FromArgb(0xE3, 0x49, 0x48), // red
        ],
        SeriesOther = Color.FromArgb(0x9A, 0xA3, 0xAE),
    };
}
