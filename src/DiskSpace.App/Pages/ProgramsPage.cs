using DiskSpace.App.Controls;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Programs;

namespace DiskSpace.App.Pages;

/// <summary>
/// What installed software occupies.
///
/// The Scan page knows about caches because someone wrote a rule for each one. This page asks a
/// different question, and one no rule catalog can answer: of the tens of gigabytes that are not
/// cache, which program put them there? It measures rather than deletes, and hands any actual
/// removal to the uninstaller the program shipped with.
/// </summary>
public sealed class ProgramsPage : PageBase
{
    private readonly AccentButton _measureButton = new();
    private readonly ComboBox _sourceBox = new();
    private readonly TextBox _search = new();
    private readonly ThemedListView _list = new();
    private readonly ProgramDetailPane _detail = new();
    private readonly SplitContainer _split = new();
    private readonly Label _status = new();
    private readonly ProgressStrip _progress = new();

    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<ProgramFootprint> _footprints = [];
    private int _sortColumn = 4;
    private bool _sortAscending;

    public ProgramsPage()
        : base("Programs", "What installed software occupies, and where it keeps it.")
    {
        BuildToolbar();
        BuildBody();
        ApplyTheme();
    }

    private bool IsBusy => _cancellation is not null;

    private void BuildToolbar()
    {
        _measureButton.Text = "Measure";
        _measureButton.Kind = ButtonKind.Primary;
        _measureButton.Width = 92;
        _measureButton.Location = new Point(Gutter, 15);
        _measureButton.Click += async (_, _) => await ToggleMeasureAsync();

        _sourceBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _sourceBox.FlatStyle = FlatStyle.Flat;
        _sourceBox.Font = AppTheme.UiFont;
        _sourceBox.Width = 210;
        _sourceBox.Location = new Point(Gutter + 102, 16);
        _sourceBox.Items.AddRange(
        [
            "Everything",
            "Installed programs",
            "Store apps",
            "In your profile",
            "Windows components",
        ]);

        _sourceBox.SelectedIndex = 0;
        _sourceBox.SelectedIndexChanged += (_, _) => Populate();

        _search.Font = AppTheme.UiFont;
        _search.Width = 220;
        _search.Location = new Point(Gutter + 324, 16);
        _search.PlaceholderText = "Filter by name or publisher";
        _search.TextChanged += (_, _) => Populate();

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 60 };
        toolbar.Controls.AddRange([_measureButton, _sourceBox, _search]);

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = AppTheme.UiFont;
        _status.Padding = new Padding(Gutter, 0, Gutter, 0);
        _status.Text = "Ready. Measuring only reads; nothing here is removed without the "
                       + "program's own uninstaller.";

        _progress.Dock = DockStyle.Top;
        _progress.Visible = false;

        var statusBar = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        statusBar.Controls.Add(_status);
        statusBar.Controls.Add(_progress);

