using System.Diagnostics;
using DiskSpace.App.Controls;
using DiskSpace.App.Theme;
using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;
using DiskSpace.Core.Safety;
using DiskSpace.Core.Scanning;

namespace DiskSpace.App.Pages;

/// <summary>
/// Single files: the largest under a folder, optionally only the ones nobody has touched in a
/// long time, and the files that exist more than once.
///
/// The Explorer page adds sizes up per folder and cannot name a file; the Scan page only knows
/// what a rule describes. This page answers what neither can, and because what it lists is the
/// person's own data, nothing here deletes: the most it does is send what the person picked to
/// the Recycle Bin, where it can be restored.
/// </summary>
public sealed class FilesPage : PageBase
{
    /// <summary>More rows than this tells nobody anything, and slows the list down.</summary>
    private const int MaxLargeRows = 2000;

    private const int MaxDuplicateGroups = 500;

    private static readonly (string Label, long Bytes)[] SizeChoices =
    [
        ("1 MB", 1L << 20),
        ("10 MB", 10L << 20),
        ("100 MB", 100L << 20),
        ("1 GB", 1L << 30),
    ];

    private static readonly (string Label, int Days)[] AgeChoices =
    [
        ("Any age", 0),
        ("6 months", 182),
        ("1 year", 365),
        ("2 years", 730),
    ];

    private readonly ComboBox _rootBox = new();
    private readonly AccentButton _browseButton = new();
    private readonly AccentButton _largeButton = new();
    private readonly AccentButton _duplicatesButton = new();
    private readonly ComboBox _sizeBox = new();
    private readonly ComboBox _ageBox = new();
    private readonly Label _sizeLabel = new();
    private readonly Label _ageLabel = new();
    private readonly ThemedListView _list = new();
    private readonly ThemedContextMenu _menu = new();
    private readonly AccentButton _revealButton = new();
    private readonly AccentButton _extraCopiesButton = new();
    private readonly AccentButton _recycleButton = new();
    private readonly Label _status = new();
    private readonly ProgressStrip _progress = new();

    private CancellationTokenSource? _cancellation;
    private Mode _mode = Mode.None;
    private List<FileEntry> _largeFiles = [];
    private List<DuplicateGroup> _groups = [];
    private int _sortColumn = 1;
    private bool _sortDescending = true;

    public FilesPage()
        : base("Files", "The largest and oldest files under a folder, and the ones that exist more than once.")
    {
        BuildToolbar();
        BuildList();
        BuildActionBar();
        PopulateRoots();
        ApplyTheme();
        UpdateActions();
    }

    private enum Mode
    {
        None,
        Large,
        Duplicates,
    }

    private bool IsBusy => _cancellation is not null;

    private void BuildToolbar()
    {
        _rootBox.DropDownStyle = ComboBoxStyle.DropDown;
        _rootBox.FlatStyle = FlatStyle.Flat;
        _rootBox.Font = AppTheme.UiFont;
        _rootBox.Width = 320;
        _rootBox.Location = new Point(Gutter, 16);

        _browseButton.Text = "Browse…";
        _browseButton.Width = 84;
        _browseButton.Location = new Point(Gutter + 330, 15);
        _browseButton.Click += (_, _) => Browse();

        _largeButton.Text = "Find large files";
        _largeButton.Kind = ButtonKind.Primary;
        _largeButton.Width = 124;
        _largeButton.Location = new Point(Gutter + 424, 15);
        _largeButton.Click += async (_, _) => await ToggleSurveyAsync(Mode.Large);

        _duplicatesButton.Text = "Find duplicates";
        _duplicatesButton.Kind = ButtonKind.Primary;
        _duplicatesButton.Width = 124;
        _duplicatesButton.Location = new Point(Gutter + 558, 15);
        _duplicatesButton.Click += async (_, _) => await ToggleSurveyAsync(Mode.Duplicates);

        BuildFilter(_sizeLabel, "At least", _sizeBox, SizeChoices.Select(c => c.Label), Gutter, 60, 90);
        _sizeBox.SelectedIndex = 1;

        BuildFilter(_ageLabel, "Untouched for", _ageBox, AgeChoices.Select(c => c.Label), Gutter + 170, 96, 100);
        _ageBox.SelectedIndex = 0;

        // Only the large-file list is filtered by age, and that filter is applied to what was
        // already found, so changing it needs no new search.
        _ageBox.SelectedIndexChanged += (_, _) =>
        {
            if (_mode == Mode.Large)
                ShowLargeFiles();
        };

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 92 };
        toolbar.Controls.AddRange(
        [
            _rootBox, _browseButton, _largeButton, _duplicatesButton,
            _sizeLabel, _sizeBox, _ageLabel, _ageBox,
        ]);

