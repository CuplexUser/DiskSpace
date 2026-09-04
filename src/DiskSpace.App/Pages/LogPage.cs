using System.Diagnostics;
using DiskSpace.App.Controls;
using DiskSpace.App.Theme;
using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;

namespace DiskSpace.App.Pages;

/// <summary>
/// Past cleanup runs, read back from the audit log.
///
/// Since deletion is permanent, this is the only account of what was removed — so the page shows
/// entries verbatim, including failures and the process that was holding a file, rather than a
/// tidied summary.
/// </summary>
public sealed class LogPage : PageBase
{
    private const int PathColumn = 4;
    private const int NoteColumn = 5;


    private readonly ListBox _runs = new();
    private readonly ThemedListView _entries = new();
    private readonly ThemedContextMenu _entryMenu = new();
    private ToolStripMenuItem _copyRows = null!;
    private ToolStripMenuItem _copyPath = null!;
    private ToolStripMenuItem _copyNote = null!;
    private readonly AccentButton _openFolderButton = new();
    private readonly Label _status = new();
    private readonly SplitContainer _split = new();

    public LogPage()
        : base("Log", "Every item this tool has removed, as it was recorded at the time.")
    {
        BuildToolbar();
        BuildBody();
        ApplyTheme();
    }

    private void BuildToolbar()
    {
        _openFolderButton.Text = "Open log folder";
        _openFolderButton.Width = 128;
        _openFolderButton.Location = new Point(Gutter, 15);
        _openFolderButton.Click += (_, _) => OpenLogFolder();

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 60 };
        toolbar.Controls.Add(_openFolderButton);

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = AppTheme.UiFont;
        _status.Padding = new Padding(Gutter, 0, Gutter, 0);

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
        _split.FixedPanel = FixedPanel.Panel1;

        _runs.Dock = DockStyle.Fill;
        _runs.BorderStyle = BorderStyle.None;
        _runs.Font = AppTheme.UiFont;
        _runs.IntegralHeight = false;
        _runs.SelectedIndexChanged += (_, _) => ShowSelectedRun();

        _entries.Dock = DockStyle.Fill;
        _entries.Font = AppTheme.UiFont;
        _entries.ContextMenuStrip = _entryMenu;
        _entries.KeyDown += OnEntriesKeyDown;
        _entries.Columns.Add("Time", 80);
        _entries.Columns.Add("Result", 70);
        _entries.Columns.Add("Size", 84, HorizontalAlignment.Right);
        _entries.Columns.Add("Rule", 150);
        _entries.Columns.Add("Path", 420);
        _entries.Columns.Add("Note", 200);

        BuildEntryMenu();

        _split.Panel1.Controls.Add(_runs);
        _split.Panel2.Controls.Add(_entries);

