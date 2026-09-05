using System.Net;
using DiskSpace.Core.Updates;

namespace DiskSpace.Core.Tests;

public sealed class GitHubUpdateCheckerTests
{
    private static GitHubUpdateChecker Checker(HttpStatusCode status, string body) =>
        new(new HttpClient(new StubHandler(status, body)));

    [Fact]
    public async Task Reports_a_newer_release_with_its_installer_asset()
    {
        var checker = Checker(HttpStatusCode.OK, """
            {
              "tag_name": "v0.3.0",
              "html_url": "https://github.com/CuplexUser/DiskSpace/releases/tag/v0.3.0",
              "draft": false,
              "prerelease": false,
              "assets": [
                { "name": "DiskSpace-0.3.0-win-x64-setup.exe",
                  "browser_download_url": "https://example.com/setup.exe" }
              ]
            }
            """);

        var result = await checker.CheckAsync("0.2.0");

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.3.0", result.Update!.Version);
        Assert.Equal("https://example.com/setup.exe", result.Update.DownloadUrl);
        Assert.False(result.Update.Prerelease);
    }

    [Fact]
    public async Task Reports_up_to_date_for_the_same_version()
    {
        var checker = Checker(HttpStatusCode.OK, """
            { "tag_name": "v0.2.0", "draft": false, "prerelease": false, "assets": [] }
            """);

        var result = await checker.CheckAsync("0.2.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Update);
    }

    [Fact]
    public async Task Reports_up_to_date_for_an_older_release()
    {
        var checker = Checker(HttpStatusCode.OK, """
            { "tag_name": "v0.1.0", "draft": false, "prerelease": false, "assets": [] }
            """);

        var result = await checker.CheckAsync("0.2.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Ignores_a_draft_release()
    {
        var checker = Checker(HttpStatusCode.OK, """
            { "tag_name": "v9.9.9", "draft": true, "prerelease": false, "assets": [] }
            """);

        var result = await checker.CheckAsync("0.2.0");

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
    }

    [Fact]
    public async Task Fails_on_a_non_success_status()
    {
        var checker = Checker(HttpStatusCode.NotFound, "");

        var result = await checker.CheckAsync("0.2.0");

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Fails_on_a_malformed_response_instead_of_throwing()
    {
        var checker = Checker(HttpStatusCode.OK, "not json");

        var result = await checker.CheckAsync("0.2.0");

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.3.0", "1.2.9", 1)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("2.0.0-beta.1", "2.0.0", 0)]
    public void SemanticVersion_orders_by_the_numeric_triple(string a, string b, int expectedSign)
    {
        Assert.True(SemanticVersion.TryParse(a, out var versionA));
        Assert.True(SemanticVersion.TryParse(b, out var versionB));

        Assert.Equal(expectedSign, Math.Sign(versionA.CompareTo(versionB)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    public void SemanticVersion_rejects_unparsable_text(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out _));
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}