        Body.Controls.Add(toolbar);
    }

    private static void BuildFilter(
        Label label, string text, ComboBox box, IEnumerable<string> choices, int x, int labelWidth, int boxWidth)
    {
        label.Text = text;
        label.AutoSize = false;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.Font = AppTheme.UiFont;
        label.Bounds = new Rectangle(x, 56, labelWidth, 24);

        box.DropDownStyle = ComboBoxStyle.DropDownList;
        box.FlatStyle = FlatStyle.Flat;
        box.Font = AppTheme.UiFont;
        box.Width = boxWidth;
        box.Location = new Point(x + labelWidth, 56);
        box.Items.AddRange([.. choices]);
    }

    private void BuildList()
    {
        _list.Dock = DockStyle.Fill;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.Font = AppTheme.UiFont;

        _list.Columns.Add("Name", 280);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Last modified", 120);
        _list.Columns.Add("Folder", 460);

        _list.SelectedIndexChanged += (_, _) => UpdateActions();
        _list.ColumnClick += (_, e) => SortBy(e.Column);
        _list.DoubleClick += (_, _) => RevealSelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete)
                RecycleSelected();
        };

        // Opening a file from here would open it with this process's administrator rights,
        // so the list offers its folder instead.
        _menu.Add("Show in folder", RevealSelected);
        _menu.AddSeparator();
        _menu.Add("Move to Recycle Bin", RecycleSelected, Keys.Delete);
        _list.ContextMenuStrip = _menu;

        Body.Controls.Add(_list);
        _list.BringToFront();
    }

    private void BuildActionBar()
    {
        _revealButton.Text = "Show in folder";
        _revealButton.Kind = ButtonKind.Secondary;
        _revealButton.Width = 116;
        _revealButton.Click += (_, _) => RevealSelected();

        // The oldest copy is listed first in each set and is the one this keeps, because it is
        // most likely the original the others were copied from.
        _extraCopiesButton.Text = "Select all but the oldest";
        _extraCopiesButton.Kind = ButtonKind.Secondary;
        _extraCopiesButton.Width = 176;
        _extraCopiesButton.Click += (_, _) => SelectExtraCopies();

        _recycleButton.Text = "Move to Recycle Bin";
        _recycleButton.Kind = ButtonKind.Danger;
        _recycleButton.Width = 150;
        _recycleButton.Click += (_, _) => RecycleSelected();

        var actions = new Panel { Dock = DockStyle.Bottom, Height = 50 };
        actions.Controls.AddRange([_revealButton, _extraCopiesButton, _recycleButton]);

        void PositionButtons()
        {
            var right = actions.ClientSize.Width - Gutter;
            foreach (var button in new[] { _recycleButton, _extraCopiesButton, _revealButton })
            {
                right -= button.Width;
                button.Location = new Point(right, 10);
                right -= 10;
            }
        }

        actions.Resize += (_, _) => PositionButtons();
        PositionButtons();

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = AppTheme.UiFont;
        _status.Padding = new Padding(Gutter, 0, Gutter, 0);
        _status.Text = "Choose a folder, then find its large files or its duplicates. Searching only reads.";

        _progress.Dock = DockStyle.Top;
        _progress.Visible = false;

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        statusBar.Controls.Add(_status);
        statusBar.Controls.Add(_progress);

        Body.Controls.Add(actions);
        Body.Controls.Add(statusBar);
        _list.BringToFront();
    }

    private void PopulateRoots()
    {
        var candidates = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
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
            Description = "Choose a folder to search",
            UseDescriptionForTitle = true,
            SelectedPath = _rootBox.Text,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _rootBox.Text = dialog.SelectedPath;
    }

    private async Task ToggleSurveyAsync(Mode mode)
    {
        if (IsBusy)
        {
            await _cancellation!.CancelAsync();
            return;
        }

        var root = _rootBox.Text.Trim();
        if (!Directory.Exists(root))
        {
            SetStatus($"{root} is not a folder that exists.", AppTheme.Current.RiskAdvanced);
            return;
        }

        var minimum = SizeChoices[Math.Max(0, _sizeBox.SelectedIndex)].Bytes;

        _cancellation = new CancellationTokenSource();
        SetBusy(mode);
        _progress.Start();

        var progress = new Progress<SurveyProgress>(ReportProgress);

        try
        {
            if (mode == Mode.Large)
            {
                _largeFiles = [.. await FileSurvey.FindLargeFilesAsync(root, minimum, progress, _cancellation.Token)];
                _mode = Mode.Large;
                ShowLargeFiles();
            }
            else
            {
                _groups = [.. await FileSurvey.FindDuplicatesAsync(root, minimum, progress, _cancellation.Token)];
                _mode = Mode.Duplicates;
                ShowDuplicates();
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus("Search cancelled.", AppTheme.Current.TextMuted);
        }
        catch (Exception ex)
        {
            SetStatus($"Search failed: {ex.Message}", AppTheme.Current.RiskAdvanced);
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(Mode.None);
        }
    }

    private void ReportProgress(SurveyProgress p)
    {
        if (p.Total > 0)
            _progress.Report((int)(p.Done * 1000 / p.Total), 1000);

        var count = p.Total > 0 ? $"{p.Done * 100 / p.Total}%" : $"{ByteSize.Count(p.Done)} found";
        _status.Text = $"{p.Stage} · {count} · {Shorten(p.Current)}";
        _status.ForeColor = AppTheme.Current.TextMuted;
    }

    private void SetBusy(Mode running)
    {
        var busy = running != Mode.None;

        if (!busy)
            _progress.Stop();

        _largeButton.Text = running == Mode.Large ? "Cancel" : "Find large files";
        _largeButton.Kind = running == Mode.Large ? ButtonKind.Danger : ButtonKind.Primary;
        _largeButton.Enabled = running != Mode.Duplicates;

        _duplicatesButton.Text = running == Mode.Duplicates ? "Cancel" : "Find duplicates";
        _duplicatesButton.Kind = running == Mode.Duplicates ? ButtonKind.Danger : ButtonKind.Primary;
        _duplicatesButton.Enabled = running != Mode.Large;

        _rootBox.Enabled = !busy;
        _browseButton.Enabled = !busy;
        _sizeBox.Enabled = !busy;
        UpdateActions();
    }

    // ---- Large files ---------------------------------------------------------------------------

    private void ShowLargeFiles()
    {
        var days = AgeChoices[Math.Max(0, _ageBox.SelectedIndex)].Days;
        var cutoff = DateTime.UtcNow.AddDays(-days);

        var shown = (days == 0 ? _largeFiles : _largeFiles.Where(f => f.LastWriteUtc < cutoff)).ToList();
        shown.Sort(CompareLarge);

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            foreach (var file in shown.Take(MaxLargeRows))
                _list.Items.Add(FileRow(file, indent: false));
        }
        finally
        {
            _list.EndUpdate();
        }

        var total = shown.Sum(f => f.Size);
        var age = days == 0 ? string.Empty : $" untouched for {AgeChoices[_ageBox.SelectedIndex].Label}";
        var capped = shown.Count > MaxLargeRows ? $" · showing the first {MaxLargeRows:N0}" : string.Empty;

        SetStatus(
            shown.Count == 0
                ? $"No files of at least {SizeChoices[_sizeBox.SelectedIndex].Label}{age}."
                : $"{shown.Count:N0} file(s){age} · {ByteSize.Format(total)}{capped}",
            AppTheme.Current.TextMuted);

        UpdateActions();
    }

    private int CompareLarge(FileEntry a, FileEntry b)
    {
        var order = _sortColumn switch
        {
            0 => string.Compare(Path.GetFileName(a.Path), Path.GetFileName(b.Path), StringComparison.CurrentCultureIgnoreCase),
            2 => a.LastWriteUtc.CompareTo(b.LastWriteUtc),
            3 => string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase),
            _ => a.Size.CompareTo(b.Size),
        };

        return _sortDescending ? -order : order;
    }

    private void SortBy(int column)
    {
        // A duplicate set is read top to bottom as one unit; sorting rows would scatter it.
        if (_mode != Mode.Large)
            return;

        if (column == _sortColumn)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            // Size starts largest first; dates start oldest first, which is the question a
            // date column is clicked to answer here; names and folders start A to Z.
            _sortColumn = column;
            _sortDescending = column == 1;
        }

        ShowLargeFiles();
    }

    // ---- Duplicates ----------------------------------------------------------------------------

    private void ShowDuplicates()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            foreach (var group in _groups.Take(MaxDuplicateGroups))
            {
                var heading = new ListViewItem(
                    $"{group.Files.Count} copies · {ByteSize.Format(group.Wasted)} could be freed")
                {
                    Tag = group,
                    Font = AppTheme.UiFontBold,
                    ForeColor = AppTheme.Current.TextMuted,
                };

                heading.SubItems.Add(ByteSize.Format(group.Size));
                heading.SubItems.Add(string.Empty);
                heading.SubItems.Add(string.Empty);
                _list.Items.Add(heading);

                foreach (var file in group.Files)
                    _list.Items.Add(FileRow(file, indent: true));
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        var wasted = _groups.Sum(g => g.Wasted);
        var capped = _groups.Count > MaxDuplicateGroups ? $" · showing the first {MaxDuplicateGroups:N0}" : string.Empty;

        SetStatus(
            _groups.Count == 0
                ? $"No duplicate files of at least {SizeChoices[_sizeBox.SelectedIndex].Label}."
                : $"{_groups.Count:N0} set(s) of identical files · {ByteSize.Format(wasted)} in extra copies{capped}",
            AppTheme.Current.TextMuted);

        UpdateActions();
    }

    private void SelectExtraCopies()
    {
        _list.BeginUpdate();
        try
        {
            var first = true;

            foreach (ListViewItem item in _list.Items)
            {
                if (item.Tag is DuplicateGroup)
                {
                    item.Selected = false;
                    first = true;
                    continue;
                }

                item.Selected = !first;
                first = false;
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _list.Focus();
    }

    // ---- Acting on the selection ---------------------------------------------------------------

    private static ListViewItem FileRow(FileEntry file, bool indent)
    {
        var item = new ListViewItem((indent ? "    " : string.Empty) + Path.GetFileName(file.Path)) { Tag = file };
        item.SubItems.Add(ByteSize.Format(file.Size));
        item.SubItems.Add(file.LastWriteUtc == default ? "-" : file.LastWriteUtc.ToLocalTime().ToString("yyyy-MM-dd"));
        item.SubItems.Add(Path.GetDirectoryName(file.Path) ?? string.Empty);
        return item;
    }

    private List<FileEntry> SelectedFiles() =>
        [.. _list.SelectedItems.Cast<ListViewItem>().Select(i => i.Tag).OfType<FileEntry>()];

    private void UpdateActions()
    {
        var selected = SelectedFiles().Count;

        _revealButton.Enabled = !IsBusy && selected > 0;
        _recycleButton.Enabled = !IsBusy && selected > 0;
        _extraCopiesButton.Visible = _mode == Mode.Duplicates;
        _extraCopiesButton.Enabled = !IsBusy && _groups.Count > 0;
        _ageBox.Enabled = !IsBusy && _mode != Mode.Duplicates;
    }

    private void RevealSelected()
    {
        if (SelectedFiles().FirstOrDefault() is not { } file)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                $"/select,\"{file.Path}\""));
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open the folder: {ex.Message}", AppTheme.Current.RiskAdvanced);
        }
    }

    private void RecycleSelected()
    {
        if (IsBusy)
            return;

        var files = SelectedFiles();
        if (files.Count == 0)
            return;

        // A set with every copy selected would leave nothing behind. That is not what anyone
        // looking for duplicates means to do, so it is refused rather than asked about.
        if (_mode == Mode.Duplicates)
        {
            var chosen = files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var emptied = _groups.Count(g => g.Files.All(f => chosen.Contains(f.Path)));

            if (emptied > 0)
            {
                SetStatus(
                    $"Every copy of {emptied} set(s) is selected. Leave at least one copy of each "
                    + "set unselected; \"Select all but the oldest\" does that.",
                    AppTheme.Current.RiskReview);
                return;
            }
        }

        var refused = files.Where(f => RecycleBin.Refusal(f.Path) is not null).ToList();
        var allowed = files.Except(refused).ToList();

        if (allowed.Count == 0)
        {
            SetStatus(
                $"{Path.GetFileName(refused[0].Path)} cannot be removed from here. {RecycleBin.Refusal(refused[0].Path)}",
                AppTheme.Current.RiskReview);
            return;
        }

        var total = allowed.Sum(f => f.Size);
        var skipped = refused.Count > 0
            ? $"\n\n{refused.Count} selected file(s) are part of Windows or an installed program and are left alone."
            : string.Empty;

        var answer = MessageBox.Show(
            this,
            $"Move {allowed.Count} file(s), {ByteSize.Format(total)}, to the Recycle Bin?\n\n"
            + "They can be restored from the Recycle Bin until it is emptied. The space is only "
            + $"freed once it is.{skipped}",
            "Move to Recycle Bin",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK)
            return;

        var gone = RecycleBin.Send([.. allowed.Select(f => f.Path)], Handle)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Record(allowed, gone);

        _largeFiles.RemoveAll(f => gone.Contains(f.Path));
        _groups = [.. _groups
            .Select(g => g with { Files = [.. g.Files.Where(f => !gone.Contains(f.Path))] })
            .Where(g => g.Files.Count > 1)];

        if (_mode == Mode.Large)
            ShowLargeFiles();
        else
            ShowDuplicates();

        var moved = allowed.Where(f => gone.Contains(f.Path)).Sum(f => f.Size);
        var failed = allowed.Count - gone.Count;

        SetStatus(
            failed == 0
                ? $"Moved {gone.Count} file(s), {ByteSize.Format(moved)}, to the Recycle Bin."
                : $"Moved {gone.Count} file(s), {ByteSize.Format(moved)}, to the Recycle Bin. "
                  + $"{failed} could not be moved; they may be open in another program.",
            failed == 0 ? AppTheme.Current.RiskSafe : AppTheme.Current.RiskReview);
    }

    /// <summary>Recycled files go in the audit log beside everything else this tool removed.</summary>
    private void Record(IEnumerable<FileEntry> attempted, IReadOnlySet<string> gone)
    {
        using var log = AuditLog.StartRun();
        var ruleId = _mode == Mode.Duplicates ? "files.duplicate" : "files.large";
        var ruleName = _mode == Mode.Duplicates ? "Duplicate file" : "Large file";

        foreach (var file in attempted)
        {
            var succeeded = gone.Contains(file.Path);

            log.Write(new AuditEntry
            {
                Timestamp = DateTimeOffset.Now,
                Path = file.Path,
                Bytes = succeeded ? file.Size : 0,
                RuleId = ruleId,
                RuleName = ruleName,
                Risk = "Chosen by hand",
                Disposal = "RecycleBin",
                Succeeded = succeeded,
                Error = succeeded ? null : "Still there after the Recycle Bin move.",
            });
        }
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    /// <summary>Last two segments of a path, which is enough to recognize it.</summary>
    private static string Shorten(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 2 ? path : Path.Combine("...", segments[^2], segments[^1]);
    }

    protected override void ApplyTheme()
    {
        base.ApplyTheme();
        var palette = AppTheme.Current;

        foreach (var box in new[] { _rootBox, _sizeBox, _ageBox })
        {
            box.BackColor = palette.SurfaceAlt;
            box.ForeColor = palette.Text;
        }

        _status.ForeColor = palette.TextMuted;
        _sizeLabel.ForeColor = palette.TextMuted;
        _ageLabel.ForeColor = palette.TextMuted;
        _list.BackColor = palette.Surface;
        _list.ForeColor = palette.Text;

        foreach (Control control in Body.Controls)
        {
            if (control != _list)
                control.BackColor = palette.Bg;
        }

        // The set headings carry their color with them, so a theme switch has to repaint them.
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is DuplicateGroup)
                item.ForeColor = palette.TextMuted;
        }
    }

    public override void OnActivated() => _list.Focus();
}
