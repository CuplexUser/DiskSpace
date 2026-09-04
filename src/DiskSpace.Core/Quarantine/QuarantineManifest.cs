using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskSpace.Core.Quarantine;

/// <summary>
/// What a quarantined folder was, recorded beside its archive so a restore does not depend on
/// this application still existing or on anything held in memory.
/// </summary>
public sealed record QuarantineManifest
{
    public required string Id { get; init; }
    public required string OriginalPath { get; init; }
    public required string ArchivePath { get; init; }
    public required long OriginalSize { get; init; }
    public required long FileCount { get; init; }
    public required DateTimeOffset QuarantinedAt { get; init; }
    public required DateTimeOffset PurgeAfter { get; init; }
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }

    /// <summary>Set when the folder was moved aside on the same volume instead of archived.</summary>
    public string? MovedToPath { get; init; }

    public bool IsArchive => MovedToPath is null;

    public bool IsDue(DateTimeOffset now) => now >= PurgeAfter;

    public string ManifestPath => Path.ChangeExtension(ArchivePath, ".manifest.json");

    public void Save()
    {
        var json = JsonSerializer.Serialize(this, QuarantineJson.Default.QuarantineManifest);
        File.WriteAllText(ManifestPath, json);
    }

    public static QuarantineManifest? Load(string manifestPath)
    {
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(manifestPath), QuarantineJson.Default.QuarantineManifest);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// Source-generated serialization rather than reflection.
///
/// A manifest is the only thing that makes a restore possible, and reflection-based
/// serialization throws outright under trimming or AOT — which would mean quarantining
/// something and then being unable to record where it came from.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(QuarantineManifest))]
internal sealed partial class QuarantineJson : JsonSerializerContext;

public readonly record struct QuarantineProgress(
    int FilesDone, int FilesTotal, long BytesDone, string CurrentPath);
