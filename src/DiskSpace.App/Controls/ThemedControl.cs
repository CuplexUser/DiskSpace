using System.ComponentModel;
using DiskSpace.App.Theme;

namespace DiskSpace.App.Controls;

/// <summary>
/// Base for every custom-painted control: double buffered, and repainted whenever the palette
/// swaps. Subscribing here rather than in each control is what makes a live theme change a
/// single event rather than a tree walk.
/// </summary>
public abstract class ThemedControl : Control
{
    protected ThemedControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        Font = AppTheme.UiFont;
        AppTheme.Changed += OnThemeChanged;
    }

    protected Palette Palette => AppTheme.Current;

    protected virtual void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated)
            return;

        if (InvokeRequired)
        {
            BeginInvoke(() => OnThemeChanged(sender, e));
            return;
        }

        ApplyTheme();
        Invalidate();
    }

    /// <summary>Hook for controls that must push colours into child or native controls.</summary>
    protected virtual void ApplyTheme()
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            AppTheme.Changed -= OnThemeChanged;

        base.Dispose(disposing);
    }

    /// <summary>Rounded-rectangle path, used for pills, cards and hover states.</summary>
    protected static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Max(1, radius * 2);

        if (diameter >= bounds.Width || diameter >= bounds.Height)
        {
            path.AddRectangle(bounds);
            return path;
        }

        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public override Color BackColor
    {
        get => base.BackColor;
        set => base.BackColor = value;
    }
}
