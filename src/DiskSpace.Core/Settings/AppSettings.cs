using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiskSpace.Core.Quarantine;

namespace DiskSpace.Core.Settings;

/// <summary>
/// Everything the app remembers between runs.
///
/// Source-generated serialization for the same reason as the audit log and the quarantine
/// manifest: it must not depend on reflection surviving a trimmed or AOT publish. Written
/// indented and with enum names rather than numbers, because a settings file that cannot be
/// read or repaired by hand is a settings file that has to be deleted when it goes wrong.
/// </summary>
public sealed class AppSettings
{
    /// <summary>"FollowSystem", "Dark" or "Light". Resolved by the UI, which owns the palette.</summary>
    public string Theme { get; set; } = "FollowSystem";

    public QuarantineMode QuarantineMode { get; set; } = QuarantineMode.ArchiveToOtherVolume;

    public int QuarantineRetentionDays { get; set; } = 7;

    public CompressionLevel QuarantineCompression { get; set; } = CompressionLevel.Fastest;

    public string? QuarantineLocation { get; set; }

    /// <summary>Render a previously scanned root from the cache before re-measuring it.</summary>
    public bool UseScanCache { get; set; } = true;

    /// <summary>
    /// Adopt a cached folder whose timestamp has not moved instead of listing it again.
    /// Off by default, and deliberately so: see <c>ScanOptions.TrustUnchangedFolders</c> for
    /// what it gets wrong.
    /// </summary>
    public bool TrustUnchangedFolders { get; set; }

    /// <summary>Where the settings file lives. Sibling of the run logs.</summary>
    public static string FilePath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskSpace",
        "settings.json");

    /// <summary>
    /// Reads the settings, falling back to defaults for anything missing or unreadable.
    /// <paramref name="path"/> overrides <see cref="FilePath"/>; tests must pass one so a run
    /// cannot rewrite the settings of whoever is running them.
    /// </summary>
    public static AppSettings Load(string? path = null)
    {
        var file = path ?? FilePath;

        try
        {
            if (!File.Exists(file))
                return new AppSettings();

            return JsonSerializer.Deserialize(File.ReadAllText(file), SettingsJson.Default.AppSettings)
                   ?? new AppSettings();
        }
        catch (Exception)
        {
            // A corrupt settings file costs the user their preferences, not the app's start.
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings. Never throws: a preference is not worth an error dialog.</summary>
    public void Save(string? path = null)
    {
        var file = path ?? FilePath;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);

            // Written aside and moved into place, so a crash halfway through leaves the previous
            // settings intact rather than a half file that will not parse.
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings));
            File.Move(temporary, file, overwrite: true);
        }
        catch (Exception)
        {
            // Read-only profile, roaming glitch, disk full. None of those should stop the app.
        }
    }

    /// <summary>Pushes the stored quarantine preferences onto the store's live options.</summary>
    public void ApplyTo(QuarantineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Mode = QuarantineMode;
        options.Retention = TimeSpan.FromDays(Math.Clamp(QuarantineRetentionDays, 1, 365));
        options.Compression = QuarantineCompression;
        options.Location = string.IsNullOrWhiteSpace(QuarantineLocation) ? null : QuarantineLocation;
    }

    /// <summary>Takes the live quarantine options back, ready to be saved.</summary>
    public void CaptureFrom(QuarantineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        QuarantineMode = options.Mode;
        QuarantineRetentionDays = Math.Clamp((int)options.Retention.TotalDays, 1, 365);
        QuarantineCompression = options.Compression;
        QuarantineLocation = options.Location;
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;
