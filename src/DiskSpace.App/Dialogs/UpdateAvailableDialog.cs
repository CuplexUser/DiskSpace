using System.Diagnostics;
using DiskSpace.App.Controls;
using DiskSpace.App.Platform;
using DiskSpace.App.Theme;
using DiskSpace.Core.Updates;

namespace DiskSpace.App.Dialogs;

/// <summary>
/// Tells the user a newer release exists. DiskSpace never replaces itself while running, so
/// every path out of here ends at a browser tab, not an in-place update: skip the version,
/// come back later, or go download it.
/// </summary>
public sealed class UpdateAvailableDialog : Form
{
    private readonly UpdateInfo _update;
    private readonly AccentButton _downloadButton = new();
    private readonly AccentButton _skipButton = new();
    private readonly AccentButton _laterButton = new();
    private readonly Label _message = new();
    private readonly Label _note = new();

    public UpdateAvailableDialog(string currentVersion, UpdateInfo update)
    {
        _update = update;

        Text = "Update available";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = true;
        AppIcon.Apply(this);
        ClientSize = new Size(440, 190);
        Font = AppTheme.UiFont;
        DoubleBuffered = true;

        BuildLayout(currentVersion);
        ApplyTheme();
    }

    private void BuildLayout(string currentVersion)
    {
        _message.AutoSize = false;
        _message.Bounds = new Rectangle(20, 20, ClientSize.Width - 40, 30);
        _message.Font = AppTheme.HeadingFont;
        _message.Text = $"DiskSpace {_update.Version} is available";

        _note.AutoSize = false;
        _note.Bounds = new Rectangle(20, 58, ClientSize.Width - 40, 60);
        _note.Font = AppTheme.UiFont;
        _note.Text = _update.Prerelease
            ? $"You have {currentVersion}. This is a pre-release."
            : $"You have {currentVersion}.";

        const int ButtonHeight = 30;
        const int ButtonTop = 140;
        const int Margin = 20;
        const int Gap = 10;
        const int SkipWidth = 120;
        const int LaterWidth = 120;
        const int DownloadWidth = 110;

        var downloadLeft = ClientSize.Width - Margin - DownloadWidth;
        var laterLeft = downloadLeft - Gap - LaterWidth;

        _skipButton.Text = "Skip this version";
        _skipButton.Kind = ButtonKind.Secondary;
        _skipButton.Bounds = new Rectangle(Margin, ButtonTop, SkipWidth, ButtonHeight);
        _skipButton.Click += (_, _) => { DialogResult = DialogResult.Ignore; Close(); };

        _laterButton.Text = "Remind me later";
        _laterButton.Kind = ButtonKind.Secondary;
        _laterButton.Bounds = new Rectangle(laterLeft, ButtonTop, LaterWidth, ButtonHeight);
        _laterButton.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        _downloadButton.Text = "Download";
        _downloadButton.Kind = ButtonKind.Primary;
        _downloadButton.Bounds = new Rectangle(downloadLeft, ButtonTop, DownloadWidth, ButtonHeight);
        _downloadButton.Click += (_, _) => OpenDownload();

        Controls.AddRange([_message, _note, _skipButton, _laterButton, _downloadButton]);
    }

    private void OpenDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_update.DownloadUrl ?? _update.ReleaseUrl)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Best-effort: the release page is still reachable from GitHub if the browser
            // launch itself fails for some local reason.
        }

        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void ApplyTheme()
    {
        var palette = AppTheme.Current;
        BackColor = palette.Bg;
        _message.ForeColor = palette.Text;
        _note.ForeColor = palette.TextMuted;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyTitleBarTheme(Handle, AppTheme.Current.IsDark);
    }
}
