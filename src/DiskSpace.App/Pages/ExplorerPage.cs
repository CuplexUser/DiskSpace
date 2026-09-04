using DiskSpace.App.Controls;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Scanning;

namespace DiskSpace.App.Pages;

/// <summary>
/// The size explorer: pick a root, scan it, and drill in. Answers "what is eating my disk",
/// which the rule catalog on the Scan page deliberately cannot — it only knows what it has a
/// rule for.
/// </summary>
public sealed class ExplorerPage : PageBase
{
    private readonly ComboBox _rootBox = new();
    private readonly AccentButton _scanButton = new();
    private readonly AccentButton _browseButton = new();
    private readonly SizeTreeView _tree = new();
    private readonly TreeMapPanel _treeMap = new();
    private readonly Breadcrumb _breadcrumb = new();
    private readonly Label _status = new();
    private readonly SplitContainer _split = new();

    private CancellationTokenSource? _cancellation;
    private ScanResult? _result;

    public ExplorerPage()
        : base("Explorer", "Measure any folder and drill into what is actually large.")
    {
        BuildToolbar();
        BuildBody();
        PopulateRoots();
        ApplyTheme();
    }

    private bool IsScanning => _cancellation is not null;

    private void BuildToolbar()
    {
        _rootBox.DropDownStyle = ComboBoxStyle.DropDown;
        _rootBox.FlatStyle = FlatStyle.Flat;
        _rootBox.Font = AppTheme.UiFont;
        _rootBox.Width = 420;
        _rootBox.Location = new Point(Gutter, 16);

        _browseButton.Text = "Browse…";
        _browseButton.Width = 84;
        _browseButton.Location = new Point(Gutter + 430, 15);
        _browseButton.Click += (_, _) => Browse();

        _scanButton.Text = "Scan";
        _scanButton.Kind = ButtonKind.Primary;
        _scanButton.Width = 92;
        _scanButton.Location = new Point(Gutter + 524, 15);
        _scanButton.Click += async (_, _) => await ToggleScanAsync();

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 62 };
        toolbar.Controls.AddRange([_rootBox, _browseButton, _scanButton]);

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = AppTheme.UiFont;
        _status.Padding = new Padding(Gutter, 0, Gutter, 0);
        _status.Text = "Ready.";

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        statusBar.Controls.Add(_status);

        Body.Controls.Add(_split);
        Body.Controls.Add(statusBar);
        Body.Controls.Add(toolbar);
    }

    private void BuildBody()
    {
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.SplitterWidth = 6;

        _tree.Dock = DockStyle.Fill;
        _tree.NodeHighlighted += (_, node) => ShowInTreeMap(node, fromTree: true);
        _tree.NodeActivated += (_, node) => ShowInTreeMap(node, fromTree: true);

        _treeMap.CellActivated += (_, node) => SelectInTree(node);
        _treeMap.CellSelected += (_, node) => _breadcrumb.SetPath(node);

        _breadcrumb.SegmentClicked += (_, node) => SelectInTree(node);

        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_treeMap);
        _split.Panel2.Controls.Add(_breadcrumb);

        PositionSplitOnFirstShow(_split, width => Math.Max(280, (int)(width * 0.52)));
    }

    private void PopulateRoots()
    {
        var candidates = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive is { IsReady: true, DriveType: DriveType.Fixed })
                candidates.Add(drive.RootDirectory.FullName);
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            _rootBox.Items.Add(candidate);

        _rootBox.SelectedIndex = 0;
    }

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a folder to measure",
            UseDescriptionForTitle = true,
            SelectedPath = _rootBox.Text,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _rootBox.Text = dialog.SelectedPath;
    }

    private async Task ToggleScanAsync()
    {
        if (IsScanning)
        {
            await _cancellation!.CancelAsync();
            return;
        }

        var root = _rootBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            _status.Text = $"Not a folder: {root}";
            _status.ForeColor = AppTheme.Current.RiskAdvanced;
            return;
        }

        _cancellation = new CancellationTokenSource();
        _scanButton.Text = "Cancel";
        _scanButton.Kind = ButtonKind.Danger;
        _rootBox.Enabled = false;
        _browseButton.Enabled = false;
        _status.ForeColor = AppTheme.Current.TextMuted;

        // Progress arrives from worker threads; Progress<T> marshals it back to this one.
        var progress = new Progress<ScanProgress>(p =>
            _status.Text =
                $"Scanning…  {ByteSize.Count(p.DirectoriesScanned)} folders  ·  " +
                $"{ByteSize.Format(p.BytesSeen)}  ·  {Shorten(p.CurrentPath)}");

        try
        {
            var scanner = new FastDirectoryScanner();
            _result = await scanner.ScanAsync(root, progress, _cancellation.Token);
            ShowResult(_result);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Scan failed: {ex.Message}";
            _status.ForeColor = AppTheme.Current.RiskAdvanced;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _scanButton.Text = "Scan";
            _scanButton.Kind = ButtonKind.Primary;
            _rootBox.Enabled = true;
            _browseButton.Enabled = true;
        }
    }

    private void ShowResult(ScanResult result)
    {
        _tree.Load(result.Root);
        ShowInTreeMap(result.Root, fromTree: true);

        var summary =
            $"{ByteSize.Format(result.TotalSize)} in {ByteSize.Count(result.TotalFileCount)} files, " +
            $"{ByteSize.Count(result.TotalDirectoryCount)} folders  ·  " +
            $"scanned in {result.Duration.TotalSeconds:N1}s";

        if (result.Issues.Count > 0)
            summary += $"  ·  {ByteSize.Count(result.Issues.Count)} locations unreadable";

        _status.Text = summary;
        _status.ForeColor = AppTheme.Current.TextMuted;
    }

    private void ShowInTreeMap(DirectoryNode node, bool fromTree)
    {
        // A leaf has nothing to subdivide, so show its parent and let the leaf stay highlighted.
        var target = node.Children.Count > 0 ? node : node.Parent ?? node;
        _treeMap.Show(target);
        _breadcrumb.SetPath(node);
    }

    private void SelectInTree(DirectoryNode node)
    {
        // Walk down from the root, expanding as we go, so the lazy tree materialises the path.
        var chain = new List<DirectoryNode>();
        for (var current = node; current is not null; current = current.Parent)
            chain.Add(current);
        chain.Reverse();

        if (_tree.Nodes.Count == 0)
            return;

        var treeNode = _tree.Nodes[0];
        foreach (var step in chain.Skip(1))
        {
            treeNode.Expand();
            TreeNode? match = null;
            foreach (TreeNode child in treeNode.Nodes)
            {
                if (ReferenceEquals(child.Tag, step))
                {
                    match = child;
                    break;
                }
            }

            if (match is null)
                break;

            treeNode = match;
        }

        _tree.SelectedNode = treeNode;
        _tree.Focus();
    }

    private static string Shorten(string path) =>
        path.Length <= 62 ? path : "…" + path[^61..];

    protected override void ApplyTheme()
    {
        base.ApplyTheme();
        var palette = AppTheme.Current;

        _rootBox.BackColor = palette.SurfaceAlt;
        _rootBox.ForeColor = palette.Text;
        _tree.BackColor = palette.Surface;
        _status.ForeColor = palette.TextMuted;
        _split.BackColor = palette.Border;
        _split.Panel1.BackColor = palette.Surface;
        _split.Panel2.BackColor = palette.Bg;

        foreach (Control control in Body.Controls)
            control.BackColor = palette.Bg;
    }

    public override void OnActivated() => _tree.Focus();
}
