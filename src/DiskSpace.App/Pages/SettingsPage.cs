using System.IO.Compression;
using DiskSpace.App.Controls;
using DiskSpace.App.Theme;
using DiskSpace.Core.Caching;
using DiskSpace.Core.Model;
using DiskSpace.Core.Quarantine;
using DiskSpace.Core.Settings;

namespace DiskSpace.App.Pages;

/// <summary>
/// Settings that change what the tool does, not how it looks — plus the theme override.
/// Everything here is deliberately explicit about its cost.
/// </summary>
public sealed class SettingsPage : PageBase
{
    private readonly QuarantineStore _store;
    private readonly AppSettings _settings;
    private readonly TreeCache _cache;
    private readonly ComboBox _themeBox = new();
    private readonly ComboBox _modeBox = new();
    private readonly ComboBox _compressionBox = new();
    private readonly NumericUpDown _retention = new();
    private readonly TextBox _location = new();
    private readonly AccentButton _browse = new();
    private readonly Label _locationNote = new();
    private readonly CheckBox _useCache = new();
    private readonly CheckBox _trustUnchanged = new();
    private readonly AccentButton _clearCache = new();
    private readonly Label _cacheNote = new();

    public SettingsPage(QuarantineStore store, AppSettings settings, TreeCache cache)
        : base("Settings", "How scanning and quarantine behave, and which theme the window follows.")
    {
        _store = store;
        _settings = settings;
        _cache = cache;

        BuildControls();
        LoadValues();
        ApplyTheme();
    }

    private int _row;

    /// <summary>
    /// Set while LoadValues populates the controls. Each assignment raises a change event, and
    /// without this the handler writes the settings back from a half-populated form — which
    /// silently overwrote the retention value with whatever the spinner happened to hold.
    /// </summary>
    private bool _loading;

    private void BuildControls()
    {
        _row = 20;

        AddLabel("Appearance", heading: true);
        _themeBox.Items.AddRange(["Follow Windows", "Always dark", "Always light"]);
        AddField("Theme", _themeBox, "The window re-themes immediately when Windows changes.");

        AddLabel("Scanning", heading: true);

        _useCache.Text = "Show the last measurement while re-measuring";
        _useCache.AutoSize = false;
        _useCache.Width = 430;
        _useCache.Height = 22;
        AddField(
            "Scan cache",
            _useCache,
            "A folder scanned before appears at once, marked as an estimate, and settles into "
            + "measured numbers as the scan confirms them.");

        _trustUnchanged.Text = "Trust folders whose timestamp has not changed";
        _trustUnchanged.AutoSize = false;
        _trustUnchanged.Width = 430;
        _trustUnchanged.Height = 22;
        AddField(
            "Fast re-scan",
            _trustUnchanged,
            "Faster, and it will report a file that grew in place at its old size. Windows does "
            + "not move a folder's timestamp when a file inside it is written to.");

        _clearCache.Text = "Clear cache";
        _clearCache.Kind = ButtonKind.Secondary;
        _clearCache.Width = 108;
        _clearCache.Location = new Point(Gutter + 200, _row);
        _clearCache.Click += (_, _) =>
        {
            _cache.Clear();
            UpdateCacheNote();
        };

        _cacheNote.AutoSize = false;
        _cacheNote.Bounds = new Rectangle(Gutter + 320, _row + 4, 460, 22);
        _cacheNote.Font = AppTheme.UiFontSmall;
        _cacheNote.Tag = "note";

        Body.Controls.AddRange([_clearCache, _cacheNote]);
        _row += 44;

        AddLabel("Quarantine", heading: true);

        _modeBox.Items.AddRange(
        [
            "Archive to another volume (frees this disk now)",
            "Move aside on the same volume (instant, frees nothing until purge)",
        ]);
        AddField(
            "Method",
            _modeBox,
            "Packs the folder into one file, so 100,000 files cost a single sequential write.");

        _retention.Minimum = 1;
        _retention.Maximum = 365;
        _retention.Width = 80;
        AddField("Retention (days)", _retention, "Quarantined items are purged automatically after this.");

        _compressionBox.Items.AddRange(["Fastest (recommended)", "Smallest", "None"]);
        AddField(
            "Compression",
            _compressionBox,
            "Leftover app data compresses well, and writing fewer bytes usually beats the CPU cost.");

        _location.Width = 420;
        AddField(
            "Location",
            _location,
            "Leave empty to pick the roomiest volume that is not the source.");

        _browse.Text = "Browse…";
        _browse.Width = 88;
        _browse.Location = new Point(Gutter + 200 + 430, _location.Top - 2);
        _browse.Click += (_, _) => BrowseForLocation();
        Body.Controls.Add(_browse);

        _locationNote.AutoSize = false;
        _locationNote.Bounds = new Rectangle(Gutter + 200, _row, 700, 34);
        _locationNote.Font = AppTheme.UiFontSmall;
        Body.Controls.Add(_locationNote);

        _themeBox.SelectedIndexChanged += (_, _) => ApplyThemePreference();
        _useCache.CheckedChanged += (_, _) => Save();
        _trustUnchanged.CheckedChanged += (_, _) => Save();
        _modeBox.SelectedIndexChanged += (_, _) => Save();
        _compressionBox.SelectedIndexChanged += (_, _) => Save();
        _retention.ValueChanged += (_, _) => Save();
        _location.TextChanged += (_, _) => Save();
    }

