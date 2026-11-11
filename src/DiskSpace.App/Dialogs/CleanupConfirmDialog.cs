using System.Drawing.Text;
using DiskSpace.App.Controls;
using DiskSpace.App.Platform;
using DiskSpace.App.Theme;
using DiskSpace.Core.Cleaning;
using DiskSpace.Core.Model;

namespace DiskSpace.App.Dialogs;

/// <summary>
/// The last thing between a selection and permanent deletion.
///
/// Shows every path, grouped, with no summarizing away of the detail, and confirms in a single
/// click. A selection that goes beyond caches says so above the buttons and turns the confirm
/// button into a destructive one, because there is no Recycle Bin behind this and no undo.
/// </summary>
public sealed class CleanupConfirmDialog : Form
{
    private readonly CleanupPlan _plan;
    private readonly AccentButton _confirmButton = new();
    private readonly AccentButton _cancelButton = new();
    private readonly ListBox _paths = new();
    private readonly Label _prompt = new();

    public CleanupConfirmDialog(CleanupPlan plan)
    {
        _plan = plan;

        Text = "Confirm cleanup";
        // FixedSingle rather than FixedDialog: a dialog-style frame refuses to draw a title
        // bar icon at all, so the window would stay anonymous however Icon was set.
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = true;
        AppIcon.Apply(this);
        ClientSize = new Size(720, 560);
        Font = AppTheme.UiFont;
        DoubleBuffered = true;

        BuildLayout();
        ApplyTheme();
        DescribePlan();
    }

