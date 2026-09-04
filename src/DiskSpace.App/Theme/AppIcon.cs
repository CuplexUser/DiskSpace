namespace DiskSpace.App.Theme;

/// <summary>
/// The window icon, loaded once and shared by every window.
///
/// It comes from the embedded .ico rather than from the executable, so all sizes are present:
/// Windows picks 16px for the title bar and 32px or larger for the taskbar and Alt+Tab, and an
/// icon extracted from the running process would only have supplied one of them.
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "DiskSpace.App.Assets.DiskSpace.ico";

    public static Icon? Shared { get; } = Load();

    /// <summary>
    /// Gives a window the application icon. Applied to every window, not just the main one:
    /// a dialog carrying the default icon looks like it belongs to some other program, which
    /// is the last impression a confirmation for a permanent deletion should give.
    /// </summary>
    public static void Apply(Form form)
    {
        if (Shared is { } icon)
            form.Icon = icon;
    }

    private static Icon? Load()
    {
        try
        {
            using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream(ResourceName);
            if (stream is not null)
                return new Icon(stream);
        }
        catch (Exception)
        {
            // Fall through to the executable's own icon.
        }

        try
        {
            // Single-file publish included: ProcessPath is the host executable either way.
            return Environment.ProcessPath is { } executable
                ? Icon.ExtractAssociatedIcon(executable)
                : null;
        }
        catch (Exception)
        {
            // A window with the default icon is not worth failing startup over.
            return null;
        }
    }
}
