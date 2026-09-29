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

    private static Binaries Create(GithubStub github, ReleaseChannel channel)
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
    public async Task UnreadableList_ReturnsAnEmptyRelease_SoTheInstalledBuildIsKept()
    {
        GithubStub github = new(HttpStatusCode.InternalServerError);

        GithubReleaseResponse release = await Create(github, ReleaseChannel.Beta)
            .GetMediaServerReleaseInfo();

        release.TagName.Should().BeEmpty();
        release.Assets.Should().BeEmpty();
    }
}
