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
using System.Text.Json;
using NoMercy.Database.Models.Storage;
using NoMercy.Tests.Api.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Api.Dashboard;

// Locks the CURRENT contract of DriversController: Moderator-gated reads
// (Index/GetTypes/Show/GetSystemLocalId) and Owner-gated mutations
// (Create/Update/UpdateCredentials/Delete). SecondaryUserId (Allowed=true,
// Owner=false, Manage=false) proves rejection at BOTH gates — it is neither
// a moderator nor an owner. Mutations are exercised only for their auth pair
// and validation short-circuit paths (never their success path) so no real
// Driver row or credential is ever written by this suite.
[Trait("Category", "DashboardDrivers")]
public class DriversControllerTests : IClassFixture<NoMercyApiFactory>
{
    private const string BaseUrl = "/api/v1/dashboard/drivers";

    private static readonly string SystemLocalId = Driver.SystemLocalDriverId.ToString();

    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;
    private readonly HttpClient _secondaryUser;

    public DriversControllerTests(NoMercyApiFactory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
        _secondaryUser = factory.CreateClient().AsSecondaryUser();
    }

    [Fact]
    public async Task Index_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync(BaseUrl);

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Index_ReturnsForbidden_WhenSecondaryUserNotModerator()
    {
        HttpResponseMessage response = await _secondaryUser.GetAsync(BaseUrl);

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Index_ReturnsArrayIncludingSeededSystemDriver_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync(BaseUrl);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.ValueKind.Should()
            .Be(JsonValueKind.Array, "Index returns a raw array, not a data envelope");

        JsonElement systemDriver = doc
            .RootElement.EnumerateArray()
            .First(e => e.GetProperty("id").GetString() == SystemLocalId);

        systemDriver.GetProperty("type").GetString().Should().Be("local");
        systemDriver.GetProperty("is_system").GetBoolean().Should().BeTrue();
        systemDriver
            .TryGetProperty("credentials_configured", out JsonElement credsConfigured)
            .Should()
            .BeTrue();
        credsConfigured.GetBoolean().Should().BeFalse();
        systemDriver.TryGetProperty("folder_count", out _).Should().BeTrue();
        systemDriver.TryGetProperty("created_at", out _).Should().BeTrue();
        systemDriver.TryGetProperty("updated_at", out _).Should().BeTrue();
    }

    [Fact]
    public async Task GetTypes_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync($"{BaseUrl}/types");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetTypes_ReturnsForbidden_WhenSecondaryUserNotModerator()
    {
        HttpResponseMessage response = await _secondaryUser.GetAsync($"{BaseUrl}/types");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetTypes_ReturnsAllFiveDriverTypeEntries_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync($"{BaseUrl}/types");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        doc.RootElement.GetArrayLength().Should().Be(5);

        string[] types =
        [
            .. doc.RootElement.EnumerateArray().Select(e => e.GetProperty("type").GetString()!),
        ];

        types.Should().BeEquivalentTo(["local", "nfs", "s3", "r2", "webdav"]);
    }

