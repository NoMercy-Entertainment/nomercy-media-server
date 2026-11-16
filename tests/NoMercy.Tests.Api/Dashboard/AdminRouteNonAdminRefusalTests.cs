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
using System.Text.Json;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

/// <summary>
/// Admin routes that a signed-in user without the Manage permission must not reach.
/// The secondary test user is allowed on the server but is neither owner nor moderator,
/// like a family member. Each route answers that user 403 before its action runs; the
/// owner keeps the answer it had.
/// </summary>
[Trait("Category", "Authorization")]
public class AdminRouteNonAdminRefusalTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;

    public AdminRouteNonAdminRefusalTests(NoMercyApiFactory factory)
    {
        _factory = factory;
    }

    private static HttpRequestMessage Request(string method, string url, string body = "{}") =>
        new(new HttpMethod(method), url)
        {
            Content =
                method == "GET" ? null : new StringContent(body, Encoding.UTF8, "application/json"),
        };

    [Theory]
    [InlineData("POST", "/api/v1/dashboard/server/start")]
    [InlineData("POST", "/api/v1/dashboard/encoderprofiles")]
    [InlineData("POST", "/api/v1/dashboard/encoding/presets/preview")]
    [InlineData("GET", "/api/v1/dashboard/server/setup")]
    [InlineData("PATCH", "/api/v1/dashboard/server/info")]
    [InlineData("POST", "/api/v1/dashboard/libraries")]
    [InlineData(
        "PATCH",
        "/api/v1/dashboard/users/37d03e60-7b0a-4246-a85b-a5618966a383/permissions"
    )]
    public async Task NonAdminUser_IsForbidden(string method, string url)
    {
        HttpClient member = _factory.CreateClient().AsSecondaryUser();

        HttpResponseMessage response = await member.SendAsync(Request(method, url));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/dashboard/server/start", "{}", HttpStatusCode.NotImplemented)]
    [InlineData("/api/v1/dashboard/encoderprofiles", "{}", HttpStatusCode.Gone)]
    [InlineData(
        "/api/v1/dashboard/encoding/presets/preview",
        """{"profile_json":"{}","video_file_id":"placeholder"}""",
        HttpStatusCode.NotImplemented
    )]
    public async Task Owner_KeepsTheAnswerItHad(string url, string body, HttpStatusCode expected)
    {
        HttpClient owner = _factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await owner.SendAsync(Request("POST", url, body));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Owner_ReadsTheServerSetup()
    {
        HttpClient owner = _factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await owner.GetAsync("/api/v1/dashboard/server/setup");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Owner_CreatesALibrary()
    {
        HttpClient owner = _factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await owner.PostAsync("/api/v1/dashboard/libraries", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>An unknown user id: past the gate to the lookup, and nothing changes.</summary>
    [Fact]
    public async Task Owner_UpdatingPermissions_GetsPastTheGate()
    {
        HttpClient owner = _factory.CreateClient().AsAuthenticated();

        HttpResponseMessage response = await owner.SendAsync(
            Request(
                "PATCH",
                $"/api/v1/dashboard/users/{Guid.NewGuid()}/permissions",
                """{"allowed":false,"optical_access":false,"audio_transcoding":false,"video_transcoding":false,"no_transcoding":false,"manage":false,"libraries":[]}"""
            )
        );

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The server keeps its current name, so nothing changes. The test host holds no
    /// token for the NoMercy API, so the action answers 503 after the gate.
    /// </summary>
    [Fact]
    public async Task Owner_RenamingTheServer_GetsPastTheGate()
    {
        HttpClient owner = _factory.CreateClient().AsAuthenticated();
        using JsonDocument info = JsonDocument.Parse(
            await owner.GetStringAsync("/api/v1/dashboard/server/info")
        );
        string name = info.RootElement.GetProperty("data").GetProperty("server").GetString()!;

        HttpResponseMessage response = await owner.SendAsync(
            Request(
                "PATCH",
                "/api/v1/dashboard/server/info",
                JsonSerializer.Serialize(new { name })
            )
        );

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
