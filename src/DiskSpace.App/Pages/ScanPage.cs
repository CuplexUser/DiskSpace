using DiskSpace.App.Controls;
using DiskSpace.App.Dialogs;
using DiskSpace.App.Theme;
using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;
using DiskSpace.Core.Quarantine;
using DiskSpace.Core.Rules;

namespace DiskSpace.App.Pages;

/// <summary>
/// The cleaner: measure what the rule catalog knows about, show it with its consequences, and
/// remove what the user selects.
/// </summary>
public sealed class ScanPage : PageBase
{
    private readonly FindingsTree _tree = new();
    private readonly FindingDetailPane _detail = new();
    private readonly SplitContainer _split = new();
    private readonly AccentButton _scanButton = new();
    private readonly AccentButton _cleanButton = new();
    private readonly Label _status = new();
    private readonly Label _selectionSummary = new();
    private readonly ProgressStrip _progress = new();

    private readonly QuarantineStore _quarantine;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<CleanupFinding> _findings = [];

    public ScanPage(QuarantineStore quarantine)
        : base("Scan", "Find reclaimable space, with the consequences of removing each item.")
    {
        _quarantine = quarantine;

        BuildToolbar();
        BuildBody();
        ApplyTheme();
    }

    /// <summary>Raised after a cleanup run, so the shell can refresh the quarantine badge.</summary>
    public event EventHandler? CleanupCompleted;

    private bool IsBusy => _cancellation is not null;

    private void BuildToolbar()
    {
        _scanButton.Text = "Scan";
        _scanButton.Kind = ButtonKind.Primary;
        _scanButton.Width = 92;
        _scanButton.Location = new Point(Gutter, 15);
        _scanButton.Click += async (_, _) => await ToggleScanAsync();

        _cleanButton.Text = "Clean selected";
        _cleanButton.Kind = ButtonKind.Danger;
        _cleanButton.Width = 124;
        _cleanButton.Location = new Point(Gutter + 102, 15);
        _cleanButton.Enabled = false;
        _cleanButton.Click += async (_, _) => await CleanAsync();

        _selectionSummary.AutoSize = false;
        _selectionSummary.Bounds = new Rectangle(Gutter + 240, 15, 460, 30);
        _selectionSummary.TextAlign = ContentAlignment.MiddleLeft;
        _selectionSummary.Font = AppTheme.UiFont;

        var toolbar = new Panel { Dock = DockStyle.Top, Height = 60 };
        toolbar.Controls.AddRange([_scanButton, _cleanButton, _selectionSummary]);

        _status.AutoSize = false;
        _status.Dock = DockStyle.Fill;
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Font = AppTheme.UiFont;
        _status.Padding = new Padding(Gutter, 0, Gutter, 0);
        _status.Text = "Ready. Scanning only measures. Nothing is removed until you say so.";

        _progress.Dock = DockStyle.Top;
        _progress.Visible = false;

        // The label fills what the strip leaves, so it is added first: docking is applied in
        // reverse of the order controls go in.
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

        _tree.Dock = DockStyle.Fill;
        _tree.SelectionChanged += (_, _) => UpdateSelectionSummary();
        _tree.FindingHighlighted += (_, finding) => _detail.Show(finding);

        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(_detail);

        PositionSplitOnFirstShow(_split, width => Math.Max(340, (int)(width * 0.55)));
    }

    private void UpdateSelectionSummary()
    {
        var count = _tree.Selected.Count;
        var palette = AppTheme.Current;

        _cleanButton.Enabled = count > 0 && !IsBusy;

        if (count == 0)
        {
            _selectionSummary.Text = "Nothing selected.";
            _selectionSummary.ForeColor = palette.TextFaint;
            return;
        }

        var quarantined = _tree.Selected.Count(f =>
            f.Rule.Risk == RiskLevel.Review && f.Rule.Id.StartsWith("orphan.", StringComparison.Ordinal));

        _selectionSummary.Text = quarantined > 0
            ? $"{count} selected · {ByteSize.Format(_tree.SelectedSize)} · {quarantined} will be quarantined"
            : $"{count} selected · {ByteSize.Format(_tree.SelectedSize)}";

        _selectionSummary.ForeColor = palette.Text;
    }

