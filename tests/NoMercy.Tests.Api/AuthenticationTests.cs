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
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Characterization")]
public class AuthenticationTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public AuthenticationTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AnonymousEndpoint_ReturnsOk_WithoutAuth()
    {
        HttpClient client = _factory.CreateClient().AsUnauthenticated();

        HttpResponseMessage response = await client.GetAsync("/status");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedEndpoint_ReturnsOk_WhenAuthenticated()
    {
        HttpClient client = _factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.GetAsync("/api/v1/setup/permissions");

        string content = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected OK, got {(int)response.StatusCode}: {content}"
        );
        Assert.Contains("owner", content);
        Assert.Contains("allowed", content);
    }

    [Fact]
    public async Task Permissions_AlsoAnswerInsideData_ForReleasedAppsThatReadTheEnvelope()
    {
        // Android 2026.11.0 and older read {data:{owner,manager}}; newer apps read the bare object.
        HttpClient client = _factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await client.GetAsync("/api/v1/setup/permissions");

        System.Text.Json.JsonElement root = System
            .Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement;
        Assert.True(root.TryGetProperty("owner", out _));
        Assert.True(root.TryGetProperty("data", out System.Text.Json.JsonElement data));
        Assert.Equal(
            root.GetProperty("owner").GetBoolean(),
            data.GetProperty("owner").GetBoolean()
        );
        Assert.Equal(
            root.GetProperty("manager").GetBoolean(),
            data.GetProperty("manager").GetBoolean()
        );
    }

    [Fact]
    public async Task AuthenticatedEndpoint_ReturnsUnauthorized_WhenNotAuthenticated()
    {
        HttpClient client = _factory.CreateClient().AsUnauthenticated();

        HttpResponseMessage response = await client.GetAsync("/api/v1/setup/permissions");

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"Expected 401 or 403, got {(int)response.StatusCode}"
        );
    }
}
