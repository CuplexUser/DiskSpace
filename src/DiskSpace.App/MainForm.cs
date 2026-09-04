using DiskSpace.App.Controls;
using DiskSpace.App.Pages;
using DiskSpace.App.Platform;
using DiskSpace.App.Theme;
using DiskSpace.Core.Quarantine;

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

    public MainForm()
    {
        Text = "DiskSpace";
        MinimumSize = new Size(1040, 660);
        Size = new Size(1320, 820);
        StartPosition = FormStartPosition.CenterScreen;
        DoubleBuffered = true;
        Font = AppTheme.UiFont;

        Controls.Add(_content);
        Controls.Add(_nav);

        // Subscribe before registering: RegisterPages selects the first item, and that
        // selection is what puts the opening page on screen.
        _nav.SelectionChanged += (_, item) => ShowPage(item.Key);
        AppTheme.Changed += (_, _) => ApplyTheme();

        RegisterPages();
        ApplyTheme();

        PurgeExpiredQuarantine();
    }

    private void RegisterPages()
    {
        Register("scan", "Scan", Glyphs.Scan, () =>
        {
            var page = new ScanPage(_quarantine);
            page.CleanupCompleted += (_, _) => UpdateQuarantineBadge();
            return page;
        });

        Register("explorer", "Explorer", Glyphs.Folder, () => new ExplorerPage());

        Register("quarantine", "Quarantine", Glyphs.Quarantine, () =>
        {
            var page = new QuarantinePage(_quarantine);
            page.Changed += (_, _) => UpdateQuarantineBadge();
            return page;
        });

        Register("log", "Log", Glyphs.History, () => new LogPage());
        Register("settings", "Settings", Glyphs.Settings, () => new SettingsPage(_quarantine));

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
}
