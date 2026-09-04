using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DiskSpace.App.Theme;

namespace DiskSpace.App.Controls;

public sealed class NavItem(string key, string label, string glyph)
{
    public string Key { get; } = key;
    public string Label { get; } = label;

    /// <summary>A Segoe Fluent Icons / MDL2 code point, e.g. "".</summary>
    public string Glyph { get; } = glyph;

    /// <summary>Shown as a pill on the right of the row when non-zero.</summary>
    public int Badge { get; set; }
}

/// <summary>
/// The left navigation rail. Keyboard navigable, because a tool aimed at people who live in
/// Sysinternals should not require the mouse.
/// </summary>
public sealed class NavRail : ThemedControl
{
    private const int RowHeight = 38;
    private const int HeaderHeight = 68;
    private const int FooterHeight = 34;

    private const string ShieldGlyph = Glyphs.Shield;
    private const string WarningGlyph = Glyphs.Warning;

    private readonly List<NavItem> _items = [];
    private readonly Font _glyphFont;
    private int _selectedIndex;
    private int _hoverIndex = -1;

    public NavRail()
    {
        Width = 208;
        Dock = DockStyle.Left;
        TabStop = true;
        _glyphFont = CreateGlyphFont(12f);
    }

    public event EventHandler<NavItem>? SelectionChanged;

    public IReadOnlyList<NavItem> Items => _items;

    public string? SelectedKey =>
        _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex].Key : null;

    public void AddItem(NavItem item)
    {
        _items.Add(item);
        Invalidate();
    }

    public void Select(string key)
    {
        var index = _items.FindIndex(i => i.Key == key);
        if (index < 0 || index == _selectedIndex)
            return;

        _selectedIndex = index;
        Invalidate();
        SelectionChanged?.Invoke(this, _items[index]);
    }

    /// <summary>Raises the initial selection event so the shell can show a first page.</summary>
    public void SelectFirst()
    {
        if (_items.Count == 0)
            return;

        _selectedIndex = 0;
        Invalidate();
        SelectionChanged?.Invoke(this, _items[0]);
    }

    public void SetBadge(string key, int count)
    {
        var item = _items.Find(i => i.Key == key);
        if (item is null || item.Badge == count)
            return;

        item.Badge = count;
        Invalidate();
    }

    private static Font CreateGlyphFont(float size) => FontResolver.Icons(size);

    private int IndexAt(Point point)
    {
        if (point.Y < HeaderHeight)
            return -1;

        var index = (point.Y - HeaderHeight) / RowHeight;
        return index >= 0 && index < _items.Count ? index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = IndexAt(e.Location);
        if (index == _hoverIndex)
            return;

        _hoverIndex = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverIndex = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        var index = IndexAt(e.Location);
        if (index >= 0)
            Select(_items[index].Key);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        var target = e.KeyCode switch
        {
            Keys.Up => _selectedIndex - 1,
            Keys.Down => _selectedIndex + 1,
            Keys.Home => 0,
            Keys.End => _items.Count - 1,
            _ => _selectedIndex,
        };

        if (target == _selectedIndex || target < 0 || target >= _items.Count)
            return;

        Select(_items[target].Key);
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = Palette;
        g.Clear(palette.Surface);

        using (var border = new Pen(palette.Border))
            g.DrawLine(border, Width - 1, 0, Width - 1, Height);

        PaintHeader(g, palette);

        for (var i = 0; i < _items.Count; i++)
            PaintRow(g, palette, _items[i], i);

        PaintFooter(g, palette);
    }

    private static void PaintHeader(Graphics g, Palette palette)
    {
        using var titleBrush = new SolidBrush(palette.Text);
        using var subtitleBrush = new SolidBrush(palette.TextFaint);

        g.DrawString("DiskSpace", AppTheme.HeadingFont, titleBrush, new PointF(18, 16));
        g.DrawString("Disk reclamation", AppTheme.UiFontSmall, subtitleBrush, new PointF(19, 42));
    }

    private void PaintRow(Graphics g, Palette palette, NavItem item, int index)
    {
        var bounds = new Rectangle(0, HeaderHeight + (index * RowHeight), Width - 1, RowHeight);
        var selected = index == _selectedIndex;
        var hovered = index == _hoverIndex;

        if (selected || hovered)
        {
            using var brush = new SolidBrush(selected ? palette.SurfaceAlt : palette.SurfaceHover);
            using var path = RoundedRect(
                new Rectangle(8, bounds.Y + 2, bounds.Width - 16, RowHeight - 4), 5);
            g.FillPath(brush, path);
        }

        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            using var path = RoundedRect(new Rectangle(8, bounds.Y + 9, 3, RowHeight - 18), 2);
            g.FillPath(accent, path);
        }

        using var glyphBrush = new SolidBrush(selected ? palette.Accent : palette.TextMuted);
        using var textBrush = new SolidBrush(selected ? palette.Text : palette.TextMuted);

        var glyphSize = g.MeasureString(item.Glyph, _glyphFont);
        g.DrawString(item.Glyph, _glyphFont, glyphBrush,
            new PointF(24, bounds.Y + ((RowHeight - glyphSize.Height) / 2f)));

        var font = selected ? AppTheme.UiFontBold : AppTheme.UiFont;
        var textSize = g.MeasureString(item.Label, font);
        g.DrawString(item.Label, font, textBrush,
            new PointF(52, bounds.Y + ((RowHeight - textSize.Height) / 2f)));

        if (item.Badge > 0)
            PaintBadge(g, palette, bounds, item.Badge);
    }

    private void PaintBadge(Graphics g, Palette palette, Rectangle row, int count)
    {
        var text = count > 99 ? "99+" : count.ToString();
        var size = g.MeasureString(text, AppTheme.UiFontSmall);
        var width = (int)Math.Max(20, size.Width + 12);
        var rect = new Rectangle(Width - 20 - width, row.Y + ((RowHeight - 18) / 2), width, 18);

        using var brush = new SolidBrush(palette.RiskReview);
        using var path = RoundedRect(rect, 9);
        g.FillPath(brush, path);

        using var textBrush = new SolidBrush(palette.IsDark ? palette.Bg : Color.White);
        g.DrawString(text, AppTheme.UiFontSmall, textBrush,
            new PointF(rect.X + ((rect.Width - size.Width) / 2f),
                       rect.Y + ((rect.Height - size.Height) / 2f)));
    }

    private void PaintFooter(Graphics g, Palette palette)
    {
        var elevated = Platform.NativeMethods.IsElevated();
        var y = Height - FooterHeight;

        using (var border = new Pen(palette.Border))
            g.DrawLine(border, 12, y, Width - 13, y);

        // The app always requests elevation, so this normally reads as quiet confirmation.
        // It turns amber only when elevation was somehow not granted.
        var glyph = elevated ? ShieldGlyph : WarningGlyph;
        var label = elevated ? "Administrator" : "Not elevated";

        using var brush = new SolidBrush(elevated ? palette.TextFaint : palette.RiskReview);
        using var smallGlyphFont = CreateGlyphFont(8f);
        g.DrawString(glyph, smallGlyphFont, brush, new PointF(18, y + 10));
        g.DrawString(label, AppTheme.UiFontSmall, brush, new PointF(36, y + 9));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _glyphFont.Dispose();

        base.Dispose(disposing);
    }
}