    [Fact]
    public async Task Show_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync($"{BaseUrl}/{SystemLocalId}");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Show_ReturnsForbidden_WhenSecondaryUserNotModerator()
    {
        HttpResponseMessage response = await _secondaryUser.GetAsync($"{BaseUrl}/{SystemLocalId}");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Show_ReturnsSystemLocalDriverEnvelope_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync($"{BaseUrl}/{SystemLocalId}");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("id").GetString().Should().Be(SystemLocalId);
        doc.RootElement.GetProperty("type").GetString().Should().Be("local");
        doc.RootElement.GetProperty("is_system").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Show_ReturnsNotFound_WhenDriverDoesNotExist()
    {
        Ulid unknownId = Ulid.NewUlid();

        HttpResponseMessage response = await _authed.GetAsync($"{BaseUrl}/{unknownId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSystemLocalId_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync($"{BaseUrl}/system-local");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetSystemLocalId_ReturnsForbidden_WhenSecondaryUserNotModerator()
    {
        HttpResponseMessage response = await _secondaryUser.GetAsync($"{BaseUrl}/system-local");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetSystemLocalId_ReturnsStableSystemDriverId_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync($"{BaseUrl}/system-local");
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("id").GetString().Should().Be(SystemLocalId);
    }

    [Fact]
    public async Task Create_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.PostAsJsonAsync(
            BaseUrl,
            new { name = "contract-test-driver", type = "local" }
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Create_ReturnsForbidden_WhenSecondaryUserNotOwner()
    {
        HttpResponseMessage response = await _secondaryUser.PostAsJsonAsync(
            BaseUrl,
            new { name = "contract-test-driver", type = "local" }
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenTypeInvalid()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            BaseUrl,
            new { name = "contract-test-driver", type = "ftp" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenLocalConfigMissingRootPath()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            BaseUrl,
            new { name = "contract-test-driver", type = "local" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenNameMissing()
    {
        HttpResponseMessage response = await _authed.PostAsJsonAsync(
            BaseUrl,
            new
            {
                name = "",
                type = "local",
                config = new { rootPath = "/mnt/media" },
            }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.PutAsJsonAsync(
            $"{BaseUrl}/{SystemLocalId}",
            new { name = "renamed" }
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Update_ReturnsForbidden_WhenSecondaryUserNotOwner()
    {
        HttpResponseMessage response = await _secondaryUser.PutAsJsonAsync(
            $"{BaseUrl}/{SystemLocalId}",
            new { name = "renamed" }
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Update_ReturnsNotFound_WhenDriverDoesNotExist()
    {
        Ulid unknownId = Ulid.NewUlid();

        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"{BaseUrl}/{unknownId}",
            new { name = "renamed" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ReturnsConflict_WhenTargetingBuiltInSystemLocalDriver()
    {
        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"{BaseUrl}/{SystemLocalId}",
            new { name = "renamed" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateCredentials_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.PutAsJsonAsync(
            $"{BaseUrl}/{SystemLocalId}/credentials",
            new { access_key = "ak", secret_key = "sk" }
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task UpdateCredentials_ReturnsForbidden_WhenSecondaryUserNotOwner()
    {
        HttpResponseMessage response = await _secondaryUser.PutAsJsonAsync(
            $"{BaseUrl}/{SystemLocalId}/credentials",
            new { access_key = "ak", secret_key = "sk" }
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task UpdateCredentials_ReturnsNotFound_WhenDriverDoesNotExist()
    {
        Ulid unknownId = Ulid.NewUlid();

        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"{BaseUrl}/{unknownId}/credentials",
            new { access_key = "ak", secret_key = "sk" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateCredentials_ReturnsConflict_WhenTargetingBuiltInSystemLocalDriver()
    {
        // The system-local guard fires before CredentialManager.SetCredentials
        // and before the row is rewritten, so no real credential is ever written.
        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"{BaseUrl}/{SystemLocalId}/credentials",
            new { access_key = "ak", secret_key = "sk" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task UpdateCredentials_ReturnsBadRequest_WhenBothFieldsBlank()
    {
        // Blank credentials short-circuit BEFORE CredentialManager.SetCredentials is
        // called, so this exercises validation only — no real credential is written.
        HttpResponseMessage response = await _authed.PutAsJsonAsync(
            $"{BaseUrl}/{SystemLocalId}/credentials",
            new { access_key = "", secret_key = "" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Delete_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.DeleteAsync($"{BaseUrl}/{SystemLocalId}");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Delete_ReturnsForbidden_WhenSecondaryUserNotOwner()
    {
        HttpResponseMessage response = await _secondaryUser.DeleteAsync(
            $"{BaseUrl}/{SystemLocalId}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Delete_ReturnsConflict_WhenTargetingBuiltInSystemLocalDriver()
    {
        // The system-local guard fires before any repository lookup or delete —
        // no real row is ever touched by this assertion.
        HttpResponseMessage response = await _authed.DeleteAsync($"{BaseUrl}/{SystemLocalId}");

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Delete_ReturnsNotFound_WhenDriverDoesNotExist()
    {
        Ulid unknownId = Ulid.NewUlid();

        HttpResponseMessage response = await _authed.DeleteAsync($"{BaseUrl}/{unknownId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
