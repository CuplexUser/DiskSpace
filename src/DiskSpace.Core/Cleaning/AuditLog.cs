using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskSpace.Core.Cleaning;

public sealed record AuditEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public required string Path { get; init; }
    public required long Bytes { get; init; }
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public required string Risk { get; init; }
    public required string Disposal { get; init; }
    public required bool Succeeded { get; init; }
    public string? Error { get; init; }
    public string? HeldBy { get; init; }
}

/// <summary>
/// Source-generated serialization, for the same reason as the quarantine manifest: the log is
/// the only account of what a permanent deletion removed, and it must not depend on reflection
/// surviving a trimmed or AOT publish. Written compactly — one entry per line.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuditEntry))]
internal sealed partial class AuditJson : JsonSerializerContext;

/// <summary>
/// An append-only record of everything the tool removed.
///
/// Since deletion is permanent, this log is the only account of what happened, so it is written
/// as each item is disposed of rather than in a batch at the end — a crash or a power loss
/// halfway through still leaves a complete record of everything that actually went.
///
/// JSONL, one object per line: appendable without rewriting, and readable with any text tool
/// if this application is not around.
/// </summary>
public sealed class AuditLog : IDisposable
{
    private readonly StreamWriter? _writer;

    private AuditLog(StreamWriter? writer, string path)
    {
        _writer = writer;
        FilePath = path;
    }

    public string FilePath { get; }

    /// <summary>Where run logs live. Also the directory the Log page reads.</summary>
    public static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiskSpace",
        "logs");

    public static AuditLog StartRun()
    {
        var path = Path.Combine(
            LogDirectory,
            $"cleanup-{DateTime.Now:yyyy-MM-dd-HHmmss}.jsonl");

        try
        {
            Directory.CreateDirectory(LogDirectory);

            var stream = new FileStream(
                path, FileMode.Append, FileAccess.Write, FileShare.Read);

            // AutoFlush is the point: an entry that is only in a buffer is not a record.
            return new AuditLog(new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true }, path);
        }
        catch (Exception)
        {
            // A log that cannot be opened must not stop a cleanup the user asked for.
            return new AuditLog(null, path);
        }
    }

    public void Write(AuditEntry entry)
    {
        try
        {
            _writer?.WriteLine(JsonSerializer.Serialize(entry, AuditJson.Default.AuditEntry));
        }
        catch (Exception)
        {
            // Never let logging break the operation being logged.
        }
    }

    /// <summary>Past runs, newest first.</summary>
    public static IReadOnlyList<string> ListRuns()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
                return [];

            return
            [
                .. Directory.EnumerateFiles(LogDirectory, "cleanup-*.jsonl")
                    .OrderByDescending(File.GetLastWriteTimeUtc),
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>Reads one run's entries, skipping any line that will not parse.</summary>
    public static IReadOnlyList<AuditEntry> Read(string path)
    {
        var entries = new List<AuditEntry>();

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                try
                {
                    if (JsonSerializer.Deserialize(line, AuditJson.Default.AuditEntry) is { } entry)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A truncated final line is expected if a run was killed mid-write.
                }
            }
        }
        catch (Exception)
        {
            // Unreadable log; report what was parsed so far.
        }

        return entries;
    }

    public void Dispose() => _writer?.Dispose();
}
