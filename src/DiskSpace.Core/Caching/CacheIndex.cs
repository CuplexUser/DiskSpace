using System.Text.Json.Serialization;

namespace DiskSpace.Core.Caching;

/// <summary>One cached tree, as recorded in the index.</summary>
public sealed record CacheEntry
{
    /// <summary>The canonical root path this tree measured.</summary>
    public required string RootPath { get; init; }

    public required string FileName { get; init; }

    public required DateTimeOffset WrittenAt { get; init; }

    /// <summary>Drives eviction: the least recently opened tree is the first to go.</summary>
    public required DateTimeOffset LastUsedAt { get; init; }

    public required long FileBytes { get; init; }

    public required int NodeCount { get; init; }

    public required long TotalSize { get; init; }

    public required bool WasComplete { get; init; }
}

/// <summary>
/// The list of cached trees. Small enough to stay JSON in the house style, and worth being able
/// to read by hand, unlike the trees themselves.
/// </summary>
public sealed record CacheIndex
{
    /// <summary>Bumped to discard the whole cache directory rather than migrate it.</summary>
    public required int Version { get; init; }

    public required List<CacheEntry> Entries { get; init; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CacheIndex))]
internal sealed partial class CacheJson : JsonSerializerContext;
