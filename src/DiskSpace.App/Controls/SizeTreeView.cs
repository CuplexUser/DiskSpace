using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DiskSpace.App.Platform;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Scanning;

namespace DiskSpace.App.Controls;

/// <summary>
/// A directory tree with an inline proportional size bar on every row.
///
/// Built on the native <see cref="TreeView"/> rather than a custom scroller, because the
/// native control already handles scrolling, keyboard navigation and accessibility. Children
/// are materialised on expand, so loading a scan of 60,000 directories costs one row.
/// </summary>
public sealed class SizeTreeView : TreeView
{
    private const int IndentWidth = 18;
    private const int ChevronWidth = 16;
    private const int IconWidth = 22;
    private const int BarWidth = 110;
    private const int SizeWidth = 82;
    private const int PercentWidth = 52;
    private const int RightPadding = 12;

    private static readonly object LazyPlaceholder = new();

    private readonly Font _iconFont = FontResolver.Icons(10f);
    private DirectoryNode? _root;

    public SizeTreeView()
    {
        DrawMode = TreeViewDrawMode.OwnerDrawAll;
        FullRowSelect = true;
        ShowLines = false;
        ShowPlusMinus = false;
        ShowRootLines = false;
        HideSelection = false;
        ItemHeight = 24;
        Indent = IndentWidth;
        BorderStyle = BorderStyle.None;

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        AppTheme.Changed += OnThemeChanged;
    }

    /// <summary>Raised when a row is double-clicked or activated with Enter.</summary>
    public event EventHandler<DirectoryNode>? NodeActivated;

    /// <summary>Raised whenever the highlighted row changes.</summary>
    public event EventHandler<DirectoryNode>? NodeHighlighted;

    public DirectoryNode? SelectedDirectory => SelectedNode?.Tag as DirectoryNode;

    public void Load(DirectoryNode root)
    {
        _root = root;
        BeginUpdate();
        try
        {
            Nodes.Clear();
            var node = CreateNode(root);
            Nodes.Add(node);
            node.Expand();
        }
        finally
        {
            EndUpdate();
        }

        SelectedNode = Nodes.Count > 0 ? Nodes[0] : null;
    }

    private static TreeNode CreateNode(DirectoryNode directory)
    {
        var node = new TreeNode(directory.Name) { Tag = directory };

        // A stub child makes the row expandable without walking the subtree now.
        if (directory.Children.Count > 0)
            node.Nodes.Add(new TreeNode { Tag = LazyPlaceholder });

        return node;
    }

    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        base.OnBeforeExpand(e);

        if (e.Node?.Tag is not DirectoryNode directory)
            return;

        if (e.Node.Nodes.Count != 1 || e.Node.Nodes[0].Tag != LazyPlaceholder)
            return;

