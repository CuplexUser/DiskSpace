using System.Drawing.Text;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Programs;

namespace DiskSpace.App.Controls;

/// <summary>
/// Explains the highlighted program: where its bytes are, and what removing it would mean.
///
/// Shaped like <see cref="FindingDetailPane"/>, and for the same reason: the number is not the
/// interesting part. Where a program keeps its data, whether the size shown was measured or
/// merely claimed by its installer, and who does the removing all matter more.
/// </summary>
public sealed class ProgramDetailPane : ThemedControl
{
    private const int Pad = 18;

    private readonly AccentButton _uninstall = new();
    private ProgramFootprint? _footprint;

    public ProgramDetailPane()
    {
        Dock = DockStyle.Fill;

        _uninstall.Text = "Uninstall…";
        _uninstall.Kind = ButtonKind.Danger;
        _uninstall.Width = 112;
        _uninstall.Visible = false;
        _uninstall.Click += (_, _) =>
        {
            if (_footprint is { } footprint)
                UninstallRequested?.Invoke(this, footprint);
        };

        Controls.Add(_uninstall);
    }

    /// <summary>Raised when the button is pressed. The page owns the confirmation.</summary>
    public event EventHandler<ProgramFootprint>? UninstallRequested;

    public void Show(ProgramFootprint? footprint)
    {
        _footprint = footprint;

        _uninstall.Visible = footprint is not null
                             && ProgramUninstaller.CanUninstall(footprint.Program);

        _uninstall.Location = new Point(Pad, Pad);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var palette = Palette;
        g.Clear(palette.Bg);

        if (_footprint is null)
        {
            TextRenderer.DrawText(
                g, "Select a program to see where its space goes.", AppTheme.UiFont,
                new Rectangle(Pad, Pad, Width - (Pad * 2), 40), palette.TextFaint,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        var width = Width - (Pad * 2);
        var y = Pad;

        // The button is laid out first so the text below it knows where to start.
        if (_uninstall.Visible)
        {
            _uninstall.Location = new Point(Pad + width - _uninstall.Width, Pad);
            width -= _uninstall.Width + 12;
        }

        y = DrawTitle(g, palette, _footprint, width, y);
        width = Width - (Pad * 2);
        y = DrawStats(g, palette, _footprint, width, y);
        y = DrawParts(g, palette, _footprint, width, y);

        if (_footprint.Program.Remedy is { Length: > 0 } remedy)
            y = DrawSection(g, palette, "HOW TO RECLAIM IT", remedy, width, y, emphasis: true);

        if (_footprint.Program.Note is { Length: > 0 } note)
            y = DrawSection(g, palette, "WORTH KNOWING", note, width, y);

        if (ProgramUninstaller.CanUninstall(_footprint.Program))
        {
            DrawSection(
                g, palette, "REMOVED BY",
                ProgramUninstaller.Describe(_footprint.Program)
                + "\n\nDiskSpace does not delete program files itself. It starts the "
                + "uninstaller that the program shipped, which is the only thing that knows "
                + "what else it registered.",
                width, y, mono: true);
        }
    }

    private static int DrawTitle(
        Graphics g, Palette palette, ProgramFootprint footprint, int width, int y)
    {
        var program = footprint.Program;

        var titleHeight = TextRenderer.MeasureText(
            g, program.Name, AppTheme.HeadingFont, new Size(width, 200),
            TextFormatFlags.WordBreak).Height;

        TextRenderer.DrawText(
            g, program.Name, AppTheme.HeadingFont,
            new Rectangle(Pad, y, width, titleHeight), palette.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        y += titleHeight + 6;

        var subtitle = string.Join("  ·  ", new[]
        {
            program.Publisher,
            program.Version,
            program.InstallDate?.ToString("d MMM yyyy", null),
        }.Where(part => !string.IsNullOrWhiteSpace(part)));

        if (subtitle.Length > 0)
        {
            TextRenderer.DrawText(
                g, subtitle, AppTheme.UiFontSmall,
                new Rectangle(Pad, y, width, 18), palette.TextMuted, TextFormatFlags.NoPrefix);

            y += 22;
        }

        var (label, color) = Describe(program.Source, palette);

        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, Pad, y + 4, 8, 8);

        TextRenderer.DrawText(
            g, label, AppTheme.UiFont, new Rectangle(Pad + 14, y, width - 14, 20), color,
            TextFormatFlags.NoPrefix);

        return y + 28;
    }

    private static (string Label, Color Color) Describe(ProgramSource source, Palette palette) =>
        source switch
        {
            ProgramSource.Registry => ("Installed program", palette.RiskReview),
            ProgramSource.StorePackage => ("Store app", palette.RiskReview),
            ProgramSource.UserInstall => ("In your profile, unregistered", palette.RiskReview),
            _ => ("Part of Windows: reported, never removed", palette.RiskReport),
        };

    private static int DrawStats(
        Graphics g, Palette palette, ProgramFootprint footprint, int width, int y)
    {
        var stats = new (string Label, string Value)[]
        {
            ("Program", ByteSize.Format(footprint.InstallSize)),
            ("Data", ByteSize.Format(footprint.DataSize)),
            ("Total", footprint.SizeIsEstimated
                ? "~" + ByteSize.Format(footprint.TotalSize)
                : ByteSize.Format(footprint.TotalSize)),
        };

        var columnWidth = width / stats.Length;

        for (var i = 0; i < stats.Length; i++)
        {
            var x = Pad + (i * columnWidth);

            TextRenderer.DrawText(
                g, stats[i].Label.ToUpperInvariant(), AppTheme.UiFontSmall,
                new Rectangle(x, y, columnWidth, 16), palette.TextFaint, TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(
                g, stats[i].Value, AppTheme.TitleFont,
                new Rectangle(x, y + 16, columnWidth, 22), palette.Text, TextFormatFlags.NoPrefix);
        }

        y += 46;

        if (footprint.SizeIsEstimated)
        {
            TextRenderer.DrawText(
                g,
                "Nothing here could be measured, so this is the size the installer claimed.",
                AppTheme.UiFontSmall,
                new Rectangle(Pad, y, width, 18), palette.RiskReview, TextFormatFlags.NoPrefix);

            y += 20;
        }

        return y + 4;
    }

    /// <summary>
    /// The measured paths themselves. Worth showing rather than summing away: a program whose
    /// data folder dwarfs its install folder is a different decision from one that does not.
    /// </summary>
    private static int DrawParts(
        Graphics g, Palette palette, ProgramFootprint footprint, int width, int y)
    {
        if (footprint.Parts.Count == 0)
        {
            return DrawSection(
                g, palette, "LOCATIONS",
                "Nothing on disk could be attributed to this entry. Its installer recorded no "
                + "location, and no folder matching its name was found.",
                width, y);
        }

        using (var separator = new Pen(palette.Border))
            g.DrawLine(separator, Pad, y, Pad + width, y);

        y += 12;

        TextRenderer.DrawText(
            g, "LOCATIONS", AppTheme.UiFontSmall, new Rectangle(Pad, y, width, 16),
            palette.TextFaint, TextFormatFlags.NoPrefix);

        y += 20;

        foreach (var part in footprint.Parts)
        {
            var value = part.Error ?? ByteSize.Format(part.Size);
            var color = part.Error is null ? palette.Text : palette.RiskReview;

            TextRenderer.DrawText(
                g, value, AppTheme.UiFont, new Rectangle(Pad, y, 110, 18), color,
                TextFormatFlags.Right | TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(
                g, part.Path, AppTheme.MonoFont,
                new Rectangle(Pad + 122, y, width - 122, 18), palette.TextMuted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            y += 20;
        }

        return y + 12;
    }

    private static int DrawSection(
        Graphics g,
        Palette palette,
        string heading,
        string body,
        int width,
        int y,
        bool emphasis = false,
        bool mono = false)
    {
        using (var separator = new Pen(palette.Border))
            g.DrawLine(separator, Pad, y, Pad + width, y);

        y += 12;

        TextRenderer.DrawText(
            g, heading, AppTheme.UiFontSmall,
            new Rectangle(Pad, y, width, 16),
            emphasis ? palette.RiskReview : palette.TextFaint,
            TextFormatFlags.NoPrefix);

        y += 20;

        var font = mono ? AppTheme.MonoFont : AppTheme.UiFont;
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
        var height = TextRenderer.MeasureText(g, body, font, new Size(width, 600), flags).Height;

        TextRenderer.DrawText(
            g, body, font, new Rectangle(Pad, y, width, height),
            emphasis ? palette.Text : palette.TextMuted, flags);

        return y + height + 18;
    }
}
