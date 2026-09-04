using Microsoft.Win32;

namespace DiskSpace.App.Theme;

public enum ThemePreference
{
    FollowSystem,
    Dark,
    Light,
}

/// <summary>
/// Holds the active palette and the fonts. Controls subscribe to <see cref="Changed"/> and
/// invalidate; nothing caches a colour past a repaint.
/// </summary>
public static class AppTheme
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static ThemePreference _preference = ThemePreference.FollowSystem;

    static AppTheme()
    {
        Current = Resolve();
        UiFont = CreateUiFont(9f, FontStyle.Regular);
        UiFontBold = CreateUiFont(9f, FontStyle.Bold);
        UiFontSmall = CreateUiFont(8f, FontStyle.Regular);
        HeadingFont = CreateUiFont(13f, FontStyle.Regular);
        TitleFont = CreateUiFont(10.5f, FontStyle.Bold);
        MonoFont = CreateMonoFont(8.5f);
    }

    public static Palette Current { get; private set; }

    public static Font UiFont { get; }
    public static Font UiFontBold { get; }
    public static Font UiFontSmall { get; }
    public static Font HeadingFont { get; }
    public static Font TitleFont { get; }
    public static Font MonoFont { get; }

    /// <summary>Raised after <see cref="Current"/> changes. Handlers run on the UI thread.</summary>
    public static event EventHandler? Changed;

    public static ThemePreference Preference
    {
        get => _preference;
        set
        {
            if (_preference == value)
                return;

            _preference = value;
            Refresh();
        }
    }

    /// <summary>
    /// Re-reads the system setting and swaps the palette if it moved. Called at startup and
    /// from the main form's WM_SETTINGCHANGE handler, so the app re-themes live.
    /// </summary>
    public static void Refresh()
    {
        var resolved = Resolve();
        if (resolved == Current)
            return;

        Current = resolved;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Palette Resolve() => _preference switch
    {
        ThemePreference.Dark => Palette.Dark,
        ThemePreference.Light => Palette.Light,
        _ => SystemUsesLightTheme() ? Palette.Light : Palette.Dark,
    };

    private static bool SystemUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // Absent value means an older build that predates the setting: assume light.
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static Font CreateUiFont(float size, FontStyle style) =>
        FontResolver.Ui(size, style);

    private static Font CreateMonoFont(float size) => FontResolver.Mono(size);

}
