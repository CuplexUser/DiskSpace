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
/// Shows every path, grouped, with no summarising away of the detail. A selection of nothing but
/// caches confirms in a single click; anything rated Review or Advanced requires typing DELETE,
/// because there is no Recycle Bin behind this and no undo.
/// </summary>
public sealed class CleanupConfirmDialog : Form
{
    private const string ConfirmWord = "DELETE";

    private readonly CleanupPlan _plan;
    private readonly TextBox _confirmBox = new();
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
        UpdateConfirmState();
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
                var disposal = item.Disposal == Disposal.Quarantine ? "quarantine" : "delete";
                _paths.Items.Add(new PathRow(
                    $"    [{disposal}] {item.Path}  ({ByteSize.Format(item.Size)})",
                    IsHeading: false,
                    item.Risk));
            }
        }

        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 8, 18, 8) };
        listHost.Controls.Add(_paths);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 96 };

        _prompt.AutoSize = false;
        // Full dialog width: the prompt names the confirmation word, so clipping it
        // would hide the instruction the button depends on.
        _prompt.Bounds = new Rectangle(18, 8, ClientSize.Width - 36, 22);
        _prompt.Font = AppTheme.UiFont;

        _confirmBox.Bounds = new Rectangle(18, 34, 160, 26);
        _confirmBox.BorderStyle = BorderStyle.FixedSingle;
        _confirmBox.Font = AppTheme.UiFont;
        _confirmBox.TextChanged += (_, _) => UpdateConfirmState();
        _confirmBox.Visible = _plan.NeedsExplicitConfirmation;

        // Both buttons are placed from the right edge in one calculation, and Cancel is
        // positioned off Confirm rather than off its own guess at the edge. Two independent
        // offsets are what let these overlap in the first place.
        const int Margin = 18;
        const int Gap = 10;
        const int ButtonTop = 34;
        const int ButtonHeight = 30;
        const int ConfirmWidth = 104;
        const int CancelWidth = 92;

        var confirmLeft = ClientSize.Width - Margin - ConfirmWidth;
        var cancelLeft = confirmLeft - Gap - CancelWidth;

        _cancelButton.Text = "Cancel";
        _cancelButton.Bounds = new Rectangle(cancelLeft, ButtonTop, CancelWidth, ButtonHeight);
        _cancelButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _confirmButton.Kind = ButtonKind.Danger;
        _confirmButton.Bounds = new Rectangle(confirmLeft, ButtonTop, ConfirmWidth, ButtonHeight);
        _confirmButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _confirmButton.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        footer.Controls.AddRange([_prompt, _confirmBox, _cancelButton, _confirmButton]);

        Controls.Add(listHost);
        Controls.Add(footer);
        Controls.Add(header);

        CancelButton = null;
        AcceptButton = null;
    }

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

    private void UpdateConfirmState()
    {
        var palette = AppTheme.Current;

        if (!_plan.NeedsExplicitConfirmation)
        {
            _prompt.Text = "These are caches that regenerate on demand.";
            _prompt.ForeColor = palette.TextMuted;
            _confirmButton.Text = "Clean";
            _confirmButton.Kind = ButtonKind.Primary;
            _confirmButton.Enabled = true;
            return;
        }

        var matches = string.Equals(_confirmBox.Text.Trim(), ConfirmWord, StringComparison.Ordinal);

        _prompt.Text = _plan.ContainsQuarantine
            ? $"This selection includes items that are not simple caches. Type {ConfirmWord} to continue."
            : $"Deletion is permanent. There is no Recycle Bin. Type {ConfirmWord} to continue.";
        _prompt.ForeColor = palette.RiskReview;

        _confirmButton.Text = "Delete";
        _confirmButton.Kind = ButtonKind.Danger;
        _confirmButton.Enabled = matches;
    }

    private void ApplyTheme()
    {
        var palette = AppTheme.Current;

        BackColor = palette.Bg;
        _paths.BackColor = palette.Surface;
        _paths.ForeColor = palette.Text;
        _confirmBox.BackColor = palette.SurfaceAlt;
        _confirmBox.ForeColor = palette.Text;

        foreach (Control control in Controls)
            control.BackColor = palette.Bg;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyTitleBarTheme(Handle, AppTheme.Current.IsDark);
        NativeMethods.ApplyExplorerTheme(_paths.Handle, AppTheme.Current.IsDark);
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

            TextRenderer.DrawText(
                g, $"{ByteSize.Count(plan.TotalFileCount)} files", AppTheme.UiFontSmall,
                new Point(18, 68), palette.TextFaint, TextFormatFlags.NoPrefix);

            using var pen = new Pen(palette.Border);
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}