    private async Task ToggleScanAsync()
    {
        if (IsBusy)
        {
            await _cancellation!.CancelAsync();
            return;
        }

        _cancellation = new CancellationTokenSource();
        SetBusy(true, "Cancel");

        _progress.Start();

        var progress = new Progress<RuleProgress>(p =>
        {
            _progress.Report(p.Completed, p.Total);
            _status.Text = $"Measuring {p.Completed}/{p.Total} · {p.CurrentRule}";
        });

        try
        {
            _findings = await new RuleCatalog().ResolveAsync(progress, _cancellation.Token);
            _tree.Load(_findings);
            UpdateSelectionSummary();

            var actionable = _findings.Where(f => f.IsActionable).Sum(f => f.Size);
            var safe = _findings.Where(f => f.Rule.Risk == RiskLevel.Safe).Sum(f => f.Size);

            _status.Text =
                $"{ByteSize.Format(actionable)} reclaimable across {_findings.Count} findings · "
                + $"{ByteSize.Format(safe)} of that rated Safe";
            _status.ForeColor = AppTheme.Current.TextMuted;
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
            SetBusy(false, "Scan");
        }
    }

    private async Task CleanAsync()
    {
        var executor = new CleanupExecutor(_quarantine);
        var plan = executor.Plan(_tree.Selected);

        if (plan.Items.Count == 0)
            return;

        // Nothing is removed without this dialog: it lists every path the plan touches.
        using var dialog = new CleanupConfirmDialog(plan);
        if (dialog.ShowDialog(this) != DialogResult.OK)
            return;

        _cancellation = new CancellationTokenSource();
        SetBusy(true, "Scan");
        _cleanButton.Enabled = false;

        _progress.Start();

        var progress = new Progress<CleanupProgress>(p =>
        {
            _progress.Report(p.Completed, p.Total);
            _status.Text =
                $"Cleaning {p.Completed}/{p.Total} · {DescribeWork(p)} · "
                + $"{ByteSize.Format(p.BytesReclaimed)} reclaimed";
        });

        try
        {
            var report = await executor.ExecuteAsync(plan, progress, _cancellation.Token);

            _status.Text = report.FailedCount == 0
                ? $"Reclaimed {ByteSize.Format(report.BytesReclaimed)} from {report.SucceededCount} items "
                  + $"in {report.Duration.TotalSeconds:N1}s."
                : $"Reclaimed {ByteSize.Format(report.BytesReclaimed)}; {report.FailedCount} item(s) could "
                  + $"not be removed: {DescribeFirstFailure(report)}";

            _status.ForeColor = report.FailedCount == 0
                ? AppTheme.Current.RiskSafe
                : AppTheme.Current.RiskReview;

            CleanupCompleted?.Invoke(this, EventArgs.Empty);

            // Re-measure so the numbers reflect what is actually left.
            _cancellation.Dispose();
            _cancellation = null;
            SetBusy(false, "Scan");
            await ToggleScanAsync();
            return;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cleanup cancelled. Items already removed are recorded in the log.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Cleanup failed: {ex.Message}";
            _status.ForeColor = AppTheme.Current.RiskAdvanced;
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false, "Scan");
        }
    }

    /// <summary>
    /// What the run is doing right now: the file-level detail when there is one, otherwise the
    /// item being worked on. Naming it matters more than the count does, because one item can
    /// occupy the whole run.
    /// </summary>
    private static string DescribeWork(CleanupProgress progress)
    {
        if (progress.Detail is { Length: > 0 } detail)
            return detail;

        return progress.CurrentPath == "Done" ? "finishing" : Shorten(progress.CurrentPath);
    }

    /// <summary>Last two segments of a path, which is enough to recognise it.</summary>
    private static string Shorten(string path)
    {
        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 2 ? path : Path.Combine("...", segments[^2], segments[^1]);
    }

    private static string DescribeFirstFailure(CleanupReport report)
    {
        var failure = report.Failures.First();
        return failure.HeldBy is { } holder
            ? $"{Path.GetFileName(failure.Path)} is held by {holder}."
            : $"{Path.GetFileName(failure.Path)}: {failure.Error}";
    }

    private void SetBusy(bool busy, string scanLabel)
    {
        if (!busy)
            _progress.Stop();

        _scanButton.Text = busy ? "Cancel" : scanLabel;
        _scanButton.Kind = busy ? ButtonKind.Danger : ButtonKind.Primary;
        _cleanButton.Enabled = !busy && _tree.Selected.Count > 0;
        _status.ForeColor = AppTheme.Current.TextMuted;
    }

    protected override void ApplyTheme()
    {
        base.ApplyTheme();
        var palette = AppTheme.Current;

        _tree.BackColor = palette.Surface;
        _status.ForeColor = palette.TextMuted;
        _split.BackColor = palette.Border;
        _split.Panel1.BackColor = palette.Surface;
        _split.Panel2.BackColor = palette.Bg;

        foreach (Control control in Body.Controls)
            control.BackColor = palette.Bg;

        UpdateSelectionSummary();
    }

    public override void OnActivated() => _tree.Focus();
}
