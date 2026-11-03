// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Status;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Tests.Setup;

/// <summary>
/// The update checker asks GitHub for the build its channel offers. Stable
/// keeps reading /releases/latest, exactly as every install before channels
/// did; beta and nightly read the release list and pick from it.
/// </summary>
[Trait("Category", "Unit")]
public class UpdateCheckerChannelTests
{
    private const string LatestUrl =
        "https://api.github.com/repos/NoMercy-Entertainment/nomercy-media-server/releases/latest";

    private const string ReleaseList = """
        [
          { "tag_name": "v9.0.3", "draft": false, "prerelease": true, "body": "## Install" },
          { "tag_name": "v9.0.2", "draft": false, "prerelease": true, "body": "<!-- nomercy-channel: beta -->\n## Install" },
          { "tag_name": "v9.0.1", "draft": false, "prerelease": false, "body": "<!-- nomercy-channel: stable -->\n## Install" },
          { "tag_name": "v9.0.0", "draft": false, "prerelease": false, "body": null }
        ]
        """;

    private const string Latest = """
        { "tag_name": "v9.0.1", "draft": false, "prerelease": false, "body": "" }
        """;

    /// <summary>
    /// A page of nightlies only, as every dev push stacks in front of the newest
    /// beta until the first page of the release list offers a beta install nothing.
    /// </summary>
    private const string NightlyPage = """
        [
          { "tag_name": "v9.1.3", "draft": false, "prerelease": true, "body": "## Install" },
          { "tag_name": "v9.1.2", "draft": false, "prerelease": true, "body": "## Install" }
        ]
        """;

    /// <summary>
    /// Answers the latest-release URL with <see cref="Latest"/> and the release
    /// list with <see cref="Pages"/>, one page per request, with the <c>Link</c>
    /// header GitHub sends while more pages follow.
    /// </summary>
    private sealed class GithubStub : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        public string[] Pages { get; init; } = [ReleaseList];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string url = request.RequestUri!.ToString();
            Requested.Add(url);

            if (url == LatestUrl)
                return Task.FromResult(Json(Latest));

            int page = PageNumber(url);
            HttpResponseMessage response = Json(Pages[page - 1]);
            if (page < Pages.Length)
                response.Headers.Add(
                    "Link",
                    $"<{ReleaseChannelSelector.MediaServerReleaseListUrl}&page={page + 1}>; rel=\"next\""
                );

            return Task.FromResult(response);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

        private static int PageNumber(string url)
        {
            System.Text.RegularExpressions.Match match = Regex.Match(
                url,
                @"[?&]page=(\d+)",
                RegexOptions.None,
                TimeSpan.FromSeconds(1)
            );
            return match.Success ? int.Parse(match.Groups[1].Value) : 1;
        }
    }

    private static async Task<(UpdateStatus Status, GithubStub Github)> CheckAsync(
        ReleaseChannel channel,
        params string[] pages
    )
    {
        GithubStub github = pages.Length == 0 ? new() : new() { Pages = pages };
        UpdateStatus status = new();
        RuntimeServerSettings settings = new() { UpdateChannel = channel };
        UpdateChecker checker = new(status, settings, new HttpClient(github));

        await checker.IsUpdateAvailableAsync();

        return (status, github);
    }

    [Fact]
    public async Task Stable_ReadsGithubsLatestRelease()
    {
        (UpdateStatus status, GithubStub github) = await CheckAsync(ReleaseChannel.Stable);

        github.Requested.Should().Equal(LatestUrl);
        status.LatestVersion.Should().Be("9.0.1");
    }

    [Fact]
    public async Task Beta_OffersTheNewestBeta()
    {
        (UpdateStatus status, GithubStub github) = await CheckAsync(ReleaseChannel.Beta);

        github.Requested.Should().Equal(ReleaseChannelSelector.MediaServerReleaseListUrl);
        status.LatestVersion.Should().Be("9.0.2");
    }

    [Fact]
    public async Task Beta_PagesPastTheNightlies_UntilItFindsTheNewestBeta()
    {
        (UpdateStatus status, GithubStub github) = await CheckAsync(
            ReleaseChannel.Beta,
            NightlyPage,
            ReleaseList
        );

        status.LatestVersion.Should().Be("9.0.2", "the newest beta sits on the second page");
        github.Requested.Should().HaveCount(2);
    }

    [Fact]
    public async Task Nightly_OffersTheNewestBuild()
    {
        (UpdateStatus status, _) = await CheckAsync(ReleaseChannel.Nightly);

        status.LatestVersion.Should().Be("9.0.3");
    }
}
