using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Scanning;

namespace DiskSpace.App.Controls;

/// <summary>
/// A squarified treemap of one directory's children.
///
/// Area encodes size, so colour is free to carry identity instead: each top-level child of the
/// current directory takes the next categorical slot, and everything past the eighth folds into
/// a single neutral "Other" cell rather than cycling hues back to the start. Cells are directly
/// labelled wherever they are big enough, so identity never rests on colour alone.
/// </summary>
public sealed class TreeMapPanel : ThemedControl
{
    private const int CellGap = 2;
    private const int MaxSeries = 8;
    private const int MinLabelWidth = 54;
    private const int MinLabelHeight = 24;

    /// <summary>How much of a name has to survive truncation before it is worth drawing.</summary>
    private const int MinLabelCharacters = 7;

    private sealed record Cell(DirectoryNode? Node, RectangleF Bounds, Color Fill, string Label, long Size);

    private readonly List<Cell> _cells = [];

    /// <summary>
    /// Which series color each child was given. Colors are assigned by rank, and under a live
    /// scan the ranks churn, so recomputing them every second makes the map strobe. Remembering
    /// the first assignment lets a cell keep its identity while it grows.
    /// </summary>
    private readonly Dictionary<DirectoryNode, int> _slots = [];

    private DirectoryNode? _current;
    private Cell? _hover;
    private ToolTip? _toolTip;

    public TreeMapPanel()
    {
        Dock = DockStyle.Fill;
        _toolTip = new ToolTip { InitialDelay = 220, ReshowDelay = 100, ShowAlways = true };
    }

    /// <summary>Raised when a cell is double-clicked, to drill into that directory.</summary>
    public event EventHandler<DirectoryNode>? CellActivated;

    /// <summary>Raised when a cell is single-clicked.</summary>
    public event EventHandler<DirectoryNode>? CellSelected;

    public DirectoryNode? Current => _current;

    public void Show(DirectoryNode? directory)
    {
        if (!ReferenceEquals(directory, _current))
            _slots.Clear();

        _current = directory;
        _hover = null;
        Rebuild();
        Invalidate();
    }

    /// <summary>Re-lays the current directory against values a running scan has moved on.</summary>
    public void RefreshValues()
    {
        if (_current is null)
            return;

        _hover = null;
        Rebuild();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        Rebuild();
    }

    protected override void ApplyTheme() => Rebuild();

    private void Rebuild()
    {
        _cells.Clear();

        if (_current is null || ClientSize.Width < 20 || ClientSize.Height < 20)
            return;

        var palette = Palette;
        var children = _current.ChildrenBySize.Where(c => c.TotalSize > 0).ToList();

        // Files sitting directly in this directory are real space; without a cell for them a
        // directory of 10,000 loose files would look empty.
        var items = new List<(DirectoryNode? Node, long Size, string Label)>();
        foreach (var child in children.Take(MaxSeries))
            items.Add((child, child.TotalSize, child.Name));

        var overflow = children.Skip(MaxSeries).Sum(c => c.TotalSize);
        if (_current.OwnSize > 0)
            items.Add((null, _current.OwnSize, "(files here)"));
        if (overflow > 0)
            items.Add((null, overflow, $"Other ({children.Count - MaxSeries})"));

        if (items.Count == 0)
            return;

        items.Sort((a, b) => b.Size.CompareTo(a.Size));

        var colors = new Color[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            colors[i] = items[i].Node is { } node && SlotFor(node) is { } slot
                ? palette.Series[slot]
                : palette.SeriesOther;
        }

        var bounds = new RectangleF(0, 0, ClientSize.Width, ClientSize.Height);
        var laid = new List<(int Index, RectangleF Rect)>();
        Squarify(items.Select(i => (double)i.Size).ToList(), bounds, laid);

        foreach (var (index, rect) in laid)
        {
            var item = items[index];
            _cells.Add(new Cell(item.Node, rect, colors[index], item.Label, item.Size));
        }
    }

    /// <summary>
    /// The series slot this child holds, assigning the lowest free one the first time it is
    /// seen. Null once all of them are taken, which sends the child to the neutral color.
    /// </summary>
    private int? SlotFor(DirectoryNode node)
    {
        if (_slots.TryGetValue(node, out var existing))
            return existing;

        if (_slots.Count >= MaxSeries)
            return null;

        for (var slot = 0; slot < MaxSeries; slot++)
        {
            if (!_slots.ContainsValue(slot))
            {
                _slots[node] = slot;
                return slot;
            }
        }

        return null;
    }

