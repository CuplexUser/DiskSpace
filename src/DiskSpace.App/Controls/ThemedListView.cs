using DiskSpace.App.Platform;
using DiskSpace.App.Theme;

namespace DiskSpace.App.Controls;

/// <summary>
/// A details-view list whose column header follows the palette.
///
/// A <see cref="ListView"/> header ignores BackColor and ForeColor entirely: it is a separate
/// native control drawn by the visual style, so on a dark window it stays light gray and is the
/// one bright band in the view. Owner drawing the header is the only reliable fix that does not
/// depend on the undocumented dark mode theme names.
///
/// Rows keep their default drawing, because the native control already handles selection,
/// focus rectangles and per-item colors correctly.
/// </summary>
public class ThemedListView : ListView
{
    private const int HeaderPadding = 8;

    public ThemedListView()
    {
        View = View.Details;
        FullRowSelect = true;
        BorderStyle = BorderStyle.None;
        OwnerDraw = true;
        DoubleBuffered = true;

        AppTheme.Changed += OnThemeChanged;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeTheme();
    }

    protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
    {
        var palette = AppTheme.Current;

        using (var background = new SolidBrush(palette.SurfaceAlt))
            e.Graphics.FillRectangle(background, e.Bounds);

        using (var line = new Pen(palette.Border))
        {
            // Underline the whole header, and separate this column from the next.
            e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(line, e.Bounds.Right - 1, e.Bounds.Top + 5, e.Bounds.Right - 1, e.Bounds.Bottom - 6);
        }

        var alignment = e.Header?.TextAlign switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };

        var text = new Rectangle(
            e.Bounds.X + HeaderPadding,
            e.Bounds.Y,
            Math.Max(0, e.Bounds.Width - (HeaderPadding * 2)),
            e.Bounds.Height);

        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? string.Empty,
            AppTheme.UiFont,
            text,
            palette.TextMuted,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix);
    }

    protected override void OnDrawItem(DrawListViewItemEventArgs e)
    {
        e.DrawDefault = true;
        base.OnDrawItem(e);
    }

    protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
    {
        e.DrawDefault = true;
        base.OnDrawSubItem(e);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(() => OnThemeChanged(sender, e));
            return;
        }

        ApplyNativeTheme();
        Invalidate();
    }

    private void ApplyNativeTheme()
    {
        if (!IsHandleCreated)
            return;

        var palette = AppTheme.Current;
        BackColor = palette.Surface;
        ForeColor = palette.Text;

        // Without this the scrollbar stays bright white inside a dark window.
        NativeMethods.ApplyExplorerTheme(Handle, palette.IsDark);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            AppTheme.Changed -= OnThemeChanged;

        base.Dispose(disposing);
    }
}
