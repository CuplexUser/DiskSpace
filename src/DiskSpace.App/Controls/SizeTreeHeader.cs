using System.Drawing.Text;
using DiskSpace.App.Theme;

namespace DiskSpace.App.Controls;

/// <summary>
/// Clickable column header for <see cref="SizeTreeView"/>. A scan settles into a size-sorted
/// tree on its own, but "biggest first" is not the only order worth reading a tree in, so this
/// lets the user ask for name order too, or flip either one around.
/// </summary>
public sealed class SizeTreeHeader : ThemedControl
{
    private SortColumn _sortColumn = SortColumn.Size;
    private bool _sortDescending = true;
    private SortColumn? _hover;

    public SizeTreeHeader()
    {
        Height = 28;
        Dock = DockStyle.Top;
        Cursor = Cursors.Hand;
    }

    /// <summary>Raised when the user clicks a column to sort by it.</summary>
    public event EventHandler<(SortColumn Column, bool Descending)>? SortRequested;

    private (Rectangle Name, Rectangle Size) ColumnBounds()
    {
        var rightEdge = ClientSize.Width - SizeTreeView.RightPadding;
        var percentX = rightEdge - SizeTreeView.PercentWidth;
        var sizeX = percentX - SizeTreeView.SizeWidth;
        var barX = sizeX - SizeTreeView.BarWidth - 10;
        var nameStart = 8 + SizeTreeView.ChevronWidth + SizeTreeView.IconWidth;

        var name = new Rectangle(nameStart, 0, Math.Max(0, barX - 8 - nameStart), Height);
        var size = new Rectangle(sizeX, 0, SizeTreeView.SizeWidth, Height);
        return (name, size);
    }

    private SortColumn? ColumnAt(Point location)
    {
        var (name, size) = ColumnBounds();
        return name.Contains(location) ? SortColumn.Name
            : size.Contains(location) ? SortColumn.Size
            : null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var hover = ColumnAt(e.Location);
        if (hover == _hover)
            return;

        _hover = hover;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hover is null)
            return;

        _hover = null;
        Invalidate();
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        if (ColumnAt(e.Location) is not { } column)
            return;

        // Clicking the column that is already active flips its direction. Clicking a different
        // one starts at whichever direction someone actually wants first: biggest folders, but
        // names read A to Z.
        _sortDescending = column == _sortColumn ? !_sortDescending : column == SortColumn.Size;
        _sortColumn = column;

        Invalidate();
        SortRequested?.Invoke(this, (_sortColumn, _sortDescending));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = Palette;
        g.Clear(palette.Surface);

        using (var pen = new Pen(palette.Border))
            g.DrawLine(pen, 0, Height - 1, ClientSize.Width, Height - 1);

        var (name, size) = ColumnBounds();
        DrawColumn(g, palette, "Name", name, SortColumn.Name, TextFormatFlags.Left);
        DrawColumn(g, palette, "Size", size, SortColumn.Size, TextFormatFlags.Right);
    }

    private void DrawColumn(
        Graphics g, Palette palette, string label, Rectangle bounds, SortColumn column, TextFormatFlags align)
    {
        var active = column == _sortColumn;
        var color = active ? palette.Text : _hover == column ? palette.TextMuted : palette.TextFaint;
        var font = active ? AppTheme.UiFontBold : AppTheme.UiFont;
        var arrow = active ? (_sortDescending ? " ▼" : " ▲") : string.Empty;

        TextRenderer.DrawText(
            g, label + arrow, font, bounds, color,
            TextFormatFlags.VerticalCenter | align | TextFormatFlags.NoPrefix);
    }
}
