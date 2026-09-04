using System.Drawing.Text;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Rules;

namespace DiskSpace.App.Controls;

/// <summary>
/// Explains the highlighted finding.
///
/// The consequence text gets the most prominent position on the pane, because "what breaks if I
/// remove this" is the only question a person actually needs answered before deciding — and it
/// is the question most cleanup tools decline to answer.
/// </summary>
public sealed class FindingDetailPane : ThemedControl
{
    private const int Pad = 18;

    private CleanupFinding? _finding;

    public FindingDetailPane()
    {
        Dock = DockStyle.Fill;
    }

    public void Show(CleanupFinding? finding)
    {
        _finding = finding;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var palette = Palette;
        g.Clear(palette.Bg);

        if (_finding is null)
        {
            TextRenderer.DrawText(
                g, "Select a finding to see what it removes.", AppTheme.UiFont,
                new Rectangle(Pad, Pad, Width - (Pad * 2), 40), palette.TextFaint,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        var width = Width - (Pad * 2);
        var y = Pad;

        y = DrawTitle(g, palette, _finding, width, y);
        y = DrawStats(g, palette, _finding, width, y);
        y = DrawSection(g, palette, "WHAT THIS IS", _finding.Rule.Description, width, y);
        y = DrawSection(g, palette, "WHAT BREAKS", _finding.Rule.WhatBreaks, width, y, emphasis: true);

        if (_finding.Rule.Purge is { } purge)
        {
            y = DrawSection(
                g, palette, "CLEARED USING",
                $"{purge}\n\nThe tool's own command is preferred over deleting the files "
                + "directly, so its index stays consistent with what is on disk.",
                width, y);
        }

        DrawSection(g, palette, "LOCATION", _finding.Path, width, y, mono: true);
    }

    private static int DrawTitle(Graphics g, Palette palette, CleanupFinding finding, int width, int y)
    {
        var titleHeight = TextRenderer.MeasureText(
            g, finding.Rule.Name, AppTheme.HeadingFont, new Size(width, 200),
            TextFormatFlags.WordBreak).Height;

        TextRenderer.DrawText(
            g, finding.Rule.Name, AppTheme.HeadingFont,
            new Rectangle(Pad, y, width, titleHeight), palette.Text,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

        y += titleHeight + 8;

        var (label, color) = finding.Rule.Risk switch
        {
            RiskLevel.Safe => ("Safe: regenerates on demand", palette.RiskSafe),
            RiskLevel.Review => ("Review: quarantined, restorable", palette.RiskReview),
            RiskLevel.Advanced => ("Advanced: affects system state", palette.RiskAdvanced),
            _ => ("Information only: not removed", palette.RiskReport),
        };

        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, Pad, y + 4, 8, 8);

        TextRenderer.DrawText(
            g, label, AppTheme.UiFont, new Rectangle(Pad + 14, y, width - 14, 20), color,
            TextFormatFlags.NoPrefix);

        return y + 28;
    }

    private static int DrawStats(Graphics g, Palette palette, CleanupFinding finding, int width, int y)
    {
        var stats = new (string Label, string Value)[]
        {
            ("Size", ByteSize.Format(finding.Size)),
            ("Files", ByteSize.Count(finding.FileCount)),
            ("Last written", Age(finding.LastWriteUtc)),
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

        return y + 50;
    }

    private static string Age(DateTime lastWriteUtc)
    {
        if (lastWriteUtc == default)
            return "unknown";

        var days = (int)(DateTime.UtcNow - lastWriteUtc).TotalDays;
        return days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 60 => $"{days} days ago",
            < 730 => $"{days / 30} months ago",
            _ => $"{days / 365} years ago",
        };
    }

    private int DrawSection(
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