    private void AddLabel(string text, bool heading)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = false,
            Bounds = new Rectangle(Gutter, _row, 600, 26),
            Font = heading ? AppTheme.TitleFont : AppTheme.UiFont,
        };

        Body.Controls.Add(label);
        _row += heading ? 34 : 26;
    }

    private void AddField(string label, Control control, string note)
    {
        var caption = new Label
        {
            Text = label,
            AutoSize = false,
            Bounds = new Rectangle(Gutter, _row + 3, 190, 22),
            Font = AppTheme.UiFont,
        };

        control.Location = new Point(Gutter + 200, _row);
        if (control is ComboBox combo)
        {
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.FlatStyle = FlatStyle.Flat;
            combo.Width = Math.Max(combo.Width, 430);
        }

        control.Font = AppTheme.UiFont;

        var noteLabel = new Label
        {
            Text = note,
            AutoSize = false,
            Bounds = new Rectangle(Gutter + 200, _row + 26, 700, 34),
            Font = AppTheme.UiFontSmall,
            Tag = "note",
        };

        Body.Controls.AddRange([caption, control, noteLabel]);
        _row += 68;
    }

    private void LoadValues()
    {
        _loading = true;

        try
        {
            LoadValuesCore();
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadValuesCore()
    {
        _themeBox.SelectedIndex = AppTheme.Preference switch
        {
            ThemePreference.Dark => 1,
            ThemePreference.Light => 2,
            _ => 0,
        };

        _useCache.Checked = _settings.UseScanCache;
        _trustUnchanged.Checked = _settings.TrustUnchangedFolders;
        _trustUnchanged.Enabled = _settings.UseScanCache;
        UpdateCacheNote();

        var options = _store.Options;
        _modeBox.SelectedIndex = options.Mode == QuarantineMode.MoveOnSameVolume ? 1 : 0;
        _retention.Value = Math.Clamp((int)options.Retention.TotalDays, 1, 365);
        _compressionBox.SelectedIndex = options.Compression switch
        {
            CompressionLevel.SmallestSize => 1,
            CompressionLevel.NoCompression => 2,
            _ => 0,
        };
        _location.Text = options.Location ?? string.Empty;

        UpdateLocationNote();
    }

    private void ApplyThemePreference()
    {
        AppTheme.Preference = _themeBox.SelectedIndex switch
        {
            1 => ThemePreference.Dark,
            2 => ThemePreference.Light,
            _ => ThemePreference.FollowSystem,
        };

        Save();
    }

    private void UpdateCacheNote()
    {
        var bytes = _cache.TotalBytes();

        _cacheNote.Text = bytes == 0
            ? "Nothing cached."
            : $"{ByteSize.Format(bytes)} of remembered measurements.";
    }

    /// <summary>
    /// Writes every setting through to disk. Until this existed, everything on this page reset
    /// on the next launch, which made a preference something the user had to set again each time.
    /// </summary>
    private void Save()
    {
        if (_loading)
            return;

        _settings.Theme = AppTheme.Preference.ToString();
        _settings.UseScanCache = _useCache.Checked;
        _settings.TrustUnchangedFolders = _trustUnchanged.Checked;
        _trustUnchanged.Enabled = _useCache.Checked;

        var options = _store.Options;

        options.Mode = _modeBox.SelectedIndex == 1
            ? QuarantineMode.MoveOnSameVolume
            : QuarantineMode.ArchiveToOtherVolume;

        options.Retention = TimeSpan.FromDays((double)_retention.Value);

        options.Compression = _compressionBox.SelectedIndex switch
        {
            1 => CompressionLevel.SmallestSize,
            2 => CompressionLevel.NoCompression,
            _ => CompressionLevel.Fastest,
        };

        options.Location = string.IsNullOrWhiteSpace(_location.Text) ? null : _location.Text.Trim();

        _settings.CaptureFrom(options);
        _settings.Save();

        UpdateLocationNote();
    }

    private void UpdateLocationNote()
    {
        var palette = AppTheme.Current;

        if (_store.Options.Location is { } configured)
        {
            var root = Path.GetPathRoot(configured);
            var profileRoot = Path.GetPathRoot(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            // Worth saying plainly: staging on the source volume reclaims nothing until purge.
            if (string.Equals(root, profileRoot, StringComparison.OrdinalIgnoreCase))
            {
                _locationNote.Text =
                    $"This location is on {root}, the same volume as your profile, so archiving "
                    + "there will not free space until items are purged.";
                _locationNote.ForeColor = palette.RiskReview;
                return;
            }

            _locationNote.Text = $"Archives are written to {configured}.";
            _locationNote.ForeColor = palette.TextMuted;
            return;
        }

        var automatic = QuarantineOptions.ChooseLocation(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        if (automatic is null)
        {
            _locationNote.Text =
                "No second fixed volume was found, so quarantined items are moved aside on their "
                + "own volume instead. That is instant, but frees nothing until purge.";
            _locationNote.ForeColor = palette.RiskReview;
            return;
        }

        var drive = new DriveInfo(Path.GetPathRoot(automatic)!);
        _locationNote.Text =
            $"Automatic: {automatic} ({ByteSize.Format(drive.AvailableFreeSpace)} free).";
        _locationNote.ForeColor = palette.TextMuted;
    }

    private void BrowseForLocation()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where quarantined items are stored",
            UseDescriptionForTitle = true,
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
            _location.Text = dialog.SelectedPath;
    }

    protected override void ApplyTheme()
    {
        base.ApplyTheme();
        var palette = AppTheme.Current;

        foreach (Control control in Body.Controls)
        {
            control.BackColor = palette.Bg;
            control.ForeColor = control.Tag as string == "note" ? palette.TextFaint : palette.Text;
        }

        foreach (var box in new Control[] { _themeBox, _modeBox, _compressionBox, _location, _retention })
        {
            box.BackColor = palette.SurfaceAlt;
            box.ForeColor = palette.Text;
        }

        _cacheNote.ForeColor = palette.TextFaint;

        UpdateLocationNote();
        UpdateCacheNote();
    }
}
