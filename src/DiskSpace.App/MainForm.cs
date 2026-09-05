using DiskSpace.App.Controls;
using DiskSpace.App.Pages;
using DiskSpace.App.Platform;
using DiskSpace.App.Theme;
using DiskSpace.App.Updates;
using DiskSpace.Core.Caching;
using DiskSpace.Core.Quarantine;
using DiskSpace.Core.Settings;

namespace DiskSpace.App;

public sealed class MainForm : Form
{
    private readonly NavRail _nav = new();
    private readonly Panel _content = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, Func<PageBase>> _factories = [];
    private readonly Dictionary<string, PageBase> _pages = [];

    // One store for the whole app, so a settings change is visible to the scan page
    // immediately and both pages agree on what is staged.
    private readonly QuarantineStore _quarantine = new();

    // Likewise one cache and one settings object: a preference written on the Settings page has
    // to be the one the Explorer page reads on its next scan.
    private readonly TreeCache _cache = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly AppUpdateManager _updates;

    public MainForm()
    {
        _updates = new AppUpdateManager(_settings);

        Text = $"DiskSpace {AppVersion.Current}";
        AppIcon.Apply(this);
        MinimumSize = new Size(1040, 660);
        Size = new Size(1320, 820);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Font = AppTheme.UiFont;

        Controls.Add(_content);
        Controls.Add(_nav);

        // Before RegisterPages, which builds the opening page: a page constructed against
        // default settings would show them, and then write them back on its first change.
        ApplySettings();

        // Subscribe before registering: RegisterPages selects the first item, and that
        // selection is what puts the opening page on screen.
        _nav.SelectionChanged += (_, item) => ShowPage(item.Key);
        AppTheme.Changed += (_, _) => ApplyTheme();

        RegisterPages();
        ApplyTheme();

        PurgeExpiredQuarantine();
        SweepScanCache();

        Shown += (_, _) => _ = _updates.CheckSilentlyAsync(this);
    }

    private void ApplySettings()
    {
        _settings.ApplyTo(_quarantine.Options);

        AppTheme.Preference = _settings.Theme switch
        {
            nameof(ThemePreference.Dark) => ThemePreference.Dark,
            nameof(ThemePreference.Light) => ThemePreference.Light,
            _ => ThemePreference.FollowSystem,
        };
    }

    private void RegisterPages()
    {
        Register("scan", "Scan", Glyphs.Scan, () =>
        {
            var page = new ScanPage(_quarantine);
            page.CleanupCompleted += (_, _) => UpdateQuarantineBadge();
            return page;
        });

        Register("programs", "Programs", Glyphs.Programs, () => new ProgramsPage());

        Register("explorer", "Explorer", Glyphs.Folder, () => new ExplorerPage(_cache, _settings));

        Register("quarantine", "Quarantine", Glyphs.Quarantine, () =>
        {
            var page = new QuarantinePage(_quarantine);
            page.Changed += (_, _) => UpdateQuarantineBadge();
            return page;
        });

        Register("log", "Log", Glyphs.History, () => new LogPage());
        Register("settings", "Settings", Glyphs.Settings, () =>
            new SettingsPage(_quarantine, _settings, _cache, _updates));

        _nav.SelectFirst();
    }

    private void Register(string key, string label, string glyph, Func<PageBase> factory)
    {
        _factories[key] = factory;
        _nav.AddItem(new NavItem(key, label, glyph));
    }

    private void ShowPage(string key)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            if (!_factories.TryGetValue(key, out var factory))
                return;

            page = factory();
            _pages[key] = page;
            _content.Controls.Add(page);
        }

        page.BringToFront();
        page.Visible = true;

        foreach (var other in _pages.Values.Where(p => p != page))
            other.Visible = false;

        page.Activate();
    }

    /// <summary>
    /// Expired items are purged at startup rather than on a timer: this is the moment the user
    /// is present to see the badge change, and a background purge of permanently-deleted data
    /// should not happen while nobody is looking.
    /// </summary>
    private void PurgeExpiredQuarantine()
    {
        try
        {
            _quarantine.PurgeExpired();
        }
        catch (Exception)
        {
            // Never let quarantine housekeeping stop the app from opening.
        }

        UpdateQuarantineBadge();
    }

    /// <summary>
    /// Drops expired and over-cap cached trees. Off the UI thread because it deletes files, and
    /// nothing on screen is waiting for it.
    /// </summary>
    private void SweepScanCache() => Task.Run(() =>
    {
        try
        {
            _cache.Sweep();
        }
        catch (Exception)
        {
            // A cache is disposable; failing to tidy it is not worth reporting.
        }
    });

    private void UpdateQuarantineBadge()
    {
        try
        {
            _nav.SetBadge("quarantine", _quarantine.List().Count);
        }
        catch (Exception)
        {
            _nav.SetBadge("quarantine", 0);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowChrome();
    }

    private void ApplyWindowChrome()
    {
        if (!IsHandleCreated)
            return;

        var palette = AppTheme.Current;
        NativeMethods.ApplyTitleBarTheme(Handle, palette.IsDark);
        NativeMethods.ApplyBorderColor(Handle, palette.Border);
    }

    private void ApplyTheme()
    {
        var palette = AppTheme.Current;
        BackColor = palette.Bg;
        _content.BackColor = palette.Bg;
        ApplyWindowChrome();
        Invalidate(true);
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        // Windows broadcasts this when the user flips the light/dark app setting, among many
        // other changes; AppTheme.Refresh is a no-op unless the resolved palette actually moved.
        if (m.Msg == NativeMethods.WmSettingChange)
            AppTheme.Refresh();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _updates.Dispose();

        base.Dispose(disposing);
    }
}
