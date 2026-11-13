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

[Trait("Category", "DashboardUsers")]
public class UsersControllerTests : IClassFixture<NoMercyApiFactory>
{
    private readonly NoMercyApiFactory _factory;
    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;

    public UsersControllerTests(NoMercyApiFactory factory)
    {
        _factory = factory;
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
    }

    private static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PatchAsync(HttpClient client, string url, object body) =>
        client.PatchAsync(url, JsonBody(body));

    [Fact]
    public async Task GetUsers_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync("/api/v1/dashboard/users");

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetUsers_ReturnsOk_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/dashboard/users");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetUsers_ReturnsEnvelopeWithDataArray()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/dashboard/users");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("data", out JsonElement data)
            .Should()
            .BeTrue("users response must have a 'data' property");
        data.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task GetUsers_DataArray_ContainsSeededOwner()
    {
        HttpResponseMessage response = await _authed.GetAsync("/api/v1/dashboard/users");

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        JsonElement data = doc.RootElement.GetProperty("data");
        data.GetArrayLength().Should().BeGreaterThan(0, "at least the seeded owner user exists");

        JsonElement ownerItem = data.EnumerateArray().First();
        ownerItem.TryGetProperty("id", out _).Should().BeTrue("user item must expose 'id'");
        ownerItem.TryGetProperty("name", out _).Should().BeTrue("user item must expose 'name'");
        ownerItem.TryGetProperty("email", out _).Should().BeTrue("user item must expose 'email'");
        ownerItem.TryGetProperty("owner", out _).Should().BeTrue("user item must expose 'owner'");
        ownerItem
            .TryGetProperty("allowed", out _)
            .Should()
            .BeTrue("user item must expose 'allowed'");
    }

    [Fact]
    public async Task GetUserDetail_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync(
            $"/api/v1/dashboard/users/{TestAuthHandler.DefaultUserId}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetUserDetail_ReturnsNotFound_WhenUserDoesNotExist()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            $"/api/v1/dashboard/users/{Guid.NewGuid()}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetUserDetail_ReturnsOkWithUserShape_WhenUserExists()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            $"/api/v1/dashboard/users/{TestAuthHandler.SecondaryUserId}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("data", out JsonElement data)
            .Should()
            .BeTrue("user detail response must have a 'data' property");
        data.ValueKind.Should().Be(JsonValueKind.Object);

        data.TryGetProperty("id", out JsonElement id).Should().BeTrue();
        id.GetString().Should().Be(TestAuthHandler.SecondaryUserId.ToString());

        data.TryGetProperty("name", out _).Should().BeTrue("user detail must expose 'name'");
        data.TryGetProperty("email", out _).Should().BeTrue("user detail must expose 'email'");
        data.TryGetProperty("owner", out _).Should().BeTrue("user detail must expose 'owner'");
        data.TryGetProperty("allowed", out _).Should().BeTrue("user detail must expose 'allowed'");
        data.TryGetProperty("library_user", out _)
            .Should()
            .BeTrue("user detail must expose 'library_user'");
        data.TryGetProperty("libraries", out _)
            .Should()
            .BeTrue("user detail must expose 'libraries'");
    }

    [Fact]
    public async Task GetPermissions_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.GetAsync(
            "/api/v1/dashboard/users/permissions"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task GetPermissions_ReturnsOkWithDataArray_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync(
            "/api/v1/dashboard/users/permissions"
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);

        doc.RootElement.TryGetProperty("data", out JsonElement data)
            .Should()
            .BeTrue("permissions response must have a 'data' property");
        data.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task DeleteUser_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _unauthed.DeleteAsync(
            $"/api/v1/dashboard/users/{Guid.NewGuid()}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task DeleteUser_ReturnsNotFound_WhenUserDoesNotExist()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/dashboard/users/{Guid.NewGuid()}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteUser_ReturnsUnauthorized_WhenDeletingOwner()
    {
        HttpResponseMessage response = await _authed.DeleteAsync(
            $"/api/v1/dashboard/users/{TestAuthHandler.DefaultUserId}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task CreateUser_PersistsLibraryGrantUnderNewUsersOwnId_NotTheActingUsersId()
    {
        Guid newUserId = Guid.NewGuid();

        HttpResponseMessage createResponse = await _authed.PostAsync(
            "/api/v1/dashboard/users",
            JsonBody(
                new
                {
                    id = newUserId,
                    email = "grant-test@example.com",
                    name = "Grant Test",
                    manage = false,
                    owner = false,
                    allowed = true,
                    audio_transcoding = true,
                    video_transcoding = true,
                    no_transcoding = false,
                    libraries = new[] { NoMercyApiFactory.MovieLibraryId },
                }
            )
        );

        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage detailResponse = await _authed.GetAsync(
            $"/api/v1/dashboard/users/{newUserId}"
        );
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await detailResponse.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        JsonElement libraryUser = data.GetProperty("library_user");
        libraryUser
            .GetArrayLength()
            .Should()
            .Be(1, "the requested library grant must be persisted for the new user");

        JsonElement grant = libraryUser.EnumerateArray().First();
        grant
            .GetProperty("library_id")
            .GetString()
            .Should()
            .Be(NoMercyApiFactory.MovieLibraryId.ToString());
        grant
            .GetProperty("UserId")
            .GetString()
            .Should()
            .Be(
                newUserId.ToString(),
                "the LibraryUser row must be owned by the newly-created user, not the acting owner"
            );
        grant
            .GetProperty("UserId")
            .GetString()
            .Should()
            .NotBe(TestAuthHandler.DefaultUserId.ToString());
    }

    [Fact]
    public async Task CreateUser_PersistsAllowedAndNoTranscodingFromRequest_NotHardcodedDefaults()
    {
        Guid newUserId = Guid.NewGuid();

        HttpResponseMessage createResponse = await _authed.PostAsync(
            "/api/v1/dashboard/users",
            JsonBody(
                new
                {
                    id = newUserId,
                    email = "restricted-test@example.com",
                    name = "Restricted Test",
                    manage = false,
                    owner = false,
                    allowed = false,
                    audio_transcoding = false,
                    video_transcoding = false,
                    no_transcoding = false,
                    libraries = Array.Empty<Ulid>(),
                }
            )
        );

        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage detailResponse = await _authed.GetAsync(
            $"/api/v1/dashboard/users/{newUserId}"
        );
        detailResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await detailResponse.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        data.GetProperty("allowed")
            .GetBoolean()
            .Should()
            .BeFalse("the request's allowed=false must be persisted, not overridden to true");
        data.GetProperty("no_transcoding")
            .GetBoolean()
            .Should()
            .BeFalse(
                "the request's no_transcoding=false must be persisted, not overridden to true"
            );
    }

    private HttpClient ClientFor(Guid userId)
    {
        HttpClient client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthDefaults.TestUserIdHeader, userId.ToString());
        return client;
    }

    private async Task<Guid> CreateUserAsync(string label, bool manage)
    {
        Guid userId = Guid.NewGuid();

        HttpResponseMessage response = await _authed.PostAsync(
            "/api/v1/dashboard/users",
            JsonBody(
                new
                {
                    id = userId,
                    email = $"{label}-{userId:N}@example.com",
                    name = label,
                    manage,
                    owner = false,
                    allowed = true,
                    audio_transcoding = true,
                    video_transcoding = true,
                    no_transcoding = false,
                    libraries = new[] { NoMercyApiFactory.MovieLibraryId },
                }
            )
        );
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return userId;
    }

    private async Task<(bool Allowed, int LibraryCount)> ReadPermissionRowAsync(Guid userId)
    {
        HttpResponseMessage response = await _authed.GetAsync($"/api/v1/dashboard/users/{userId}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        JsonElement data = doc.RootElement.GetProperty("data");

        return (
            data.GetProperty("allowed").GetBoolean(),
            data.GetProperty("library_user").GetArrayLength()
        );
    }

    private static object RevokeEverything() =>
        new
        {
            manage = false,
            owner = false,
            allowed = false,
            optical_access = false,
            audio_transcoding = false,
            video_transcoding = false,
            no_transcoding = false,
            libraries = Array.Empty<Ulid>(),
        };

    // AUD-1112: a moderator cannot change the owner's permissions.
    [Fact]
    public async Task UpdatePermissions_ModeratorTargetsOwner_IsRefusedAndRowUnchanged()
    {
        Guid moderatorId = await CreateUserAsync("moderator", manage: true);
        (bool Allowed, int LibraryCount) before = await ReadPermissionRowAsync(
            TestAuthHandler.DefaultUserId
        );

        HttpResponseMessage response = await PatchAsync(
            ClientFor(moderatorId),
            $"/api/v1/dashboard/users/{TestAuthHandler.DefaultUserId}/permissions",
            RevokeEverything()
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadPermissionRowAsync(TestAuthHandler.DefaultUserId)).Should().Be(before);
    }

    // AUD-1112: a moderator cannot change another moderator's permissions.
    [Fact]
    public async Task UpdatePermissions_ModeratorTargetsOtherModerator_IsRefusedAndRowUnchanged()
    {
        Guid moderatorId = await CreateUserAsync("moderator", manage: true);
        Guid otherModeratorId = await CreateUserAsync("other-moderator", manage: true);
        (bool Allowed, int LibraryCount) before = await ReadPermissionRowAsync(otherModeratorId);

        HttpResponseMessage response = await PatchAsync(
            ClientFor(moderatorId),
            $"/api/v1/dashboard/users/{otherModeratorId}/permissions",
            RevokeEverything()
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ReadPermissionRowAsync(otherModeratorId)).Should().Be(before);
    }

    // AUD-1112: a moderator cannot read the owner's permissions.
    [Fact]
    public async Task GetUserPermissions_ModeratorTargetsOwner_IsRefused()
    {
        Guid moderatorId = await CreateUserAsync("moderator", manage: true);

        HttpResponseMessage response = await ClientFor(moderatorId)
            .GetAsync($"/api/v1/dashboard/users/{TestAuthHandler.DefaultUserId}/permissions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // AUD-1112: a moderator cannot read another moderator's permissions.
    [Fact]
    public async Task GetUserPermissions_ModeratorTargetsOtherModerator_IsRefused()
    {
        Guid moderatorId = await CreateUserAsync("moderator", manage: true);
        Guid otherModeratorId = await CreateUserAsync("other-moderator", manage: true);

        HttpResponseMessage response = await ClientFor(moderatorId)
            .GetAsync($"/api/v1/dashboard/users/{otherModeratorId}/permissions");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UserPermissions_ModeratorTargetsRegularUser_StillReadsAndUpdates()
    {
        Guid moderatorId = await CreateUserAsync("moderator", manage: true);
        Guid regularUserId = await CreateUserAsync("regular", manage: false);
        HttpClient moderator = ClientFor(moderatorId);

        HttpResponseMessage read = await moderator.GetAsync(
            $"/api/v1/dashboard/users/{regularUserId}/permissions"
        );
        HttpResponseMessage update = await PatchAsync(
            moderator,
            $"/api/v1/dashboard/users/{regularUserId}/permissions",
            RevokeEverything()
        );

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPermissionRowAsync(regularUserId)).Should().Be((false, 0));
    }

    [Fact]
    public async Task UserPermissions_OwnerTargetsModerator_StillReadsAndUpdates()
    {
        Guid moderatorId = await CreateUserAsync("moderator", manage: true);

        HttpResponseMessage read = await _authed.GetAsync(
            $"/api/v1/dashboard/users/{moderatorId}/permissions"
        );
        HttpResponseMessage update = await PatchAsync(
            _authed,
            $"/api/v1/dashboard/users/{moderatorId}/permissions",
            RevokeEverything()
        );

        read.StatusCode.Should().Be(HttpStatusCode.OK);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadPermissionRowAsync(moderatorId)).Should().Be((false, 0));
    }

    [Fact]
    public async Task UpdateNotifications_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await PatchAsync(
            _unauthed,
            "/api/v1/dashboard/users/notifications",
            new { }
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task UpdateNotifications_ReturnsNotImplemented_WhenAuthenticated()
    {
        HttpResponseMessage response = await PatchAsync(
            _authed,
            "/api/v1/dashboard/users/notifications",
            new { }
        );

        // Notification settings have no storage behind them yet — the
        // endpoint answers honestly instead of a 200 that persisted nothing.
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }
}
