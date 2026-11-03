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
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Setup;

/// <summary>
/// The server, CLI, app and launcher downloads all resolve the media server
/// release through the install's update channel, so a beta server never pulls
/// a stable CLI and a stable server never pulls a nightly.
/// </summary>
[Trait("Category", "Unit")]
public class BinariesReleaseChannelTests
{
    private const string ReleaseList = """
        [
          { "tag_name": "v9.0.3", "draft": false, "prerelease": true, "body": "## Install" },
          { "tag_name": "v9.0.2", "draft": false, "prerelease": true, "body": "<!-- nomercy-channel: beta -->" },
          { "tag_name": "v9.0.1", "draft": false, "prerelease": false, "body": null }
        ]
        """;

    /// <summary>
    /// A page of nightlies only: what every dev push adds in front of the newest
    /// beta or stable, until the first page of the release list holds no release
    /// a beta install may have.
    /// </summary>
    private const string NightlyPage = """
        [
          { "tag_name": "v9.1.3", "draft": false, "prerelease": true, "body": "## Install" },
          { "tag_name": "v9.1.2", "draft": false, "prerelease": true, "body": "## Install" }
        ]
        """;

    private sealed class GithubStub(HttpStatusCode status) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requested.Add(request.RequestUri!.ToString());
            return Task.FromResult(
                new HttpResponseMessage(status)
                {
                    Content = new StringContent(ReleaseList, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    /// <summary>
    /// Serves the release list a page at a time, with the <c>Link</c> header
    /// GitHub sends while more pages follow. <see cref="Endless"/> keeps sending
    /// a next page forever, as a repository with thousands of nightlies would.
    /// </summary>
    private sealed class PagedGithubStub(params string[] pages) : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        public bool Endless { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string url = request.RequestUri!.ToString();
            Requested.Add(url);

            int page = PageNumber(url);
            string json = pages[Math.Min(page, pages.Length) - 1];
            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };

            if (Endless || page < pages.Length)
                response.Headers.Add(
                    "Link",
                    $"<{PageUrl(page + 1)}>; rel=\"next\", <{PageUrl(pages.Length)}>; rel=\"last\""
                );

            return Task.FromResult(response);
        }

        private static string PageUrl(int page) =>
            $"{ReleaseChannelSelector.MediaServerReleaseListUrl}&page={page}";

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

    private static Binaries Create(HttpMessageHandler github, ReleaseChannel channel)
    {
        LocalStorageDriver driver = new();
        return new(
            driver,
            null!,
            new HttpClient(github),
            new RuntimeServerSettings { UpdateChannel = channel }
        );
    }

    [Theory]
    [InlineData(ReleaseChannel.Beta, "v9.0.2")]
    [InlineData(ReleaseChannel.Nightly, "v9.0.3")]
    public async Task PrereleaseChannels_PickFromTheReleaseList(
        ReleaseChannel channel,
        string expectedTag
    )
    {
        GithubStub github = new(HttpStatusCode.OK);

        GithubReleaseResponse release = await Create(github, channel).GetMediaServerReleaseInfo();

        github.Requested.Should().Equal(ReleaseChannelSelector.MediaServerReleaseListUrl);
        release.TagName.Should().Be(expectedTag);
    }

    [Fact]
    public async Task Beta_PagesPastTheNightlies_UntilItFindsTheNewestBeta()
    {
        PagedGithubStub github = new(NightlyPage, ReleaseList);

        GithubReleaseResponse release = await Create(github, ReleaseChannel.Beta)
            .GetMediaServerReleaseInfo();

        release.TagName.Should().Be("v9.0.2", "the newest beta sits on the second page");
        github.Requested.Should().HaveCount(2);
    }

    [Fact]
    public async Task Nightly_StopsAtTheFirstPage_ThatOffersARelease()
    {
        PagedGithubStub github = new(NightlyPage, ReleaseList);

        GithubReleaseResponse release = await Create(github, ReleaseChannel.Nightly)
            .GetMediaServerReleaseInfo();

        release.TagName.Should().Be("v9.1.3");
        github.Requested.Should().ContainSingle();
    }

    [Fact]
    public async Task EndlessNightlies_StopAtThePageBound_SoTheInstalledBuildIsKept()
    {
        PagedGithubStub github = new(NightlyPage) { Endless = true };

        GithubReleaseResponse release = await Create(github, ReleaseChannel.Beta)
            .GetMediaServerReleaseInfo();

        release.TagName.Should().BeEmpty();
        github.Requested.Should().HaveCount(GithubReleaseList.MaxPages);
    }

    [Fact]
    public async Task UnreadableList_ReturnsAnEmptyRelease_SoTheInstalledBuildIsKept()
    {
        GithubStub github = new(HttpStatusCode.InternalServerError);

        GithubReleaseResponse release = await Create(github, ReleaseChannel.Beta)
            .GetMediaServerReleaseInfo();

        release.TagName.Should().BeEmpty();
        release.Assets.Should().BeEmpty();
    }
}
