using System.Text.Json.Serialization;

namespace DiskSpace.Core.Updates;

/// <summary>The handful of fields used from GitHub's "latest release" response.</summary>
internal sealed class GitHubRelease
{
    public string TagName { get; set; } = "";

    public string? HtmlUrl { get; set; }

    public bool Draft { get; set; }

    public bool Prerelease { get; set; }

    public List<GitHubReleaseAsset> Assets { get; set; } = [];
}

internal sealed class GitHubReleaseAsset
{
    public string Name { get; set; } = "";

    public string BrowserDownloadUrl { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class GitHubJson : JsonSerializerContext;
