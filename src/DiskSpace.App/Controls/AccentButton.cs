using System.Drawing.Drawing2D;
using System.Drawing.Text;
using DiskSpace.App.Theme;

namespace DiskSpace.App.Controls;

public enum ButtonKind
{
    /// <summary>Filled with the accent colour. One per view, at most.</summary>
    Primary,

    /// <summary>Outlined. The default for everything else.</summary>
    Secondary,

    /// <summary>Outlined in the danger colour, for destructive actions.</summary>
    Danger,
}

public sealed class AccentButton : ThemedControl
{
    private bool _hovered;
    private bool _pressed;

    public AccentButton()
    {
        Size = new Size(96, 30);
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    [System.ComponentModel.DefaultValue(ButtonKind.Secondary)]
    public ButtonKind Kind { get; set; } = ButtonKind.Secondary;

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = true;
        Focus();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _pressed = false;
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var palette = Palette;
        g.Clear(Parent?.BackColor ?? palette.Bg);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedRect(bounds, 5);

        var accent = Kind switch
        {
            ButtonKind.Primary => palette.Accent,
            ButtonKind.Danger => palette.RiskAdvanced,
            _ => palette.BorderStrong,
        };

        if (!Enabled)
            accent = palette.Border;

        if (Kind == ButtonKind.Primary)
        {
            var fill = !Enabled
                ? palette.Border
                : _pressed ? palette.Accent
                : _hovered ? palette.AccentHover
                : palette.Accent;

            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        else if (_hovered && Enabled)
        {
            using var brush = new SolidBrush(palette.SurfaceHover);
            g.FillPath(brush, path);
        }

        using (var pen = new Pen(accent))
            g.DrawPath(pen, path);

        var textColor = Kind switch
        {
            _ when !Enabled => palette.TextFaint,
            ButtonKind.Primary => palette.AccentText,
            ButtonKind.Danger => palette.RiskAdvanced,
            _ => palette.Text,
        };

        TextRenderer.DrawText(
            g, Text, AppTheme.UiFont, bounds, textColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}