        Body.Controls.Add(_split);
        Body.Controls.Add(statusBar);
        Body.Controls.Add(toolbar);
    }

    private void BuildBody()
    {
        _split.Dock = DockStyle.Fill;
        _split.Orientation = Orientation.Vertical;
        _split.SplitterWidth = 6;

        _list.Dock = DockStyle.Fill;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.Columns.Add("Name", 220);
        _list.Columns.Add("Publisher", 150);
        _list.Columns.Add("Version", 90);
        _list.Columns.Add("Installed", 90);
        _list.Columns.Add("Total", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Data", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Source", 120);

        _list.ColumnClick += (_, e) => SortBy(e.Column);
        _list.SelectedIndexChanged += (_, _) => _detail.Show(Selected());

        _detail.UninstallRequested += (_, footprint) => Uninstall(footprint);

        _split.Panel1.Controls.Add(_list);
        _split.Panel2.Controls.Add(_detail);

        PositionSplitOnFirstShow(_split, width => Math.Max(420, (int)(width * 0.58)));
    }

    private ProgramFootprint? Selected() =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as ProgramFootprint : null;

    private async Task ToggleMeasureAsync()
    {
        if (IsBusy)
        {
            await _cancellation!.CancelAsync();
            return;
        }

        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        _progress.Start();

        var progress = new Progress<ProgramProgress>(p =>
        {
            _progress.Report(p.Completed, p.Total);
            _status.Text = $"Measuring {p.Completed}/{p.Total} · {p.CurrentProgram}";
        });

        try
        {
            _footprints = await new ProgramCatalog().MeasureAsync(progress, _cancellation.Token);
            Populate();

            var measured = _footprints.Where(f => f.Program.Risk != RiskLevel.ReportOnly)
                .Sum(f => f.TotalSize);

            var components = _footprints.Where(f => f.Program.Risk == RiskLevel.ReportOnly)
                .Sum(f => f.TotalSize);

            // Said plainly, because it would otherwise look like an error: Windows scatters a
            // product's bytes across the component store, the MSI cache and the driver store,
            // and nothing ties those back to the product that caused them.
            _status.Text =
                $"{ByteSize.Format(measured)} across {_footprints.Count} entries, plus "
                + $"{ByteSize.Format(components)} of Windows components  ·  install sizes are a "
                + "floor: Windows keeps part of every program outside its own folder";

            _status.ForeColor = AppTheme.Current.TextMuted;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Measurement cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Measurement failed: {ex.Message}";
            _status.ForeColor = AppTheme.Current.RiskAdvanced;
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void Populate()
    {
        var filtered = _footprints.Where(Matches).ToList();
        filtered.Sort(Compare);

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();

            foreach (var footprint in filtered)
            {
                var program = footprint.Program;

                var item = new ListViewItem(program.Name) { Tag = footprint };
                item.SubItems.Add(program.Publisher ?? string.Empty);
                item.SubItems.Add(program.Version ?? string.Empty);
                item.SubItems.Add(program.InstallDate?.ToString("yyyy-MM-dd") ?? string.Empty);
                item.SubItems.Add(FormatSize(footprint));
                item.SubItems.Add(footprint.DataSize > 0 ? ByteSize.Format(footprint.DataSize) : string.Empty);
                item.SubItems.Add(SourceLabel(program.Source));

                // Report-only rows are dimmed for the same reason the Scan page dims them: they
                // explain the disk but are not something to act on here.
                if (program.Risk == RiskLevel.ReportOnly)
                    item.ForeColor = AppTheme.Current.TextMuted;

                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        _detail.Show(null);
    }

    /// <summary>A tilde where the number is the installer's claim rather than a measurement.</summary>
    private static string FormatSize(ProgramFootprint footprint) =>
        footprint.TotalSize == 0 && !footprint.SizeIsEstimated
            ? "-"
            : (footprint.SizeIsEstimated ? "~" : string.Empty) + ByteSize.Format(footprint.TotalSize);

    private static string SourceLabel(ProgramSource source) => source switch
    {
        ProgramSource.Registry => "Installed program",
        ProgramSource.StorePackage => "Store app",
        ProgramSource.UserInstall => "In your profile",
        _ => "Windows component",
    };

    private bool Matches(ProgramFootprint footprint)
    {
        if (_sourceBox.SelectedIndex > 0
            && (int)footprint.Program.Source != _sourceBox.SelectedIndex - 1)
        {
            return false;
        }

        var term = _search.Text.Trim();
        if (term.Length == 0)
            return true;

        return footprint.Program.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
               || (footprint.Program.Publisher?.Contains(term, StringComparison.OrdinalIgnoreCase)
                   ?? false);
    }

    private void SortBy(int column)
    {
        // Clicking the same header again reverses it, which is what a details list does
        // everywhere else in Windows.
        if (column == _sortColumn)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;

            // Size columns are worth seeing largest first; text columns alphabetically.
            _sortAscending = column is not (4 or 5);
        }

        Populate();
    }

    private int Compare(ProgramFootprint left, ProgramFootprint right)
    {
        var result = _sortColumn switch
        {
            0 => string.Compare(left.Program.Name, right.Program.Name, StringComparison.OrdinalIgnoreCase),
            1 => string.Compare(left.Program.Publisher, right.Program.Publisher, StringComparison.OrdinalIgnoreCase),
            2 => string.Compare(left.Program.Version, right.Program.Version, StringComparison.OrdinalIgnoreCase),
            3 => Nullable.Compare(left.Program.InstallDate, right.Program.InstallDate),
            5 => left.DataSize.CompareTo(right.DataSize),
            6 => left.Program.Source.CompareTo(right.Program.Source),
            _ => left.TotalSize.CompareTo(right.TotalSize),
        };

        return _sortAscending ? result : -result;
    }

    private void Uninstall(ProgramFootprint footprint)
    {
        var program = footprint.Program;

        // The command is named before it runs, in the same spirit as the cleanup dialog listing
        // every path it will touch.
        var answer = MessageBox.Show(
            this,
            $"Start the uninstaller for {program.Name}?\n\n"
            + $"{ProgramUninstaller.Describe(program)}\n\n"
            + "DiskSpace does not remove the files itself. The program's own uninstaller runs, "
            + "and it may ask its own questions.",
            "Uninstall",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning);

        if (answer != DialogResult.OK)
            return;

        try
        {
            if (ProgramUninstaller.Start(program) is null)
            {
                _status.Text = $"{program.Name} records no uninstall command.";
                _status.ForeColor = AppTheme.Current.RiskReview;
                return;
            }

            _status.Text = $"Uninstaller started for {program.Name}. Measure again once it "
                           + "has finished.";
            _status.ForeColor = AppTheme.Current.TextMuted;
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not start the uninstaller: {ex.Message}";
            _status.ForeColor = AppTheme.Current.RiskAdvanced;
        }
    }

    private void SetBusy(bool busy)
    {
        if (!busy)
            _progress.Stop();

        _measureButton.Text = busy ? "Cancel" : "Measure";
        _measureButton.Kind = busy ? ButtonKind.Danger : ButtonKind.Primary;
        _sourceBox.Enabled = !busy;
        _search.Enabled = !busy;
        _status.ForeColor = AppTheme.Current.TextMuted;
    }

    protected override void ApplyTheme()
    {
        base.ApplyTheme();
        var palette = AppTheme.Current;

        _sourceBox.BackColor = palette.SurfaceAlt;
        _sourceBox.ForeColor = palette.Text;
        _search.BackColor = palette.SurfaceAlt;
        _search.ForeColor = palette.Text;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _status.ForeColor = palette.TextMuted;
        _split.BackColor = palette.Border;
        _split.Panel1.BackColor = palette.Surface;
        _split.Panel2.BackColor = palette.Bg;

        foreach (Control control in Body.Controls)
            control.BackColor = palette.Bg;
    }

    public override void OnActivated() => _list.Focus();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _cancellation?.Cancel();

        base.Dispose(disposing);
    }
}