        // The run list is a narrow index; the entries beside it need the room.
        PositionSplitOnFirstShow(_split, _ => 240);
    }

    public override void OnActivated() => Reload();

    private void Reload()
    {
        _runs.Items.Clear();

        foreach (var run in AuditLog.ListRuns())
            _runs.Items.Add(new RunRow(run));

        if (_runs.Items.Count > 0)
        {
            _runs.SelectedIndex = 0;
        }
        else
        {
            _entries.Items.Clear();
            _status.Text = "No cleanup runs recorded yet.";
        }
    }

    private void ShowSelectedRun()
    {
        if (_runs.SelectedItem is not RunRow run)
            return;

        var entries = AuditLog.Read(run.Path);

        _entries.BeginUpdate();
        try
        {
            _entries.Items.Clear();

            foreach (var entry in entries)
            {
                var item = new ListViewItem(entry.Timestamp.ToString("HH:mm:ss"))
                {
                    ForeColor = entry.Succeeded
                        ? AppTheme.Current.Text
                        : AppTheme.Current.RiskAdvanced,
                };

                item.SubItems.Add(entry.Succeeded ? entry.Disposal.ToLowerInvariant() : "failed");
                item.SubItems.Add(ByteSize.Format(entry.Bytes));
                item.SubItems.Add(entry.RuleName);
                item.SubItems.Add(entry.Path);
                item.SubItems.Add(entry.HeldBy is { } held ? $"held by {held}" : entry.Error ?? string.Empty);

                _entries.Items.Add(item);
            }
        }
        finally
        {
            _entries.EndUpdate();
        }

        var reclaimed = entries.Where(e => e.Succeeded).Sum(e => e.Bytes);
        var failed = entries.Count(e => !e.Succeeded);

        _status.Text = failed == 0
            ? $"{entries.Count} item(s) · {ByteSize.Format(reclaimed)} reclaimed"
            : $"{entries.Count} item(s) · {ByteSize.Format(reclaimed)} reclaimed · {failed} failed";
        _status.ForeColor = AppTheme.Current.TextMuted;
    }

    /// <summary>
    /// A log entry is evidence: the reason someone opens this page is usually to take a path
    /// or an error somewhere else, so the rows have to be copyable rather than only readable.
    /// </summary>
    private void BuildEntryMenu()
    {
        _copyRows = _entryMenu.Add("Copy", () => CopyText(SelectedRowsAsText()), Keys.Control | Keys.C);
        _copyPath = _entryMenu.Add("Copy path", () => CopyText(SelectedField(PathColumn)));
        _copyNote = _entryMenu.Add("Copy note", () => CopyText(SelectedField(NoteColumn)));
        _entryMenu.AddSeparator();
        _entryMenu.Add("Copy all rows", () => CopyText(AllRowsAsText()));
        _entryMenu.AddSeparator();
        _entryMenu.Add("Select all", SelectAllEntries, Keys.Control | Keys.A);

        _entryMenu.Opening += (_, _) =>
        {
            var hasSelection = _entries.SelectedItems.Count > 0;
            _copyRows.Enabled = hasSelection;
            _copyPath.Enabled = hasSelection;
            _copyNote.Enabled = hasSelection && SelectedField(NoteColumn).Length > 0;
        };
    }

    private void OnEntriesKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopyText(SelectedRowsAsText());
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.A)
        {
            SelectAllEntries();
            e.Handled = true;
        }
    }

    private void SelectAllEntries()
    {
        foreach (ListViewItem item in _entries.Items)
            item.Selected = true;
    }

    /// <summary>The selected rows, tab separated, which is what pastes into a spreadsheet.</summary>
    private string SelectedRowsAsText() =>
        string.Join(Environment.NewLine, _entries.SelectedItems.Cast<ListViewItem>().Select(RowText));

    private string AllRowsAsText() =>
        string.Join(Environment.NewLine, _entries.Items.Cast<ListViewItem>().Select(RowText));

    private static string RowText(ListViewItem item) =>
        string.Join('	', item.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text));

    /// <summary>One column of the selected rows, one per line.</summary>
    private string SelectedField(int column) =>
        string.Join(
            Environment.NewLine,
            _entries.SelectedItems
                .Cast<ListViewItem>()
                .Select(item => item.SubItems[column].Text)
                .Where(text => text.Length > 0));

    private void CopyText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        try
        {
            Clipboard.SetText(text);
            _status.Text = "Copied to the clipboard.";
            _status.ForeColor = AppTheme.Current.TextMuted;
        }
        catch (Exception ex)
        {
            // Another process can hold the clipboard open; that is not worth an exception here.
            _status.Text = $"Could not copy: {ex.Message}";
            _status.ForeColor = AppTheme.Current.RiskAdvanced;
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(AuditLog.LogDirectory);
            Process.Start(new ProcessStartInfo(AuditLog.LogDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not open the log folder: {ex.Message}";
            _status.ForeColor = AppTheme.Current.RiskAdvanced;
        }
    }

    protected override void ApplyTheme()
    {
        base.ApplyTheme();
        var palette = AppTheme.Current;

        _runs.BackColor = palette.Surface;
        _runs.ForeColor = palette.Text;
        _entries.BackColor = palette.Surface;
        _entries.ForeColor = palette.Text;
        _status.ForeColor = palette.TextMuted;
        _split.BackColor = palette.Border;
        _split.Panel1.BackColor = palette.Surface;
        _split.Panel2.BackColor = palette.Surface;

        foreach (Control control in Body.Controls)
        {
            if (control != _split)
                control.BackColor = palette.Bg;
        }
    }

    private sealed record RunRow(string Path)
    {
        public override string ToString()
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(Path);
            // "cleanup-2026-09-04-142233" -> "2026-09-04 14:22:33"
            var stamp = name.Replace("cleanup-", string.Empty);
            return DateTime.TryParseExact(
                stamp, "yyyy-MM-dd-HHmmss", null,
                System.Globalization.DateTimeStyles.None, out var parsed)
                ? parsed.ToString("yyyy-MM-dd  HH:mm")
                : name;
        }
    }
}
