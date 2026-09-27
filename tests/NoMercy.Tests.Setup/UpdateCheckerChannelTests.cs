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

    private sealed class GithubStub : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string url = request.RequestUri!.ToString();
            Requested.Add(url);
            string json = url == LatestUrl ? Latest : ReleaseList;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private static async Task<(UpdateStatus Status, GithubStub Github)> CheckAsync(
        ReleaseChannel channel
    )
    {
        GithubStub github = new();
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
    public async Task Nightly_OffersTheNewestBuild()
    {
        (UpdateStatus status, _) = await CheckAsync(ReleaseChannel.Nightly);

        status.LatestVersion.Should().Be("9.0.3");
    }
}
