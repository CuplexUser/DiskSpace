using DiskSpace.App.Theme;

namespace DiskSpace.App.Controls;

/// <summary>
/// A context menu that follows the palette.
///
/// The stock renderer paints a light menu whatever the window around it looks like, so the
/// colors come from a table that reads the current palette on every paint. That is also what
/// makes a live theme switch work without rebuilding the menu.
/// </summary>
public sealed class ThemedContextMenu : ContextMenuStrip
{
    public ThemedContextMenu()
    {
        // No image column: nothing in these menus has an icon, and the margin only leaves a
        // stripe of a second background color down the side.
        ShowImageMargin = false;
        Renderer = new ThemedRenderer();
        Font = AppTheme.UiFont;
    }

    /// <summary>Adds an item and returns it, so callers can keep a handle for enabling.</summary>
    public ToolStripMenuItem Add(string text, Action onClick, Keys shortcut = Keys.None)
    {
        var item = new ToolStripMenuItem(text, null, (_, _) => onClick());

        if (shortcut != Keys.None)
        {
            // Displayed only: the list view owns the actual key handling, because a shortcut
            // on a context menu item fires only while the menu is open.
            item.ShortcutKeyDisplayString = new KeysConverter().ConvertToString(shortcut);
        }

        Items.Add(item);
        return item;
    }

    public void AddSeparator() => Items.Add(new ToolStripSeparator());

    private sealed class ThemedColors : ProfessionalColorTable
    {
        private static Palette Palette => AppTheme.Current;

        public override Color ToolStripDropDownBackground => Palette.SurfaceAlt;
        public override Color MenuBorder => Palette.BorderStrong;
        public override Color MenuItemBorder => Palette.BorderStrong;
        public override Color MenuItemSelected => Palette.SurfaceHover;
        public override Color MenuItemSelectedGradientBegin => Palette.SurfaceHover;
        public override Color MenuItemSelectedGradientEnd => Palette.SurfaceHover;
        public override Color MenuItemPressedGradientBegin => Palette.SurfaceHover;
        public override Color MenuItemPressedGradientEnd => Palette.SurfaceHover;
        public override Color ImageMarginGradientBegin => Palette.SurfaceAlt;
        public override Color ImageMarginGradientMiddle => Palette.SurfaceAlt;
        public override Color ImageMarginGradientEnd => Palette.SurfaceAlt;
        public override Color SeparatorDark => Palette.Border;
        public override Color SeparatorLight => Palette.Border;
    }

    private sealed class ThemedRenderer() : ToolStripProfessionalRenderer(new ThemedColors())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item?.Enabled == true
                ? AppTheme.Current.Text
                : AppTheme.Current.TextFaint;

            base.OnRenderItemText(e);
        }
    }
}
