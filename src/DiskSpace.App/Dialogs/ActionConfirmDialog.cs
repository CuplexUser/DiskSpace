using System.Drawing.Text;
using DiskSpace.App.Controls;
using DiskSpace.App.Platform;
using DiskSpace.App.Theme;
using DiskSpace.Core.Model;
using DiskSpace.Core.Rules;

namespace DiskSpace.App.Dialogs;

/// <summary>
/// Asks before a finding's action runs: turning hibernation off, compacting a virtual disk,
/// dropping restore points. Each changes how the machine behaves rather than removing a cache,
/// so the dialog's whole content is what that change costs, in the rule's own words.
/// </summary>
public sealed class ActionConfirmDialog : Form
{
    private const int Inset = 20;

    private readonly AccentButton _confirmButton = new();
    private readonly AccentButton _cancelButton = new();

    public ActionConfirmDialog(CleanupFinding finding, RuleAction action)
    {
        Text = action.Label;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = true;
        AppIcon.Apply(this);
        Font = AppTheme.UiFont;
        DoubleBuffered = true;

        const int DialogWidth = 520;
        var textWidth = DialogWidth - (Inset * 2);

        var heading = new Label
        {
            AutoSize = false,
            Text = action.Label,
            Font = AppTheme.HeadingFont,
            Bounds = new Rectangle(Inset, 18, textWidth, 32),
            UseMnemonic = false,
        };

        var subject = new Label
        {
            AutoSize = false,
            Text = $"{finding.Rule.Name} · {ByteSize.Format(finding.Size)}",
            Font = AppTheme.UiFont,
            Bounds = new Rectangle(Inset, 52, textWidth, 20),
            UseMnemonic = false,
        };

        var bodyHeight = TextRenderer.MeasureText(
            action.Consequence, AppTheme.UiFont, new Size(textWidth, 1000),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;

        var body = new Label
        {
            AutoSize = false,
            Text = action.Consequence,
            Font = AppTheme.UiFont,
            Bounds = new Rectangle(Inset, 88, textWidth, bodyHeight + 4),
            UseMnemonic = false,
        };

        var buttonTop = body.Bottom + 24;

        _confirmButton.Text = action.Label;
        _confirmButton.Kind = ButtonKind.Danger;
        _confirmButton.Size = new Size(TextRenderer.MeasureText(action.Label, AppTheme.UiFont).Width + 36, 30);
        _confirmButton.Location = new Point(DialogWidth - Inset - _confirmButton.Width, buttonTop);
        _confirmButton.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        _cancelButton.Text = "Cancel";
        _cancelButton.Size = new Size(92, 30);
        _cancelButton.Location = new Point(_confirmButton.Left - 10 - _cancelButton.Width, buttonTop);
        _cancelButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange([heading, subject, body, _cancelButton, _confirmButton]);
        ClientSize = new Size(DialogWidth, buttonTop + 30 + Inset);

        var palette = AppTheme.Current;
        BackColor = palette.Bg;
        heading.ForeColor = palette.Text;
        subject.ForeColor = palette.TextMuted;
        body.ForeColor = palette.Text;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyTitleBarTheme(Handle, AppTheme.Current.IsDark);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // As in the cleanup dialog: a stray Enter must not be what changes the system.
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
}
