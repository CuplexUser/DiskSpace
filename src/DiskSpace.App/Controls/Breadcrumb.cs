using System.Drawing.Text;
using DiskSpace.App.Theme;
using DiskSpace.Core.Scanning;

namespace DiskSpace.App.Controls;

/// <summary>
/// Clickable path trail for the treemap. Segments are built by walking parent links, so the
/// trail always stops at the scanned root rather than running up to the volume.
/// </summary>
public sealed class Breadcrumb : ThemedControl
{
    private const int EdgePadding = 8;

    private readonly List<(DirectoryNode Node, Rectangle Bounds)> _segments = [];
    private DirectoryNode? _leaf;
    private int _hoverIndex = -1;

    public Breadcrumb()
    {
        Height = 30;
        Dock = DockStyle.Top;
    }

    public event EventHandler<DirectoryNode>? SegmentClicked;

    public void SetPath(DirectoryNode? leaf)
    {
        _leaf = leaf;
        _hoverIndex = -1;
        Invalidate();
    }

    private void Rebuild(Graphics g)
    {
        _segments.Clear();
        if (_leaf is null)
            return;

        var chain = new List<DirectoryNode>();
        for (var node = _leaf; node is not null; node = node.Parent)
            chain.Add(node);
        chain.Reverse();

        var x = EdgePadding + 4;
        foreach (var node in chain)
        {
            var text = node.Parent is null ? node.Path : node.Name;
            var width = (int)Math.Ceiling(g.MeasureString(text, AppTheme.UiFont).Width);
            _segments.Add((node, new Rectangle(x, 0, width, Height)));
            x += width + 18; // room for the separator chevron
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = _segments.FindIndex(s => s.Bounds.Contains(e.Location));
        if (index == _hoverIndex)
            return;

        _hoverIndex = index;
        Cursor = index >= 0 && index < _segments.Count - 1 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        var index = _segments.FindIndex(s => s.Bounds.Contains(e.Location));
        if (index >= 0)
            SegmentClicked?.Invoke(this, _segments[index].Node);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = Palette;
        g.Clear(palette.Bg);
        Rebuild(g);

        for (var i = 0; i < _segments.Count; i++)
        {
            var (node, bounds) = _segments[i];
            var isLast = i == _segments.Count - 1;
            var color = isLast
                ? palette.Text
                : i == _hoverIndex ? palette.Accent : palette.TextMuted;

            var text = node.Parent is null ? node.Path : node.Name;
            TextRenderer.DrawText(
                g, text, isLast ? AppTheme.UiFontBold : AppTheme.UiFont, bounds, color,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix);

            if (isLast)
                continue;

            using var pen = new Pen(palette.TextFaint, 1.3f);
            var cx = bounds.Right + 7;
            var cy = Height / 2f;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawLines(pen,
            [
                new PointF(cx - 2, cy - 4),
                new PointF(cx + 2, cy),
                new PointF(cx - 2, cy + 4),
            ]);
        }
    }
}