    private void BuildLayout()
    {
        var header = new HeaderPanel(_plan) { Dock = DockStyle.Top, Height = 96 };

        _paths.Dock = DockStyle.Fill;
        _paths.BorderStyle = BorderStyle.None;
        _paths.Font = AppTheme.MonoFont;
        _paths.IntegralHeight = false;
        _paths.DrawMode = DrawMode.OwnerDrawFixed;
        _paths.ItemHeight = 18;
        _paths.DrawItem += DrawPathItem;

        foreach (var group in _plan.ByCategory.OrderByDescending(g => g.Sum(i => i.Size)))
        {
            _paths.Items.Add(new PathRow(
                $"{group.Key}:  {ByteSize.Format(group.Sum(i => i.Size))}", IsHeading: true, RiskLevel.Safe));

            foreach (var item in group.OrderByDescending(i => i.Size))
            {
                _paths.Items.Add(new PathRow(
                    $"    {DescribeItem(item)}  ({ByteSize.Format(item.Size)})",
                    IsHeading: false,
                    item.Risk));
            }
        }

        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 8) };
        listHost.Controls.Add(_paths);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 96 };

        _prompt.AutoSize = false;
        // Full dialog width: the prompt is the only warning left on this dialog, so clipping
        // it would hide the one thing the button depends on being read.
        _prompt.Bounds = new Rectangle(18, 8, ClientSize.Width - 36, 22);
        _prompt.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _prompt.Font = AppTheme.UiFont;

        const int Margin = 18;
        const int Gap = 10;
        const int ButtonTop = 34;
        const int ButtonHeight = 30;
        const int ConfirmWidth = 104;
        const int CancelWidth = 92;

        _cancelButton.Text = "Cancel";
        _cancelButton.Size = new Size(CancelWidth, ButtonHeight);
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _confirmButton.Size = new Size(ConfirmWidth, ButtonHeight);
        _confirmButton.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        // A docked Panel is still DefaultSize until it is parented. Positioning Cancel
        // and Delete from that width, then anchoring them Right, pushed both off the
        // edge once the footer stretched to the dialog.
        void PositionButtons()
        {
            var confirmLeft = footer.ClientSize.Width - Margin - ConfirmWidth;
            var cancelLeft = confirmLeft - Gap - CancelWidth;
            _confirmButton.Location = new Point(confirmLeft, ButtonTop);
            _cancelButton.Location = new Point(cancelLeft, ButtonTop);
        }

        footer.Resize += (_, _) => PositionButtons();
        footer.Controls.AddRange([_prompt, _cancelButton, _confirmButton]);
        PositionButtons();

        Controls.Add(listHost);
        Controls.Add(footer);
        Controls.Add(header);

        CancelButton = null;
        AcceptButton = null;
    }

    /// <summary>
    /// One line per item. Something Windows removes itself is listed by name, since its
    /// location is a description of where the data lives and not a path this tool will delete.
    /// </summary>
    private static string DescribeItem(PlannedItem item) => item.Disposal switch
    {
        Disposal.Quarantine => $"[quarantine] {item.Path}",
        Disposal.Windows => $"[windows] {item.Rule.Name}: {item.Path}",
        _ => $"[delete] {item.Path}",
    };

    private void DrawPathItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || _paths.Items[e.Index] is not PathRow row)
            return;

        var palette = AppTheme.Current;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        using (var background = new SolidBrush(palette.Surface))
            e.Graphics.FillRectangle(background, e.Bounds);

        var color = row.IsHeading
            ? palette.Text
            : row.Risk switch
            {
                RiskLevel.Review => palette.RiskReview,
                RiskLevel.Advanced => palette.RiskAdvanced,
                _ => palette.TextMuted,
            };

        TextRenderer.DrawText(
            e.Graphics, row.Text,
            row.IsHeading ? AppTheme.UiFontBold : AppTheme.MonoFont,
            e.Bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }

    private void DescribePlan()
    {
        var palette = AppTheme.Current;

        if (!_plan.GoesBeyondCaches)
        {
            _prompt.Text = "These are caches that regenerate on demand.";
            _prompt.ForeColor = palette.TextMuted;
            _confirmButton.Text = "Clean";
            _confirmButton.Kind = ButtonKind.Primary;
            return;
        }

        _prompt.Text = _plan.ContainsQuarantine
            ? "This selection includes items that are not simple caches."
            : "Deletion is permanent. There is no Recycle Bin.";
        _prompt.ForeColor = palette.RiskReview;

        _confirmButton.Text = "Delete";
        _confirmButton.Kind = ButtonKind.Danger;
    }

    private void ApplyTheme()
    {
        var palette = AppTheme.Current;

        BackColor = palette.Bg;
        _paths.BackColor = palette.Surface;
        _paths.ForeColor = palette.Text;

        foreach (Control control in Controls)
            control.BackColor = palette.Bg;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyTitleBarTheme(Handle, AppTheme.Current.IsDark);
        NativeMethods.ApplyExplorerTheme(_paths.Handle, AppTheme.Current.IsDark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // With no typed confirmation, a stray Enter or Space must not be what deletes: the
        // keyboard starts on Cancel, and confirming takes a deliberate click or Tab.
        _cancelButton.Focus();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    private sealed record PathRow(string Text, bool IsHeading, RiskLevel Risk)
    {
        public override string ToString() => Text;
    }

    /// <summary>The headline numbers, so the scale of the operation is unmissable.</summary>
    private sealed class HeaderPanel(CleanupPlan plan) : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var palette = AppTheme.Current;

            TextRenderer.DrawText(
                g, $"Reclaim {ByteSize.Format(plan.TotalSize)}", AppTheme.HeadingFont,
                new Point(18, 16), palette.Text, TextFormatFlags.NoPrefix);

            var quarantined = plan.Items.Count(i => i.Disposal == Disposal.Quarantine);
            var deleted = plan.Items.Count - quarantined;

            var summary = quarantined > 0
                ? $"{deleted} item(s) deleted permanently · {quarantined} quarantined and restorable"
                : $"{deleted} item(s), deleted permanently";

            TextRenderer.DrawText(
                g, summary, AppTheme.UiFont,
                new Point(18, 46), palette.TextMuted, TextFormatFlags.NoPrefix);

            // Windows does not say how many files its own cleanups cover, so the count is of
            // the files this tool walked, and is left out when there are none.
            if (plan.TotalFileCount > 0)
            {
                var windowsOwned = plan.Items.Count(i => i.Disposal == Disposal.Windows);

                var files = windowsOwned > 0
                    ? $"{ByteSize.Count(plan.TotalFileCount)} files, plus {windowsOwned} item(s) Windows removes itself"
                    : $"{ByteSize.Count(plan.TotalFileCount)} files";

                TextRenderer.DrawText(
                    g, files, AppTheme.UiFontSmall,
                    new Point(18, 68), palette.TextFaint, TextFormatFlags.NoPrefix);
            }

            using var pen = new Pen(palette.Border);
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}
