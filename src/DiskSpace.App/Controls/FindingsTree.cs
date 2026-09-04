using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DiskSpace.App.Platform;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Rules;

namespace DiskSpace.App.Controls;

/// <summary>
/// The findings list: categories, then the rules inside them, each with a checkbox, a risk pill
/// and its size.
///
/// A plain <see cref="TreeView"/> rather than a virtualised list, because findings are
/// aggregates — one row per rule target, a few dozen in total — not one row per file. Checkboxes
/// are drawn rather than native, so a category can show a genuine indeterminate state and so
/// report-only rows can refuse selection outright.
/// </summary>
public sealed class FindingsTree : TreeView
{
    private const int IndentWidth = 18;
    private const int CheckSize = 14;
    private const int SizeColumnWidth = 84;
    private const int PillWidth = 74;
    private const int RightPadding = 12;

    private readonly HashSet<CleanupFinding> _selected = [];
    private readonly Font _iconFont = FontResolver.Icons(10f);
    private long _largestCategory = 1;

    public FindingsTree()
    {
        DrawMode = TreeViewDrawMode.OwnerDrawAll;
        FullRowSelect = true;
        ShowLines = false;
        ShowPlusMinus = false;
        ShowRootLines = false;
        HideSelection = false;
        ItemHeight = 26;
        Indent = IndentWidth;
        BorderStyle = BorderStyle.None;

        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        AppTheme.Changed += OnThemeChanged;
    }

    /// <summary>Raised whenever the checked set changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when the highlighted row changes, to drive the detail pane.</summary>
    public event EventHandler<CleanupFinding?>? FindingHighlighted;

    public IReadOnlyCollection<CleanupFinding> Selected => _selected;

    public long SelectedSize => _selected.Sum(f => f.Size);

