namespace DiskSpace.App.Controls;

/// <summary>
/// Positions a <see cref="SplitContainer"/> once it has a usable width.
///
/// Setting <see cref="SplitContainer.SplitterDistance"/> from a constructor or from
/// <c>OnHandleCreated</c> does not survive: the container is still at its design width, so the
/// value is clamped, and the layout pass that follows redistributes it proportionally. The
/// position has to be applied when the control is genuinely sized.
/// </summary>
internal static class SplitLayout
{
    /// <summary>Returns true when the position was applied and need not be retried.</summary>
    public static bool TryApply(SplitContainer split, Func<int, int> distanceForWidth)
    {
        if (split.IsDisposed)
            return true;

        var span = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
        if (span < 200)
            return false;

        var maximum = span - split.Panel2MinSize - split.SplitterWidth;
        if (maximum <= split.Panel1MinSize)
            return false;

        var distance = Math.Clamp(distanceForWidth(span), split.Panel1MinSize, maximum);

        try
        {
            split.SplitterDistance = distance;
        }
        catch (InvalidOperationException)
        {
            return false; // Still too early; try again next time the page is shown.
        }

        return true;
    }
}