    /// <summary>
    /// Squarified treemap layout (Bruls, Huizing &amp; van Wijk). Rows are accumulated while
    /// they improve the worst aspect ratio, then flushed against the shorter side — which is
    /// what keeps cells near-square and readable instead of turning into slivers.
    /// </summary>
    private static void Squarify(
        List<double> sizes, RectangleF bounds, List<(int Index, RectangleF Rect)> output)
    {
        var total = sizes.Sum();
        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        var scale = bounds.Width * (double)bounds.Height / total;
        var remaining = Enumerable.Range(0, sizes.Count).ToList();
        var rect = bounds;
        var row = new List<int>();

        while (remaining.Count > 0)
        {
            var next = remaining[0];
            var side = Math.Min(rect.Width, rect.Height);

            if (row.Count == 0 || Worst(row, next, side, sizes, scale) <= Worst(row, -1, side, sizes, scale))
            {
                row.Add(next);
                remaining.RemoveAt(0);
                continue;
            }

            rect = FlushRow(row, rect, sizes, scale, output);
            row.Clear();

            if (rect.Width <= 0 || rect.Height <= 0)
                return;
        }

        if (row.Count > 0)
            FlushRow(row, rect, sizes, scale, output);
    }

    private static double Worst(
        List<int> row, int extra, double side, List<double> sizes, double scale)
    {
        double sum = 0, max = 0, min = double.MaxValue;

        foreach (var index in row)
        {
            var area = sizes[index] * scale;
            sum += area;
            max = Math.Max(max, area);
            min = Math.Min(min, area);
        }

        if (extra >= 0)
        {
            var area = sizes[extra] * scale;
            sum += area;
            max = Math.Max(max, area);
            min = Math.Min(min, area);
        }

        if (sum <= 0 || min <= 0 || side <= 0)
            return double.MaxValue;

        var sumSquared = sum * sum;
        var sideSquared = side * side;
        return Math.Max(sideSquared * max / sumSquared, sumSquared / (sideSquared * min));
    }

    private static RectangleF FlushRow(
        List<int> row,
        RectangleF rect,
        List<double> sizes,
        double scale,
        List<(int Index, RectangleF Rect)> output)
    {
        var sum = row.Sum(i => sizes[i] * scale);
        if (sum <= 0)
            return rect;

        // Lay the row along whichever side is shorter, so cells stay square-ish.
        if (rect.Width >= rect.Height)
        {
            var thickness = (float)(sum / rect.Height);
            if (thickness <= 0)
                return rect;

            var y = rect.Y;
            foreach (var index in row)
            {
                var height = (float)(sizes[index] * scale / thickness);
                output.Add((index, new RectangleF(rect.X, y, thickness, height)));
                y += height;
            }

            return new RectangleF(
                rect.X + thickness, rect.Y, rect.Width - thickness, rect.Height);
        }
        else
        {
            var thickness = (float)(sum / rect.Width);
            if (thickness <= 0)
                return rect;

            var x = rect.X;
            foreach (var index in row)
            {
                var width = (float)(sizes[index] * scale / thickness);
                output.Add((index, new RectangleF(x, rect.Y, width, thickness)));
                x += width;
            }

            return new RectangleF(
                rect.X, rect.Y + thickness, rect.Width, rect.Height - thickness);
        }
    }

    private Cell? CellAt(Point point) =>
        _cells.FirstOrDefault(c => c.Bounds.Contains(point));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var cell = CellAt(e.Location);
        if (cell == _hover)
            return;

        _hover = cell;
        Cursor = cell?.Node is not null ? Cursors.Hand : Cursors.Default;

        if (cell is not null)
        {
            var tip = cell.Node is not null
                ? $"{cell.Node.Name}\n{ByteSize.Format(cell.Size)}  ·  " +
                  $"{ByteSize.Count(cell.Node.TotalFileCount)} files"
                : $"{cell.Label}\n{ByteSize.Format(cell.Size)}";
            _toolTip?.SetToolTip(this, tip);
        }
        else
        {
            _toolTip?.SetToolTip(this, string.Empty);
        }

        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (CellAt(e.Location)?.Node is { } node)
            CellSelected?.Invoke(this, node);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (CellAt(e.Location)?.Node is { } node)
            CellActivated?.Invoke(this, node);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = Palette;
        g.Clear(palette.Bg);

