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
/// are materialized on expand, so loading a scan of 60,000 directories costs one row.
///
/// The tree is shown while it is still being measured, so a row's numbers change under it. Only
/// the roughly thirty visible rows are ever painted, which is what makes a repaint on a timer
/// affordable: the alternative, an event per node, would marshal a million callbacks onto the UI
/// thread to update thirty values.
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

    /// <summary>
    /// Rows materialized for one directory before the rest are folded into a single line.
    /// A package cache with 40,000 entries would otherwise build 40,000 native rows, which
    /// freezes the window for seconds and makes the settle re-sort unusable.
    /// </summary>
    private const int MaxMaterializedChildren = 2000;

    private static readonly object LazyPlaceholder = new();
    private static readonly object OverflowPlaceholder = new();

    /// <summary>
    /// What a row knows about the directory it draws. The child-list version travels with the
    /// row so a directory that gains or loses children while the tree is open can be spotted
    /// without keeping a side table of every <see cref="TreeNode"/> ever created.
    /// </summary>
    private sealed class RowState(DirectoryNode directory)
    {
        public DirectoryNode Directory { get; } = directory;

        public int Version { get; set; } = -1;
    }

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

    /// <summary>Raised when a row is opened, so a running scan can be pointed at that subtree.</summary>
    public event EventHandler<DirectoryNode>? NodeExpanded;

    public DirectoryNode? SelectedDirectory => DirectoryOf(SelectedNode);

    /// <summary>
    /// True when a visible row order no longer matches size order, which happens because rows
    /// are sorted once at expand time and their numbers keep climbing afterwards.
    /// </summary>
    public bool IsOrderStale { get; private set; }

    public void Load(DirectoryNode root)
    {
        _root = root;
        IsOrderStale = false;

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

    /// <summary>
    /// Repaints the visible rows against the values the scan has reached, and materializes any
    /// row whose directory has been listed since it was drawn. Called on the page's timer.
    /// </summary>
    public void RefreshValues()
    {
        if (IsDisposed || _root is null)
            return;

        BeginUpdate();
        try
        {
            SyncRows(Nodes);
        }
        finally
        {
            EndUpdate();
        }

        Invalidate();
    }

    /// <summary>
    /// Re-sorts every expanded row by size. Rows are deliberately not re-sorted as numbers
    /// change: moving a row out from under the pointer is hostile, and it loses the selection
    /// and the scroll position too. This is the one settling pass, run once a scan has finished.
    /// </summary>
    public void ResortVisible()
    {
        if (IsDisposed || _root is null)
            return;

        var selected = SelectedDirectory;

        BeginUpdate();
        try
        {
            Resort(Nodes);
        }
        finally
        {
            EndUpdate();
        }

        IsOrderStale = false;

        if (selected is not null)
            SelectDirectory(selected);
    }

    /// <summary>
    /// Walks down from the root, expanding as it goes, so the lazy tree materializes the path.
    /// </summary>
    public void SelectDirectory(DirectoryNode node)
    {
        if (Nodes.Count == 0)
            return;

        var chain = new List<DirectoryNode>();
        for (DirectoryNode? current = node; current is not null; current = current.Parent)
            chain.Add(current);

        chain.Reverse();

        var row = Nodes[0];
        foreach (var step in chain.Skip(1))
        {
            row.Expand();

            TreeNode? match = null;
            foreach (TreeNode child in row.Nodes)
            {
                if (ReferenceEquals(DirectoryOf(child), step))
                {
                    match = child;
                    break;
                }
            }

            if (match is null)
                break;

            row = match;
        }

        SelectedNode = row;
        Focus();
    }

    private static DirectoryNode? DirectoryOf(TreeNode? row) =>
        (row?.Tag as RowState)?.Directory;

    private static TreeNode CreateNode(DirectoryNode directory)
    {
        var node = new TreeNode(directory.Name) { Tag = new RowState(directory) };
        AddStubIfExpandable(node, directory);
        return node;
    }

    /// <summary>
    /// A stub child makes the row expandable without walking the subtree now. A directory that
    /// has not been listed yet gets one too: it may well have children, and without the stub
    /// there is no expander and the user simply cannot open it.
    /// </summary>
    private static void AddStubIfExpandable(TreeNode row, DirectoryNode directory)
    {
        if (directory.Children.Count > 0 || !directory.IsEnumerated)
            row.Nodes.Add(new TreeNode { Tag = LazyPlaceholder });
    }

    private static bool HasOnlyStub(TreeNode row) =>
        row.Nodes.Count == 1 && ReferenceEquals(row.Nodes[0].Tag, LazyPlaceholder);

    protected override void OnBeforeExpand(TreeViewCancelEventArgs e)
    {
        base.OnBeforeExpand(e);

        if (e.Node is null || DirectoryOf(e.Node) is not { } directory)
            return;

        if (!HasOnlyStub(e.Node))
            return;

        // Not listed yet. The stub stays and reads "Measuring", and the timer materializes the
        // row as soon as the scan reaches it.
        if (directory.IsEnumerated)
            Materialize(e.Node, directory);
    }

    protected override void OnAfterExpand(TreeViewEventArgs e)
    {
        base.OnAfterExpand(e);

        if (e.Node is not null && DirectoryOf(e.Node) is { } directory)
            NodeExpanded?.Invoke(this, directory);
    }

    private void Materialize(TreeNode row, DirectoryNode directory)
    {
        BeginUpdate();
        try
        {
            row.Nodes.Clear();

            var shown = 0;
            foreach (var child in directory.ChildrenBySize)
            {
                if (shown == MaxMaterializedChildren)
                {
                    var hidden = directory.Children.Count - MaxMaterializedChildren;
                    row.Nodes.Add(new TreeNode($"and {hidden:N0} more")
                    {
                        Tag = OverflowPlaceholder,
                    });

                    break;
                }

                row.Nodes.Add(CreateNode(child));
                shown++;
            }

            ((RowState)row.Tag!).Version = directory.ChildrenVersion;
        }
        finally
        {
            EndUpdate();
        }
    }

    private void Resync(TreeNode row, DirectoryNode directory)
    {
        if (row.IsExpanded)
        {
            Materialize(row, directory);
            return;
        }

        row.Nodes.Clear();
        AddStubIfExpandable(row, directory);
        ((RowState)row.Tag!).Version = directory.ChildrenVersion;
    }

    private void SyncRows(TreeNodeCollection rows)
    {
        foreach (TreeNode row in rows)
        {
            if (row.Tag is not RowState state)
                continue;

            var directory = state.Directory;

            if (state.Version != directory.ChildrenVersion)
            {
                Resync(row, directory);
                continue;
            }

            if (row.IsExpanded && HasOnlyStub(row) && directory.IsEnumerated)
            {
                Materialize(row, directory);
                continue;
            }

            if (!IsOrderStale && row.IsExpanded && directory.IsComplete && IsOutOfOrder(row))
                IsOrderStale = true;

            SyncRows(row.Nodes);
        }
    }

    private static bool IsOutOfOrder(TreeNode row)
    {
        var previous = long.MaxValue;

        foreach (TreeNode child in row.Nodes)
        {
            if (DirectoryOf(child) is not { } directory)
                continue;

            if (directory.TotalSize > previous)
                return true;

            previous = directory.TotalSize;
        }

        return false;
    }

    private void Resort(TreeNodeCollection rows)
    {
        foreach (TreeNode row in rows)
        {
            if (row.Tag is not RowState state)
                continue;

            if (row.IsExpanded && !HasOnlyStub(row))
            {
                var expanded = CollectExpandedPaths(row);
                Materialize(row, state.Directory);
                ReExpand(row, expanded);
            }
        }
    }

    private static HashSet<string> CollectExpandedPaths(TreeNode row)
    {
        var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<TreeNode>();
        stack.Push(row);

        while (stack.Count > 0)
        {
            var current = stack.Pop();

            if (current.IsExpanded && DirectoryOf(current) is { } directory)
                expanded.Add(directory.Path);

            foreach (TreeNode child in current.Nodes)
                stack.Push(child);
        }

        return expanded;
    }

    private void ReExpand(TreeNode row, HashSet<string> expanded)
    {
        foreach (TreeNode child in row.Nodes)
        {
            if (DirectoryOf(child) is not { } directory || !expanded.Contains(directory.Path))
                continue;

            child.Expand();

            if (HasOnlyStub(child) && directory.IsEnumerated)
                Materialize(child, directory);

            ReExpand(child, expanded);
        }
    }

    protected override void OnAfterSelect(TreeViewEventArgs e)
    {
        base.OnAfterSelect(e);

        if (e.Node is not null && DirectoryOf(e.Node) is { } directory)
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
        if (node is null || DirectoryOf(node) is null || node.Nodes.Count == 0)
            return;

        var start = 8 + (node.Level * IndentWidth);
        if (e.X >= start && e.X < start + ChevronWidth + IconWidth)
            node.Toggle();
    }

    protected override void OnNodeMouseDoubleClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseDoubleClick(e);

        if (e.Node is not null && DirectoryOf(e.Node) is { } directory)
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

        if (e.Node is null)
            return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = AppTheme.Current;
        var row = new Rectangle(0, e.Bounds.Y, ClientSize.Width, e.Bounds.Height);
        var selected = (e.State & TreeNodeStates.Selected) != 0;

        using (var background = new SolidBrush(selected ? palette.SurfaceHover : palette.Surface))
            g.FillRectangle(background, row);

        if (DirectoryOf(e.Node) is not { } directory)
        {
            PaintPending(g, palette, e.Node, row);
            return;
        }

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

    /// <summary>
    /// A stub row, or the line that stands in for children too numerous to draw. Without this
    /// the row is simply blank, which under a progressive scan is a common and confusing sight.
    /// </summary>
    private static void PaintPending(Graphics g, Palette palette, TreeNode node, Rectangle row)
    {
        var text = ReferenceEquals(node.Tag, OverflowPlaceholder) ? node.Text : "Measuring…";
        var x = 8 + (node.Level * IndentWidth) + ChevronWidth;

        TextRenderer.DrawText(
            g,
            text,
            AppTheme.UiFont,
            new Rectangle(x, row.Y, Math.Max(0, row.Width - x - RightPadding), row.Height),
            palette.TextFaint,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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

        // Color by share of the parent: a child taking most of its parent is the thing worth
        // looking at, so it reads hot rather than neutral.
        var color = fraction switch
        {
            >= 0.5 => palette.Accent,
            >= 0.2 => Blend(palette.Accent, palette.TextFaint, 0.35f),
            _ => palette.TextFaint,
        };

        // A bar whose number is still climbing is drawn washed out, so a provisional share
        // never reads as a settled one.
        if (!directory.IsComplete)
            color = Color.FromArgb(150, color);

        using var brush = new SolidBrush(color);
        using var path = RoundedRect(new Rectangle(track.X, track.Y, filledWidth, track.Height), 4);
        g.FillPath(brush, path);
    }

    private static void PaintSize(
        Graphics g, Palette palette, DirectoryNode directory, int x, Rectangle row)
    {
        // Three states, one marker, on the size alone: a marker on every column would read as
        // noise rather than as information.
        //   412 MB    measured
        //  ~412 MB    still being measured, so the number is a floor
        //  ≈412 MB    from the cache, not yet confirmed against disk
        var text = ByteSize.Format(directory.TotalSize);
        var color = palette.Text;

        if (directory.IsFromCache)
        {
            text = "≈" + text;
            color = palette.TextMuted;
        }
        else if (!directory.IsComplete)
        {
            text = "~" + text;
        }

        TextRenderer.DrawText(
            g,
            text,
            AppTheme.UiFont,
            new Rectangle(x, row.Y, SizeWidth, row.Height),
            color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    private void PaintPercent(
        Graphics g, Palette palette, DirectoryNode directory, int x, Rectangle row)
    {
        var total = _root?.TotalSize ?? 0;
        if (total <= 0)
            return;

        // A directory credits itself before its ancestors, so mid-scan a child can briefly
        // out-total its own root. Clamping is cheaper than ordering the two writes.
        var percent = Math.Clamp(directory.TotalSize * 100.0 / total, 0, 100);
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