    public void Load(IReadOnlyList<CleanupFinding> findings)
    {
        _selected.Clear();

        var categories = findings
            .GroupBy(f => f.Rule.Category)
            .OrderByDescending(g => g.Sum(f => f.Size))
            .ToList();

        _largestCategory = Math.Max(1, categories.Count == 0 ? 1 : categories.Max(g => g.Sum(f => f.Size)));

        BeginUpdate();
        try
        {
            Nodes.Clear();

            foreach (var category in categories)
            {
                var categoryNode = new TreeNode(category.Key) { Tag = category.Key };

                // One rule can own several targets — a browser has a cache per profile — and
                // four rows all reading "Microsoft Edge cache" tell the user nothing about
                // which is which. Only the ambiguous ones get a location suffix.
                var ambiguous = category
                    .GroupBy(f => f.Rule.Name)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToHashSet(StringComparer.Ordinal);

                foreach (var finding in category.OrderByDescending(f => f.Size))
                {
                    var label = ambiguous.Contains(finding.Rule.Name)
                        ? $"{finding.Rule.Name}  ·  {LocationHint(finding.Path)}"
                        : finding.Rule.Name;

                    categoryNode.Nodes.Add(new TreeNode(label) { Tag = finding });

                    // Safe findings start selected; anything needing judgement never does.
                    if (finding.Rule.Risk == RiskLevel.Safe)
                        _selected.Add(finding);
                }

                Nodes.Add(categoryNode);
                categoryNode.Expand();
            }
        }
        finally
        {
            EndUpdate();
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAllOfRisk(RiskLevel risk, bool selected)
    {
        foreach (var finding in AllFindings().Where(f => f.Rule.Risk == risk))
        {
            if (selected && finding.IsActionable)
                _selected.Add(finding);
            else
                _selected.Remove(finding);
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The last two path segments — enough to tell "Default\Cache" from "Profile 1\GPUCache"
    /// without pasting the whole path into a row that has no width for it.
    /// </summary>
    private static string LocationHint(string path)
    {
        var segments = path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        return segments.Length <= 2
            ? path
            : string.Join(Path.DirectorySeparatorChar, segments[^2..]);
    }

    private IEnumerable<CleanupFinding> AllFindings()
    {
        foreach (TreeNode category in Nodes)
        {
            foreach (TreeNode child in category.Nodes)
            {
                if (child.Tag is CleanupFinding finding)
                    yield return finding;
            }
        }
    }

    private static IEnumerable<CleanupFinding> FindingsUnder(TreeNode node)
    {
        foreach (TreeNode child in node.Nodes)
        {
            if (child.Tag is CleanupFinding finding)
                yield return finding;
        }
    }

    private void Toggle(TreeNode node)
    {
        if (node.Tag is CleanupFinding finding)
        {
            if (!finding.IsActionable)
                return; // Report-only rows are informational; they cannot be actioned.

            if (!_selected.Remove(finding))
                _selected.Add(finding);
        }
        else
        {
            var children = FindingsUnder(node).Where(f => f.IsActionable).ToList();
            var allSelected = children.Count > 0 && children.All(_selected.Contains);

            foreach (var child in children)
            {
                if (allSelected)
                    _selected.Remove(child);
                else
                    _selected.Add(child);
            }
        }

        Invalidate();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnNodeMouseClick(TreeNodeMouseClickEventArgs e)
    {
        base.OnNodeMouseClick(e);
        if (e.Node is null)
            return;

        SelectedNode = e.Node;

        // The checkbox has its own hit area; clicking elsewhere on the row only selects it.
        var checkLeft = 10 + (e.Node.Level * IndentWidth) + 14;
        if (e.X >= checkLeft && e.X <= checkLeft + CheckSize + 4)
            Toggle(e.Node);
    }

    protected override void OnAfterSelect(TreeViewEventArgs e)
    {
        base.OnAfterSelect(e);
        FindingHighlighted?.Invoke(this, e.Node?.Tag as CleanupFinding);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Space && SelectedNode is { } node)
        {
            Toggle(node);
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

        if (selected)
        {
            using var accent = new SolidBrush(palette.Accent);
            g.FillRectangle(accent, new Rectangle(0, row.Y, 2, row.Height));
        }

        if (e.Node.Tag is CleanupFinding finding)
            DrawFinding(g, palette, e.Node, finding, row);
        else
            DrawCategory(g, palette, e.Node, row);
    }

    private void DrawCategory(Graphics g, Palette palette, TreeNode node, Rectangle row)
    {
        var findings = FindingsUnder(node).ToList();
        var total = findings.Sum(f => f.Size);
        var actionable = findings.Where(f => f.IsActionable).ToList();
        var checkedCount = actionable.Count(_selected.Contains);

        var state = actionable.Count == 0 ? CheckState.Unchecked
            : checkedCount == 0 ? CheckState.Unchecked
            : checkedCount == actionable.Count ? CheckState.Checked
            : CheckState.Indeterminate;

        var x = 10;
        DrawChevron(g, palette, node, ref x, row);
        DrawCheckBox(g, palette, x + 14, row, state, enabled: actionable.Count > 0);

        var textX = x + 14 + CheckSize + 10;
        var rightEdge = row.Right - RightPadding;
        var sizeX = rightEdge - SizeColumnWidth;

        TextRenderer.DrawText(
            g, node.Text, AppTheme.UiFontBold,
            new Rectangle(textX, row.Y, sizeX - textX - 90, row.Height),
            palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        // A bar comparing this category against the largest one, so relative weight is visible
        // without reading every number.
        var barWidth = 70;
        var barX = sizeX - barWidth - 12;
        var track = new Rectangle(barX, row.Y + (row.Height / 2) - 3, barWidth, 6);

        using (var trackBrush = new SolidBrush(palette.BarTrack))
        using (var trackPath = Rounded(track, 3))
            g.FillPath(trackBrush, trackPath);

        var filled = (int)Math.Round(barWidth * Math.Clamp((double)total / _largestCategory, 0, 1));
        if (filled > 1)
        {
            using var brush = new SolidBrush(palette.Accent);
            using var path = Rounded(new Rectangle(track.X, track.Y, filled, track.Height), 3);
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(
            g, ByteSize.Format(total), AppTheme.UiFontBold,
            new Rectangle(sizeX, row.Y, SizeColumnWidth, row.Height),
            palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    private void DrawFinding(
        Graphics g, Palette palette, TreeNode node, CleanupFinding finding, Rectangle row)
    {
        var x = 10 + (node.Level * IndentWidth);
        var state = _selected.Contains(finding) ? CheckState.Checked : CheckState.Unchecked;

        DrawCheckBox(g, palette, x + 14, row, state, finding.IsActionable);

        var textX = x + 14 + CheckSize + 10;
        var rightEdge = row.Right - RightPadding;
        var sizeX = rightEdge - SizeColumnWidth;
        var pillX = sizeX - PillWidth - 12;

        var ink = finding.IsActionable ? palette.Text : palette.TextMuted;

        // node.Text, not Rule.Name: Load may have appended a location hint to tell apart
        // several targets belonging to the same rule.
        TextRenderer.DrawText(
            g, node.Text, AppTheme.UiFont,
            new Rectangle(textX, row.Y, pillX - textX - 8, row.Height),
            ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        DrawRiskPill(g, palette, pillX, row, finding.Rule.Risk);

        TextRenderer.DrawText(
            g, ByteSize.Format(finding.Size), AppTheme.UiFont,
            new Rectangle(sizeX, row.Y, SizeColumnWidth, row.Height),
            ink,
            TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.NoPrefix);
    }

    private static void DrawChevron(
        Graphics g, Palette palette, TreeNode node, ref int x, Rectangle row)
    {
        var centerY = row.Y + (row.Height / 2f);
        var points = node.IsExpanded
            ? new[]
            {
                new PointF(x + 1, centerY - 2),
                new PointF(x + 9, centerY - 2),
                new PointF(x + 5, centerY + 3),
            }
            : new[]
            {
                new PointF(x + 3, centerY - 4),
                new PointF(x + 8, centerY),
                new PointF(x + 3, centerY + 4),
            };

        using var brush = new SolidBrush(palette.TextFaint);
        g.FillPolygon(brush, points);
    }

    private static void DrawCheckBox(
        Graphics g, Palette palette, int x, Rectangle row, CheckState state, bool enabled)
    {
        var box = new Rectangle(x, row.Y + ((row.Height - CheckSize) / 2), CheckSize, CheckSize);
        using var path = Rounded(box, 3);

        if (!enabled)
        {
            using var disabled = new Pen(palette.Border);
            g.DrawPath(disabled, path);
            return;
        }

        if (state == CheckState.Unchecked)
        {
            using var pen = new Pen(palette.BorderStrong);
            g.DrawPath(pen, path);
            return;
        }

        using (var fill = new SolidBrush(palette.Accent))
            g.FillPath(fill, path);

        using var mark = new Pen(palette.AccentText, 1.8f);

        if (state == CheckState.Indeterminate)
        {
            g.DrawLine(mark, box.X + 3, box.Y + (box.Height / 2f), box.Right - 3, box.Y + (box.Height / 2f));
            return;
        }

        g.DrawLines(mark,
        [
            new PointF(box.X + 3, box.Y + 7),
            new PointF(box.X + 6, box.Y + 10),
            new PointF(box.Right - 3, box.Y + 4),
        ]);
    }

    private static void DrawRiskPill(
        Graphics g, Palette palette, int x, Rectangle row, RiskLevel risk)
    {
        var (label, color) = risk switch
        {
            RiskLevel.Safe => ("SAFE", palette.RiskSafe),
            RiskLevel.Review => ("REVIEW", palette.RiskReview),
            RiskLevel.Advanced => ("ADVANCED", palette.RiskAdvanced),
            _ => ("INFO", palette.RiskReport),
        };

        var pill = new Rectangle(x, row.Y + ((row.Height - 17) / 2), PillWidth, 17);
        using var path = Rounded(pill, 8);

        // Tinted fill with a full-strength border: readable in both themes without shouting.
        using (var fill = new SolidBrush(Color.FromArgb(38, color)))
            g.FillPath(fill, path);
        using (var pen = new Pen(Color.FromArgb(150, color)))
            g.DrawPath(pen, path);

        TextRenderer.DrawText(
            g, label, AppTheme.UiFontSmall, pill, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private static GraphicsPath Rounded(Rectangle bounds, int radius)
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
