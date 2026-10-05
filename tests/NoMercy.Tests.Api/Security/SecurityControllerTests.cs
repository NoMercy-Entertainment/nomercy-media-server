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
using System.Net.Http.Json;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Security;

public class SecurityControllerTests(NoMercyApiFactory factory) : IClassFixture<NoMercyApiFactory>
{
    [Fact]
    public async Task GetBans_RequiresAuthentication()
    {
        HttpClient client = factory.CreateClient().AsUnauthenticated();

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/security/bans");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GetBans_ReturnsTheEnvelope()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/security/bans");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"data\"");
    }

    [Fact]
    public async Task PostBan_RejectsAMalformedAddress()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/dashboard/security/bans",
            new { address = "not-an-ip", minutes = 60 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostBan_RefusesToBanAnAddressOnYourOwnNetwork()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/dashboard/security/bans",
            new { address = "192.168.1.50", minutes = 60 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostThenDeleteBan_RoundTrips()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/v1/dashboard/security/bans",
            new { address = "203.0.113.200", minutes = 30 }
        );
        HttpResponseMessage listed = await client.GetAsync("/api/v1/dashboard/security/bans");
        HttpResponseMessage removed = await client.DeleteAsync(
            "/api/v1/dashboard/security/bans/203.0.113.200"
        );
        HttpResponseMessage listedAgain = await client.GetAsync("/api/v1/dashboard/security/bans");

        created.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listed.Content.ReadAsStringAsync()).Should().Contain("203.0.113.200");
        removed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await listedAgain.Content.ReadAsStringAsync()).Should().NotContain("203.0.113.200");
    }

    [Fact]
    public async Task DeleteBan_ThatDoesNotExist_Is404()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.DeleteAsync(
            "/api/v1/dashboard/security/bans/203.0.113.201"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSettings_ExposesTheDefaultsToTheDashboard()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.GetAsync("/api/v1/dashboard/security/settings");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"max_score\"");
        body.Should().Contain("\"window_minutes\"");
        body.Should().Contain("\"allowlist\"");
    }

    [Fact]
    public async Task PatchSettings_RejectsAnAllowlistItCannotParse()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            "/api/v1/dashboard/security/settings",
            new { allowlist = "203.0.113.0/24, nonsense" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchSettings_RejectsAThresholdBelowOne()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            "/api/v1/dashboard/security/settings",
            new { max_score = 0 }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchSettings_StoresOnlyWhatWasSentAndEchoesTheResult()
    {
        HttpClient client = factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.PatchAsJsonAsync(
            "/api/v1/dashboard/security/settings",
            new { ban_minutes = 45 }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body.Should().Contain("\"ban_minutes\":45");
        body.Should().Contain("\"window_minutes\":10");
    }
}