        if (_cells.Count == 0)
        {
            PaintEmpty(g, palette);
            return;
        }

        foreach (var cell in _cells)
            PaintCell(g, palette, cell);
    }

    private void PaintEmpty(Graphics g, Palette palette)
    {
        var message = _current is null ? "No scan yet" : "Nothing to show here";
        var size = g.MeasureString(message, AppTheme.UiFont);
        using var brush = new SolidBrush(palette.TextFaint);
        g.DrawString(message, AppTheme.UiFont, brush,
            new PointF((Width - size.Width) / 2f, (Height - size.Height) / 2f));
    }

    private void PaintCell(Graphics g, Palette palette, Cell cell)
    {
        // The gap is drawn as inset rather than a stroke, so adjacent fills never touch.
        var rect = RectangleF.Inflate(cell.Bounds, -CellGap / 2f, -CellGap / 2f);
        if (rect.Width <= 1 || rect.Height <= 1)
            return;

        var hovered = cell == _hover;
        var fill = hovered ? Lighten(cell.Fill, 0.18f) : cell.Fill;

        using (var brush = new SolidBrush(fill))
            g.FillRectangle(brush, rect);

        if (hovered)
        {
            using var ring = new Pen(palette.Text, 1.5f);
            g.DrawRectangle(ring, rect.X, rect.Y, rect.Width, rect.Height);
        }

        if (rect.Width < MinLabelWidth || rect.Height < MinLabelHeight)
            return;

        // Label ink is chosen for contrast against the fill, not from the fill itself.
        var ink = Luminance(fill) > 0.55 ? Color.FromArgb(0x14, 0x16, 0x1A) : Color.White;
        var textRect = Rectangle.Round(RectangleF.Inflate(rect, -7, -5));

        if (WorthDrawing(g, cell.Label, AppTheme.UiFontBold, textRect.Width))
        {
            TextRenderer.DrawText(
                g, cell.Label, AppTheme.UiFontBold,
                new Rectangle(textRect.X, textRect.Y, textRect.Width, 15),
                ink,
                TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        if (rect.Height < MinLabelHeight + 14)
            return;

        // A size is a number: half of one is not a smaller truth, it is a wrong one, so it is
        // drawn whole or not at all.
        var size = ByteSize.Format(cell.Size);
        if (!Fits(g, size, AppTheme.UiFontSmall, textRect.Width))
            return;

        TextRenderer.DrawText(
            g, size, AppTheme.UiFontSmall,
            new Rectangle(textRect.X, textRect.Y + 15, textRect.Width, 14),
            Color.FromArgb(205, ink),
            TextFormatFlags.Left | TextFormatFlags.NoPrefix);
    }

    private static bool Fits(Graphics g, string text, Font font, int width) =>
        TextRenderer.MeasureText(
            g, text, font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix).Width
        <= width;

    /// <summary>
    /// Whether an ellipsised label would still say anything. "Inno..." in a narrow cell is not
    /// a smaller label, it is noise over a color that the tree and the tooltip already name.
    /// </summary>
    private static bool WorthDrawing(Graphics g, string text, Font font, int width)
    {
        if (Fits(g, text, font, width))
            return true;

        return text.Length > MinLabelCharacters
               && Fits(g, string.Concat(text.AsSpan(0, MinLabelCharacters), "..."), font, width);
    }

    private static double Luminance(Color c) =>
        ((0.2126 * c.R) + (0.7152 * c.G) + (0.0722 * c.B)) / 255.0;

    private static Color Lighten(Color c, float amount) => Color.FromArgb(
        c.A,
        (int)Math.Min(255, c.R + ((255 - c.R) * amount)),
        (int)Math.Min(255, c.G + ((255 - c.G) * amount)),
        (int)Math.Min(255, c.B + ((255 - c.B) * amount)));

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip?.Dispose();
            _toolTip = null;
        }

        base.Dispose(disposing);
    }
}
