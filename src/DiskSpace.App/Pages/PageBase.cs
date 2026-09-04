using System.Drawing.Text;
using DiskSpace.App.Controls;
using DiskSpace.App.Theme;

namespace DiskSpace.App.Pages;

/// <summary>
/// A content page inside the shell. Owns its own header — a title, a one-line description,
/// and a right-aligned strip for the page's actions.
/// </summary>
public abstract class PageBase : Panel
{
    protected const int HeaderHeight = 62;
    protected const int Gutter = 20;

    private readonly Panel _header;
    private readonly Panel _body;
    private readonly List<(SplitContainer Split, Func<int, int> DistanceForWidth)> _pendingSplits = [];

    protected PageBase(string title, string subtitle)
    {
        Title = title;
        Subtitle = subtitle;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;

        _body = new Panel { Dock = DockStyle.Fill };
        _header = new HeaderStrip(this) { Dock = DockStyle.Top, Height = HeaderHeight };

        Controls.Add(_body);
        Controls.Add(_header);

        AppTheme.Changed += OnThemeChanged;

        // Deliberately not ApplyTheme(): that is virtual, and a derived page has not yet run
        // its own constructor body at this point, so an override would see uninitialised
        // fields. Each page calls ApplyTheme() itself once it is fully constructed.
        ApplyBaseColors();
    }

    public string Title { get; }
    public string Subtitle { get; }

    /// <summary>Container for the page's content, below the header.</summary>
    protected Panel Body => _body;

    /// <summary>Container for the page's actions, right-aligned in the header.</summary>
    protected Panel Header => _header;

    /// <summary>
    /// Called by the shell when the page is shown. Split positions are applied here rather than
    /// in a constructor or OnHandleCreated: only once the page is actually visible does the
    /// container have its final width, and a distance set before that is scaled away by the
    /// layout pass that follows.
    /// </summary>
    public void Activate()
    {
        ApplyPendingSplits();
        OnActivated();
    }

    /// <summary>Registers a split whose position should be set the first time the page shows.</summary>
    protected void PositionSplitOnFirstShow(SplitContainer split, Func<int, int> distanceForWidth) =>
        _pendingSplits.Add((split, distanceForWidth));

    private void ApplyPendingSplits()
    {
        for (var i = _pendingSplits.Count - 1; i >= 0; i--)
        {
            var (split, distanceForWidth) = _pendingSplits[i];
            if (SplitLayout.TryApply(split, distanceForWidth))
                _pendingSplits.RemoveAt(i);
        }
    }

    /// <summary>Called whenever the page becomes visible in the shell.</summary>
    public virtual void OnActivated()
    {
    }

    protected virtual void ApplyTheme() => ApplyBaseColors();

    private void ApplyBaseColors()
    {
        BackColor = AppTheme.Current.Bg;
        _body.BackColor = AppTheme.Current.Bg;
        _header.BackColor = AppTheme.Current.Bg;
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

        ApplyTheme();
        Invalidate(true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            AppTheme.Changed -= OnThemeChanged;

        base.Dispose(disposing);
    }

    private sealed class HeaderStrip(PageBase page) : Panel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            var g = e.Graphics;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var palette = AppTheme.Current;

            TextRenderer.DrawText(
                g, page.Title, AppTheme.TitleFont,
                new Point(Gutter, 12), palette.Text, TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(
                g, page.Subtitle, AppTheme.UiFontSmall,
                new Point(Gutter, 33), palette.TextMuted, TextFormatFlags.NoPrefix);

            using var pen = new Pen(palette.Border);
            g.DrawLine(pen, 0, Height - 1, Width, Height - 1);
        }
    }
}
