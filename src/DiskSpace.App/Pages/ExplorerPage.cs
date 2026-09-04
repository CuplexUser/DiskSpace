using DiskSpace.App.Controls;
using DiskSpace.App.Theme;
using DiskSpace.Core.Caching;
using DiskSpace.Core.Model;
using DiskSpace.Core.Scanning;
using DiskSpace.Core.Settings;

namespace DiskSpace.App.Pages;

/// <summary>
/// The size explorer: pick a root, scan it, and drill in. Answers "what is eating my disk",
/// which the rule catalog on the Scan page deliberately cannot — it only knows what it has a
/// rule for.
///
/// The scan is progressive, so the tree appears within about a second of pressing Scan and its
/// numbers climb from there. Nothing on this page waits for a whole drive to be measured. A root
/// that has been measured before comes back from the cache instantly, marked as an estimate, and
/// settles into real numbers as the walk confirms them.
/// </summary>
public sealed class ExplorerPage : PageBase
{
    /// <summary>
    /// How often the tree and the status line are repainted from the running scan. Only the
    /// visible rows are drawn, so this is cheap; four times a second is fast enough to look
    /// live and slow enough that the numbers stay readable.
    /// </summary>
    private const int RefreshIntervalMs = 250;

    /// <summary>Refresh ticks between treemap rebuilds. Cells rearranging at 4 Hz is noise.</summary>
    private const int TreeMapTicksPerRebuild = 4;

    private readonly ComboBox _rootBox = new();
    private readonly AccentButton _scanButton = new();
    private readonly AccentButton _browseButton = new();
    private readonly AccentButton _fullButton = new();
    private readonly AccentButton _sortButton = new();
    private readonly SizeTreeView _tree = new();
    private readonly TreeMapPanel _treeMap = new();
    private readonly Breadcrumb _breadcrumb = new();
    private readonly Label _status = new();
    private readonly SplitContainer _split = new();
    private readonly System.Windows.Forms.Timer _refresh = new() { Interval = RefreshIntervalMs };
    private readonly TreeCache _cache;
    private readonly AppSettings _settings;

    private CancellationTokenSource? _cancellation;
    private ProgressiveScanner? _scanner;
    private ScanResult? _result;
    private int _treeMapTicks;

    public ExplorerPage(TreeCache cache, AppSettings settings)
        : base("Explorer", "Measure any folder and drill into what is actually large.")
    {
        _cache = cache;
        _settings = settings;

        BuildToolbar();
        BuildBody();
        PopulateRoots();
        ApplyTheme();

        _refresh.Tick += (_, _) => OnRefreshTick();
    }

    private bool IsScanning => _cancellation is not null;

    private void BuildToolbar()
    {
        _rootBox.DropDownStyle = ComboBoxStyle.DropDown;
        _rootBox.FlatStyle = FlatStyle.Flat;
        _rootBox.Font = AppTheme.UiFont;
        _rootBox.Width = 380;
        _rootBox.Location = new Point(Gutter, 16);

        _browseButton.Text = "Browse…";
        _browseButton.Width = 84;
        _browseButton.Location = new Point(Gutter + 390, 15);
        _browseButton.Click += (_, _) => Browse();

        _scanButton.Text = "Scan";
        _scanButton.Kind = ButtonKind.Primary;
        _scanButton.Width = 92;
        _scanButton.Location = new Point(Gutter + 484, 15);
        _scanButton.Click += async (_, _) => await ToggleScanAsync(ignoreCache: false);

        // Always offered, because a cached tree is a starting point and not an answer, and there
        // has to be a way to ask for a number with no remembered input at all.
        _fullButton.Text = "Rescan (full)";
        _fullButton.Kind = ButtonKind.Secondary;
        _fullButton.Width = 108;
        _fullButton.Location = new Point(Gutter + 586, 15);
        _fullButton.Click += async (_, _) => await ToggleScanAsync(ignoreCache: true);

        // Only shown when the settling re-sort was held back because the tree was in use.
        // Sorting rows out from under the pointer is hostile, so the move is left to the user.
        _sortButton.Text = "Sort by size";
        _sortButton.Kind = ButtonKind.Secondary;
        _sortButton.Width = 104;
        _sortButton.Location = new Point(Gutter + 704, 15);
        _sortButton.Visible = false;
        _sortButton.Click += (_, _) =>
        {
            _tree.ResortVisible();
            _sortButton.Visible = false;
        };

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 62 };
        toolbar.Controls.AddRange([_rootBox, _browseButton, _scanButton, _fullButton, _sortButton]);

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
        _tree.NodeHighlighted += (_, node) => ShowInTreeMap(node);
        _tree.NodeActivated += (_, node) => ShowInTreeMap(node);

        // Opening a folder is the clearest statement of what the user wants measured next, so it
        // is what the running scan reorders itself around.
        _tree.NodeExpanded += (_, node) => _scanner?.Prioritize(node);

        _treeMap.CellActivated += (_, node) => _tree.SelectDirectory(node);
        _treeMap.CellSelected += (_, node) => _breadcrumb.SetPath(node);

        _breadcrumb.SegmentClicked += (_, node) => _tree.SelectDirectory(node);

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

