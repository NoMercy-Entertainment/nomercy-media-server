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
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

// Its own class, so its own host: the limiter counts per user for the life of a host.
[Trait("Category", "ServerActivity")]
public class PlaybackFailureReportRateLimitTests(NoMercyApiFactory factory)
    : IClassFixture<NoMercyApiFactory>
{
    private const string Route = "/api/v1/dashboard/activity/playback-failure";
    private const string Report =
        """{"type":"movie","list_id":"129","item_id":"129","error_code":"MEDIA_ERR_DECODE"}""";

    [Fact]
    public async Task PlaybackFailure_EleventhReportInAMinute_Returns429()
    {
        using HttpClient client = factory.CreateClient().AsAuthenticated();

        for (int report = 1; report <= 10; report++)
        {
            HttpResponseMessage accepted = await client.PostAsync(
                Route,
                new StringContent(Report, Encoding.UTF8, "application/json")
            );
            Assert.True(
                accepted.StatusCode == HttpStatusCode.Accepted,
                $"Report {report}: expected 202, got {(int)accepted.StatusCode}"
            );
        }

        HttpResponseMessage refused = await client.PostAsync(
            Route,
            new StringContent(Report, Encoding.UTF8, "application/json")
        );
        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);

        // Another user has their own count.
        using HttpClient secondary = factory.CreateClient().AsSecondaryUser();
        HttpResponseMessage other = await secondary.PostAsync(
            Route,
            new StringContent(Report, Encoding.UTF8, "application/json")
        );
        Assert.NotEqual(HttpStatusCode.TooManyRequests, other.StatusCode);
    }
}