        BeginUpdate();
        try
        {
            e.Node.Nodes.Clear();
            foreach (var child in directory.ChildrenBySize)
                e.Node.Nodes.Add(CreateNode(child));
        }
        finally
        {
            EndUpdate();
        }
    }

    protected override void OnAfterSelect(TreeViewEventArgs e)
    {
        base.OnAfterSelect(e);
        if (e.Node?.Tag is DirectoryNode directory)
            NodeHighlighted?.Invoke(this, directory);
    }

    /// <summary>
    /// A click on the chevron or the folder icon expands the row, which is what those glyphs
    /// look like they do. The name and the rest of the row keep selecting, and double-click
    /// still drills in.
    /// </summary>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left)
            return;

        var node = GetNodeAt(e.Location);
        if (node?.Tag is not DirectoryNode || node.Nodes.Count == 0)
            return;

        var start = 8 + (node.Level * IndentWidth);
        if (e.X >= start && e.X < start + ChevronWidth + IconWidth)
            node.Toggle();
    }

    protected override void OnNodeMouseDoubleClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseDoubleClick(e);
        if (e.Node?.Tag is DirectoryNode directory)
            NodeActivated?.Invoke(this, directory);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Enter && SelectedDirectory is { } directory)
        {
            NodeActivated?.Invoke(this, directory);
            e.Handled = true;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeTheme();
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(() => OnThemeChanged(sender, e));
            return;
        }

        ApplyNativeTheme();
        Invalidate();
    }

    private void ApplyNativeTheme()
    {
        if (!IsHandleCreated)
            return;

        BackColor = AppTheme.Current.Surface;
        // Without this the scrollbar stays bright white inside a dark window.
        NativeMethods.ApplyExplorerTheme(Handle, AppTheme.Current.IsDark);
    }

    protected override void OnDrawNode(DrawTreeNodeEventArgs e)
    {
        e.DrawDefault = false;

        if (e.Node?.Tag is not DirectoryNode directory)
            return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = AppTheme.Current;
        var row = new Rectangle(0, e.Bounds.Y, ClientSize.Width, e.Bounds.Height);
        var selected = (e.State & TreeNodeStates.Selected) != 0;

        using (var background = new SolidBrush(selected ? palette.SurfaceHover : palette.Surface))
            g.FillRectangle(background, row);

        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            g.FillRectangle(accent, new Rectangle(0, row.Y, 2, row.Height));
        }

        var x = 8 + (e.Node.Level * IndentWidth);
        x = PaintChevron(g, palette, e.Node, x, row);
        PaintIcon(g, palette, directory, ref x, row);

        var rightEdge = row.Right - RightPadding;
        var percentX = rightEdge - PercentWidth;
        var sizeX = percentX - SizeWidth;
        var barX = sizeX - BarWidth - 10;

        PaintName(g, palette, directory, x, barX - 8 - x, row, selected);
        PaintBar(g, palette, directory, barX, row);
        PaintSize(g, palette, directory, sizeX, row);
        PaintPercent(g, palette, directory, percentX, row);
    }

    private static int PaintChevron(
        Graphics g, Palette palette, TreeNode node, int x, Rectangle row)
    {
        if (node.Nodes.Count == 0)
            return x + ChevronWidth;

        // A simple triangle rotated by expansion state, rather than the native +/- boxes.
        var centerY = row.Y + (row.Height / 2f);
        var points = node.IsExpanded
            ? new[]
            {
                new PointF(x + 3, centerY - 2),
                new PointF(x + 11, centerY - 2),
                new PointF(x + 7, centerY + 3),
            }
            : new[]
            {
                new PointF(x + 5, centerY - 4),
                new PointF(x + 10, centerY),
                new PointF(x + 5, centerY + 4),
            };

        using var brush = new SolidBrush(palette.TextFaint);
        g.FillPolygon(brush, points);
        return x + ChevronWidth;
    }

    private void PaintIcon(
        Graphics g, Palette palette, DirectoryNode directory, ref int x, Rectangle row)
    {
        // A junction is dimmed rather than filled, since its bytes are counted where they
        // really live rather than here.
        var color = directory.Error is not null
            ? palette.RiskAdvanced
            : directory.IsReparsePoint
                ? palette.TextFaint
                : palette.Accent;

        using var brush = new SolidBrush(color);
        var size = g.MeasureString(Glyphs.Folder, _iconFont);
        g.DrawString(
            Glyphs.Folder, _iconFont, brush,
            new PointF(x, row.Y + ((row.Height - size.Height) / 2f)));

        x += IconWidth;
    }

    private static void PaintName(
        Graphics g,
        Palette palette,
        DirectoryNode directory,
        int x,
        int width,
        Rectangle row,
        bool selected)
    {
        if (width <= 20)
            return;

        var color = directory.Error is not null ? palette.TextFaint : palette.Text;
        var font = selected ? AppTheme.UiFontBold : AppTheme.UiFont;
        var text = directory.Error is not null
            ? directory.Name + "  (unreadable)"
            : directory.Name;

        TextRenderer.DrawText(
            g,
            text,
            font,
            new Rectangle(x, row.Y, width, row.Height),
            color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private void PaintBar(Graphics g, Palette palette, DirectoryNode directory, int x, Rectangle row)
    {
        var parentTotal = directory.Parent?.TotalSize ?? _root?.TotalSize ?? 0;
        if (parentTotal <= 0)
            return;

        var fraction = Math.Clamp((double)directory.TotalSize / parentTotal, 0, 1);
        var track = new Rectangle(x, row.Y + (row.Height / 2) - 4, BarWidth, 8);

        using (var trackBrush = new SolidBrush(palette.BarTrack))
        using (var trackPath = RoundedRect(track, 4))
            g.FillPath(trackBrush, trackPath);

        var filledWidth = (int)Math.Round(track.Width * fraction);
        if (filledWidth < 2 && directory.TotalSize > 0)
            filledWidth = 2;
        if (filledWidth <= 0)
            return;

        // Colour by share of the parent: a child taking most of its parent is the thing worth
        // looking at, so it reads hot rather than neutral.
        var color = fraction switch
        {
            >= 0.5 => palette.Accent,
            >= 0.2 => Blend(palette.Accent, palette.TextFaint, 0.35f),
            _ => palette.TextFaint,
        };

        using var brush = new SolidBrush(color);
        using var path = RoundedRect(new Rectangle(track.X, track.Y, filledWidth, track.Height), 4);
        g.FillPath(brush, path);
    }

    private static void PaintSize(
        Graphics g, Palette palette, DirectoryNode directory, int x, Rectangle row)
    {
        TextRenderer.DrawText(
            g,
            ByteSize.Format(directory.TotalSize),
            AppTheme.UiFont,
            new Rectangle(x, row.Y, SizeWidth, row.Height),
            palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    private void PaintPercent(
        Graphics g, Palette palette, DirectoryNode directory, int x, Rectangle row)
    {
        var total = _root?.TotalSize ?? 0;
        if (total <= 0)
            return;

        var percent = directory.TotalSize * 100.0 / total;
        var text = percent >= 10 ? $"{percent:0}%" : percent >= 0.1 ? $"{percent:0.0}%" : "-";

        TextRenderer.DrawText(
            g,
            text,
            AppTheme.UiFont,
            new Rectangle(x, row.Y, PercentWidth, row.Height),
            palette.TextMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    private static Color Blend(Color a, Color b, float amount) => Color.FromArgb(
        (int)(a.R + ((b.R - a.R) * amount)),
        (int)(a.G + ((b.G - a.G) * amount)),
        (int)(a.B + ((b.B - a.B) * amount)));

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);

        if (diameter >= bounds.Width || diameter >= bounds.Height)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            AppTheme.Changed -= OnThemeChanged;
            _iconFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