    private async Task ToggleScanAsync(bool ignoreCache)
    {
        if (IsScanning)
        {
            _scanner?.Cancel();
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
        _fullButton.Enabled = false;
        _sortButton.Visible = false;
        _status.ForeColor = AppTheme.Current.TextMuted;

        var scanner = new ProgressiveScanner(new ScanOptions
        {
            TrustUnchangedFolders = _settings.TrustUnchangedFolders,
        });

        _scanner = scanner;

        try
        {
            var cached = ignoreCache || !_settings.UseScanCache
                ? null
                : await Task.Run(() => _cache.TryLoad(root), _cancellation.Token);

            if (cached is not null)
            {
                // On screen before a single directory is read. Revalidation then updates the
                // same node objects in place, so rows keep their identity and the scroll
                // position survives.
                _tree.Load(cached.Root);
                ShowInTreeMap(cached.Root);
                _status.Text = $"From cache, measured {Describe(cached.Age)}. Re-measuring…";
                _status.ForeColor = AppTheme.Current.RiskReview;

                await scanner.StartFromAsync(cached.Root, _cancellation.Token);
            }
            else
            {
                // Returns as soon as the first levels are listed, which is the point: the tree
                // is on screen while the rest of the disk is still being walked.
                var tree = await scanner.StartAsync(root, _cancellation.Token);
                _tree.Load(tree);
                ShowInTreeMap(tree);
            }

            _treeMapTicks = 0;
            _refresh.Start();

            _result = await scanner.RunToCompletionAsync();
            Settle(_result);

            // Only a finished scan is remembered. A cancelled one would come back later dressed
            // as a measurement of the whole drive while missing everything it never reached.
            if (_result.IsComplete && _settings.UseScanCache)
                await Task.Run(() => _cache.Save(_result));
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
            _refresh.Stop();

            // Disposed only once the workers are joined. The scan now outlives the first await,
            // so tearing the token source down there would pull it out from under them.
            await scanner.DisposeAsync();

            if (ReferenceEquals(_scanner, scanner))
                _scanner = null;

            _cancellation?.Dispose();
            _cancellation = null;
            _scanButton.Text = "Scan";
            _scanButton.Kind = ButtonKind.Primary;
            _rootBox.Enabled = true;
            _browseButton.Enabled = true;
            _fullButton.Enabled = true;
        }
    }

    /// <summary>How old a cached measurement is, in the words someone would actually use.</summary>
    private static string Describe(TimeSpan age) => age.TotalMinutes switch
    {
        < 2 => "moments ago",
        < 60 => $"{(int)age.TotalMinutes} minutes ago",
        < 120 => "an hour ago",
        < 1440 => $"{(int)age.TotalHours} hours ago",
        < 2880 => "yesterday",
        _ => $"{(int)age.TotalDays} days ago",
    };

    private void OnRefreshTick()
    {
        if (IsDisposed || _scanner is null)
            return;

        _tree.RefreshValues();

        if (++_treeMapTicks % TreeMapTicksPerRebuild == 0)
            _treeMap.RefreshValues();

        var progress = _scanner.Snapshot();
        _status.Text =
            $"Scanning…  {ByteSize.Count(progress.DirectoriesScanned)} folders  ·  " +
            $"{ByteSize.Format(progress.BytesSeen)}  ·  {Shorten(progress.CurrentPath)}";
    }

    /// <summary>
    /// The one settling pass at the end of a scan. Rows were sorted when they were opened, and
    /// their totals kept climbing afterwards, so the order is usually stale by now. Re-sorting is
    /// skipped while the tree is in use, because moving a row out from under the pointer is worse
    /// than leaving it in the wrong place; the button then offers the move instead.
    /// </summary>
    private void Settle(ScanResult result)
    {
        _tree.RefreshValues();
        _treeMap.RefreshValues();

        if (_tree.IsOrderStale)
        {
            var inUse = _tree.Focused
                        || _tree.ClientRectangle.Contains(_tree.PointToClient(MousePosition));

            if (inUse)
                _sortButton.Visible = true;
            else
                _tree.ResortVisible();
        }

        var summary =
            $"{ByteSize.Format(result.TotalSize)} in {ByteSize.Count(result.TotalFileCount)} files, " +
            $"{ByteSize.Count(result.TotalDirectoryCount)} folders  ·  " +
            $"scanned in {result.Duration.TotalSeconds:N1}s";

        if (!result.IsComplete)
            summary = "Cancelled.  " + summary + "  ·  incomplete";

        if (result.Issues.Count > 0)
            summary += $"  ·  {ByteSize.Count(result.Issues.Count)} locations unreadable";

        _status.Text = summary;
        _status.ForeColor = result.IsComplete
            ? AppTheme.Current.TextMuted
            : AppTheme.Current.RiskReview;
    }

    private void ShowInTreeMap(DirectoryNode node)
    {
        // A leaf has nothing to subdivide, so show its parent and let the leaf stay highlighted.
        var target = node.Children.Count > 0 ? node : node.Parent ?? node;
        _treeMap.Show(target);
        _breadcrumb.SetPath(node);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Stop();
            _refresh.Dispose();

            // Not awaited: the scanner holds no reference to any control, so a worker that
            // outlives this page by a moment has nothing left to touch.
            _scanner?.Cancel();
            _cancellation?.Cancel();
        }

        base.Dispose(disposing);
    }
}
