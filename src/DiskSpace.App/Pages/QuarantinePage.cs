using DiskSpace.App.Controls;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Quarantine;

namespace DiskSpace.App.Pages;

/// <summary>
/// Staged folders awaiting purge, with restore.
///
/// This page is the reason orphan detection is allowed to be a heuristic at all: anything it got
/// wrong can be put back from here, exactly where it came from, until the retention period runs
/// out.
/// </summary>
public sealed class QuarantinePage : PageBase
{
    private readonly ThemedListView _list = new();
    private readonly AccentButton _restoreButton = new();
    private readonly AccentButton _purgeButton = new();
    private readonly AccentButton _refreshButton = new();
    private readonly Label _status = new();
    private readonly QuarantineStore _store;

    public QuarantinePage(QuarantineStore store)
        : base("Quarantine", "Items staged for recovery. They are purged automatically once retention expires.")
    {
        _store = store;

        BuildToolbar();
        BuildList();
        ApplyTheme();
    }

    public event EventHandler? Changed;

    private void BuildToolbar()
    {
        _restoreButton.Text = "Restore";
        _restoreButton.Width = 92;
        _restoreButton.Location = new Point(Gutter, 15);
        _restoreButton.Enabled = false;
        _restoreButton.Click += async (_, _) => await RestoreSelectedAsync();

        _purgeButton.Text = "Purge now";
        _purgeButton.Kind = ButtonKind.Danger;
        _purgeButton.Width = 100;
        _purgeButton.Location = new Point(Gutter + 102, 15);
        _purgeButton.Enabled = false;
        _purgeButton.Click += (_, _) => PurgeSelected();

        _refreshButton.Text = "Refresh";
        _refreshButton.Width = 88;
        _refreshButton.Location = new Point(Gutter + 214, 15);
        _refreshButton.Click += (_, _) => Reload();

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 60 };
        toolbar.Controls.AddRange([_restoreButton, _purgeButton, _refreshButton]);

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = AppTheme.UiFont;
        _status.Padding = new Padding(Gutter, 0, Gutter, 0);

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 30 };
        statusBar.Controls.Add(_status);

        Body.Controls.Add(_list);
        Body.Controls.Add(statusBar);
        Body.Controls.Add(toolbar);
    }

    private void BuildList()
    {
        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.BorderStyle = BorderStyle.None;
        _list.MultiSelect = true;
        _list.HideSelection = false;
        _list.Font = AppTheme.UiFont;

        _list.Columns.Add("Original location", 420);
        _list.Columns.Add("Size", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Files", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Quarantined", 150);
        _list.Columns.Add("Purges in", 110);
        _list.Columns.Add("Kept as", 100);

        _list.SelectedIndexChanged += (_, _) =>
        {
            var any = _list.SelectedItems.Count > 0;
            _restoreButton.Enabled = any;
            _purgeButton.Enabled = any;
        };
    }

    public override void OnActivated() => Reload();

    private void Reload()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            foreach (var manifest in _store.List())
            {
                var due = manifest.PurgeAfter - DateTimeOffset.Now;
                var item = new ListViewItem(manifest.OriginalPath) { Tag = manifest };

                item.SubItems.Add(ByteSize.Format(manifest.OriginalSize));
                item.SubItems.Add(manifest.FileCount == 0 ? "-" : ByteSize.Count(manifest.FileCount));
                item.SubItems.Add(manifest.QuarantinedAt.ToString("yyyy-MM-dd HH:mm"));
                item.SubItems.Add(due <= TimeSpan.Zero
                    ? "due now"
                    : due.TotalDays >= 1
                        ? $"{(int)due.TotalDays} days"
                        : $"{(int)due.TotalHours} hours");
                item.SubItems.Add(manifest.IsArchive ? "archive" : "moved aside");

                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        var total = _store.List().Sum(m => m.OriginalSize);
        _status.Text = _list.Items.Count == 0
            ? "Nothing quarantined."
            : $"{_list.Items.Count} item(s) staged · {ByteSize.Format(total)} recoverable";
        _status.ForeColor = AppTheme.Current.TextMuted;
    }

    private IReadOnlyList<QuarantineManifest> SelectedManifests() =>
        [.. _list.SelectedItems.Cast<ListViewItem>().Select(i => (QuarantineManifest)i.Tag!)];

    private async Task RestoreSelectedAsync()
    {
        var manifests = SelectedManifests();
        if (manifests.Count == 0)
            return;

        _restoreButton.Enabled = false;
        var restored = 0;

        foreach (var manifest in manifests)
        {
            _status.Text = $"Restoring {manifest.OriginalPath}…";

            try
            {
                await _store.RestoreAsync(manifest);
                restored++;
            }
            catch (Exception ex)
            {
                _status.Text = $"Could not restore {manifest.OriginalPath}: {ex.Message}";
                _status.ForeColor = AppTheme.Current.RiskAdvanced;
                break;
            }
        }

        if (restored > 0 && _status.ForeColor != AppTheme.Current.RiskAdvanced)
        {
            _status.Text = $"Restored {restored} item(s).";
            _status.ForeColor = AppTheme.Current.RiskSafe;
        }

        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void PurgeSelected()
    {
        var manifests = SelectedManifests();
        if (manifests.Count == 0)
            return;

        var total = manifests.Sum(m => m.OriginalSize);
        var answer = MessageBox.Show(
            this,
            $"Permanently delete {manifests.Count} quarantined item(s), freeing "
            + $"{ByteSize.Format(total)}?\n\nThis cannot be undone.",
            "Purge quarantine",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK)
            return;

        foreach (var manifest in manifests)
        {
            try
            {
                QuarantineStore.Purge(manifest);
            }
            catch (Exception ex)
            {
                _status.Text = $"Could not purge {manifest.Id}: {ex.Message}";
                _status.ForeColor = AppTheme.Current.RiskAdvanced;
            }
        }

        Reload();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override void ApplyTheme()
    {
        base.ApplyTheme();
        var palette = AppTheme.Current;

        _list.BackColor = palette.Surface;
        _list.ForeColor = palette.Text;
        _status.ForeColor = palette.TextMuted;

        foreach (Control control in Body.Controls)
        {
            if (control != _list)
                control.BackColor = palette.Bg;
        }

        if (_list.IsHandleCreated)
            Platform.NativeMethods.ApplyExplorerTheme(_list.Handle, palette.IsDark);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Platform.NativeMethods.ApplyExplorerTheme(_list.Handle, AppTheme.Current.IsDark);
    }
}
