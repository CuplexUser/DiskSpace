using DiskSpace.App.Theme;

namespace DiskSpace.App.Controls;

/// <summary>
/// A thin progress bar for long operations.
///
/// Custom-painted like everything else here: the native <see cref="ProgressBar"/> ignores the
/// palette, so it would be the one piece of Windows blue in a dark window, and its animation
/// runs on a schedule of its own that has nothing to do with the work being done.
///
/// It starts indeterminate and switches to determinate on the first real count, because the
/// gap between a click and the first unit of work is exactly when someone needs to be told
/// that something is happening.
/// </summary>
public sealed class ProgressStrip : ThemedControl
{
    private const int MarqueeStep = 5;

    private readonly System.Windows.Forms.Timer _marquee = new() { Interval = 33 };
    private int _offset;
    private int _completed;
    private int _total;
    private bool _indeterminate = true;

    public ProgressStrip()
    {
        Height = 4;
        _marquee.Tick += (_, _) =>
        {
            _offset = (_offset + MarqueeStep) % Math.Max(1, Width + MarqueeWidth);
            Invalidate();
        };
    }

    private int MarqueeWidth => Math.Max(60, Width / 5);

    /// <summary>Fraction complete, or null while indeterminate.</summary>
    public double? Fraction =>
        _indeterminate || _total <= 0 ? null : Math.Clamp((double)_completed / _total, 0, 1);

    /// <summary>Starts the sliding animation, for work whose size is not known yet.</summary>
    public void Start()
    {
        _indeterminate = true;
        _completed = 0;
        _total = 0;
        _offset = 0;
        Visible = true;
        _marquee.Start();
        Invalidate();
    }

    /// <summary>Switches to a determinate bar and moves it. Safe to call on every report.</summary>
    public void Report(int completed, int total)
    {
        if (total <= 0)
            return;

        if (_indeterminate)
        {
            _indeterminate = false;
            _marquee.Stop();
        }

        _completed = Math.Clamp(completed, 0, total);
        _total = total;
        Invalidate();
    }

    public void Stop()
    {
        _marquee.Stop();
        _indeterminate = true;
        _completed = 0;
        _total = 0;
        Visible = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var palette = Palette;

        using (var track = new SolidBrush(palette.BarTrack))
            g.FillRectangle(track, ClientRectangle);

        using var fill = new SolidBrush(palette.Accent);

        if (_indeterminate)
        {
            // The pill slides in from the left edge and off the right, so the strip is never
            // completely empty for long enough to read as "stopped".
            var x = _offset - MarqueeWidth;
            g.FillRectangle(fill, x, 0, MarqueeWidth, Height);
            return;
        }

        var width = (int)Math.Round(Width * (Fraction ?? 0));
        if (width > 0)
            g.FillRectangle(fill, 0, 0, width, Height);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _marquee.Dispose();

        base.Dispose(disposing);
    }
}
