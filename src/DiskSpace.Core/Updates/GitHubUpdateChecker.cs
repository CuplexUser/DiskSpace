using System.Net.Http.Json;

namespace DiskSpace.Core.Updates;

/// <summary>
/// Asks GitHub for the newest release of this project and compares it against the version
/// that is running. GitHub's API requires a User-Agent on every request or it answers 403,
/// which is the one header this otherwise ordinary GET cannot skip.
/// </summary>
public sealed class GitHubUpdateChecker : IDisposable
{
    private const string ReleasesUrl = "https://api.github.com/repos/CuplexUser/DiskSpace/releases/latest";

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public GitHubUpdateChecker(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("DiskSpace-App");
    }

    /// <param name="currentVersion">The running version, e.g. "0.2.0".</param>
    public async Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return UpdateCheckResult.Failed($"GitHub returned {(int)response.StatusCode}.");

            var release = await response.Content
                .ReadFromJsonAsync(GitHubJson.Default.GitHubRelease, cancellationToken)
                .ConfigureAwait(false);

            if (release is null || release.Draft)
                return UpdateCheckResult.UpToDate();

            if (!SemanticVersion.TryParse(release.TagName, out var latest) ||
                !SemanticVersion.TryParse(currentVersion, out var current) ||
                latest.CompareTo(current) <= 0)
            {
                return UpdateCheckResult.UpToDate();
            }

            var installer = release.Assets.FirstOrDefault(
                a => a.Name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase));

            return UpdateCheckResult.Available(new UpdateInfo(
                Version: latest.ToString(),
                ReleaseUrl: release.HtmlUrl ?? "https://github.com/CuplexUser/DiskSpace/releases/latest",
                DownloadUrl: installer?.BrowserDownloadUrl,
                Prerelease: release.Prerelease));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return UpdateCheckResult.Failed(ex.Message);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
            _http.Dispose();
    }
}
