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

namespace NoMercy.Tests.Api.Music;

/// <summary>
/// Locks the AUTH TIER contract on every mutating action across the six music
/// controllers (MusicController, TracksController, AlbumsController,
/// ArtistsController, PlaylistsController). Each test uses ONLY fake ids
/// (Guid.NewGuid()) or a brand-new, self-cleaned row — never a seeded id — so
/// no seeded music-library row is ever mutated or deleted by this suite.
///
/// TestAuthHandler.SecondaryUserId is Allowed=true, Manage=false, Owner=false —
/// it passes [Authorize(Policy="MediaAccess")] (only requires Allowed), but
/// fails [Authorize(Policy="Moderator")] (requires Manage or Owner). Every
/// Moderator-gated action below is exercised with the secondary user
/// specifically to prove that gate actually rejects a non-moderator, not just
/// that the default (Owner+Manage) test identity can call it — the exact gap
/// called out for this batch.
/// </summary>
[Trait("Category", "Characterization")]
public class MusicMutationAuthTests : IClassFixture<NoMercyApiFactory>
{
    private readonly HttpClient _owner;
    private readonly HttpClient _secondary;
    private readonly HttpClient _anonymous;

    public MusicMutationAuthTests(NoMercyApiFactory factory)
    {
        _owner = factory.CreateClient().AsAuthenticated();
        _secondary = factory.CreateClient().AsSecondaryUser();
        _anonymous = factory.CreateClient().AsUnauthenticated();
    }

    private static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    private static MultipartFormDataContent EmptyMultipart()
    {
        MultipartFormDataContent content = new();
        return content;
    }

    private static void AssertForbiddenProblemDetails(JsonElement root)
    {
        root.GetProperty("status").GetInt32().Should().Be(403);
        root.GetProperty("title").GetString().Should().Be("Unauthorized.");
    }

    private static void AssertNotFoundDetail(JsonElement root, string expectedDetailSubstring)
    {
        root.GetProperty("status").GetInt32().Should().Be(404);
        root.GetProperty("detail").GetString().Should().Contain(expectedDetailSubstring);
    }

    // =========================================================================
    // MusicController — POST /api/v1/music/search/{query}/{type} (class MediaAccess)
    // =========================================================================

    [Fact]
    public async Task TypeSearch_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            "/api/v1/music/search/test/artist",
            JsonBody(new { })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task TypeSearch_SecondaryUser_PassesMediaAccess_ReturnsEmptyPlaceholder()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            "/api/v1/music/search/test/artist",
            JsonBody(new { })
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data").GetArrayLength().Should().Be(0);
    }

    // =========================================================================
    // TracksController — bare class-level MediaAccess, no per-method tier
    // =========================================================================

    [Fact]
    public async Task TracksLike_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/tracks/{Guid.NewGuid()}/like",
            JsonBody(new { value = true })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task TracksLike_SecondaryUser_PassesMediaAccess_ReturnsNotFoundForFakeId()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/tracks/{Guid.NewGuid()}/like",
            JsonBody(new { value = true })
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertNotFoundDetail(doc.RootElement, "Track not found");
    }

    [Fact]
    public async Task TracksLyricsOffset_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PatchAsync(
            $"/api/v1/music/tracks/{Guid.NewGuid()}/lyrics-offset",
            JsonBody(new { offset = 100 })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task TracksLyricsOffset_SecondaryUser_PassesMediaAccess_ReturnsNotFoundForFakeId()
    {
        HttpResponseMessage response = await _secondary.PatchAsync(
            $"/api/v1/music/tracks/{Guid.NewGuid()}/lyrics-offset",
            JsonBody(new { offset = (int?)null })
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertNotFoundDetail(doc.RootElement, "Track not found");
    }

    [Fact]
    public async Task TracksPlayback_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/tracks/{Guid.NewGuid()}/playback",
            JsonBody(new { })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task TracksPlayback_SecondaryUser_PassesMediaAccess_ReturnsNotFoundForFakeId()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/tracks/{Guid.NewGuid()}/playback",
            JsonBody(new { })
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertNotFoundDetail(doc.RootElement, "Track not found");
    }

    // =========================================================================
    // AlbumsController — class-level [Authorize(Policy="MediaAccess")], plus a
    // manual AuthPolicy.IsAllowed() check on Like (same tier: Allowed=true
    // suffices); Rescan / Edit / Cover are [Authorize(Policy="Moderator")].
    // =========================================================================

    [Fact]
    public async Task AlbumsLike_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}/like",
            JsonBody(new { value = true })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task AlbumsLike_SecondaryUser_PassesAllowedCheck_ReturnsUnprocessableForFakeId()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}/like",
            JsonBody(new { value = true })
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetInt32().Should().Be(422);
        doc.RootElement.GetProperty("detail").GetString().Should().Contain("Albums not found");
    }

    [Fact]
    public async Task AlbumsRescan_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}/rescan",
            JsonBody(new { })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task AlbumsRescan_SecondaryUser_RejectedByModeratorGate()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}/rescan",
            JsonBody(new { })
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertForbiddenProblemDetails(doc.RootElement);
    }

    [Fact]
    public async Task AlbumsRescan_Owner_PassesModeratorGate_ReturnsOk()
    {
        // Rescan never reads the repository for the id at all — it always
        // returns "Rescan started" regardless — so a fake id is fully safe here.
        HttpResponseMessage response = await _owner.PostAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}/rescan",
            JsonBody(new { })
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("ok");
        doc.RootElement.GetProperty("message").GetString().Should().Be("Rescan started");
    }

    [Fact]
    public async Task AlbumsEdit_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PatchAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}",
            JsonBody(new { name = "Hijacked" })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task AlbumsEdit_SecondaryUser_RejectedByModeratorGate_BeforeReachingLookup()
    {
        HttpResponseMessage response = await _secondary.PatchAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}",
            JsonBody(new { name = "Hijacked" })
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AlbumsCover_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}/cover",
            EmptyMultipart()
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task AlbumsCover_SecondaryUser_RejectedByModeratorGate()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/albums/{Guid.NewGuid()}/cover",
            EmptyMultipart()
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // =========================================================================
    // ArtistsController — same class-level MediaAccess/Moderator split as
    // Albums, plus Destroy (Moderator) which this suite only ever calls with a
    // fake id.
    // =========================================================================

    [Fact]
    public async Task ArtistsLike_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}/like",
            JsonBody(new { value = true })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task ArtistsLike_SecondaryUser_PassesAllowedCheck_ReturnsUnprocessableForFakeId()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}/like",
            JsonBody(new { value = true })
        );

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("detail").GetString().Should().Contain("Artist not found");
    }

    [Fact]
    public async Task ArtistsRescan_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}/rescan",
            JsonBody(new { })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task ArtistsRescan_SecondaryUser_RejectedByModeratorGate()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}/rescan",
            JsonBody(new { })
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // Rescan used to return "Rescan started" for any id, real or not, without
    // ever reading the repository — the endpoint named for backfilling an
    // artist's FanArt images did nothing. It now looks the artist up before
    // doing real work, so a fake id — same as every other mutation here —
    // passes the moderator gate and then 404s.
    [Fact]
    public async Task ArtistsRescan_Owner_PassesModeratorGate_ReturnsNotFoundForFakeId()
    {
        HttpResponseMessage response = await _owner.PostAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}/rescan",
            JsonBody(new { })
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("detail").GetString().Should().Contain("Artist not found");
    }

    [Fact]
    public async Task ArtistsDestroy_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.DeleteAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task ArtistsDestroy_SecondaryUser_RejectedByModeratorGate()
    {
        HttpResponseMessage response = await _secondary.DeleteAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ArtistsDestroy_Owner_FakeId_ReturnsNotFound()
    {
        // A fake id proves the missing-row path without ever touching the
        // seeded ArtistId1 row every other music test depends on.
        HttpResponseMessage response = await _owner.DeleteAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ArtistsEdit_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PatchAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}",
            JsonBody(new { name = "Hijacked" })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task ArtistsEdit_SecondaryUser_RejectedByModeratorGate()
    {
        HttpResponseMessage response = await _secondary.PatchAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}",
            JsonBody(new { name = "Hijacked" })
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ArtistsCover_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}/cover",
            EmptyMultipart()
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task ArtistsCover_SecondaryUser_RejectedByModeratorGate()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/artists/{Guid.NewGuid()}/cover",
            EmptyMultipart()
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // =========================================================================
    // PlaylistsController — Create/Edit/Destroy/AddTrack/RemoveTrack are
    // [Authorize(Policy="MediaAccess")] (Allowed=true suffices); Cover is
    // [Authorize(Policy="Moderator")].
    // =========================================================================

    [Fact]
    public async Task PlaylistsCreate_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            "/api/v1/music/playlists",
            JsonBody(new { name = "Anonymous Music Playlist" })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task PlaylistsCreate_SecondaryUser_PassesMediaAccess_CanCreateAndDeleteOwnPlaylist()
    {
        // Proves the MediaAccess gate lets a non-moderator all the way through
        // to a real write, then immediately cleans up via the same identity's
        // own Destroy call so no residual row survives this test.
        string uniqueName = $"Secondary Tier Playlist {Guid.NewGuid()}";

        HttpResponseMessage create = await _secondary.PostAsync(
            "/api/v1/music/playlists",
            JsonBody(new { name = uniqueName })
        );
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument createDoc = JsonDocument.Parse(await create.Content.ReadAsStringAsync());
        Guid createdId = createDoc.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        HttpResponseMessage delete = await _secondary.DeleteAsync(
            $"/api/v1/music/playlists/{createdId}"
        );
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument deleteDoc = JsonDocument.Parse(await delete.Content.ReadAsStringAsync());
        deleteDoc
            .RootElement.GetProperty("data")
            .GetString()
            .Should()
            .Be("Playlist deleted successfully");
    }

    [Fact]
    public async Task PlaylistsDestroy_Owner_FakeId_ReturnsNotFound()
    {
        HttpResponseMessage response = await _owner.DeleteAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PlaylistsEdit_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PatchAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}",
            JsonBody(new { name = "Hijacked" })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task PlaylistsEdit_SecondaryUser_PassesMediaAccess_ReturnsNotFoundForFakeId()
    {
        HttpResponseMessage response = await _secondary.PatchAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}",
            JsonBody(new { name = "Whatever" })
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertNotFoundDetail(doc.RootElement, "Playlist not found");
    }

    [Fact]
    public async Task PlaylistsDestroy_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.DeleteAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task PlaylistsDestroy_SecondaryUser_FakeId_ReturnsNotFound()
    {
        // A delete matching zero rows (id + ownership scoped) answers 404 for
        // the MediaAccess tier too.
        HttpResponseMessage response = await _secondary.DeleteAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PlaylistsCover_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}/cover",
            EmptyMultipart()
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task PlaylistsCover_SecondaryUser_RejectedByModeratorGate()
    {
        // Cover is the ONE Playlists mutation gated at Moderator rather than
        // MediaAccess — unlike every sibling mutation on this controller.
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}/cover",
            EmptyMultipart()
        );

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PlaylistsAddTrack_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.PostAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}/tracks",
            JsonBody(new { id = Guid.NewGuid() })
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task PlaylistsAddTrack_SecondaryUser_PassesMediaAccess_ReturnsNotFoundForFakePlaylist()
    {
        HttpResponseMessage response = await _secondary.PostAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}/tracks",
            JsonBody(new { id = Guid.NewGuid() })
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertNotFoundDetail(doc.RootElement, "Playlist not found");
    }

    [Fact]
    public async Task PlaylistsRemoveTrack_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await _anonymous.DeleteAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}/tracks/{Guid.NewGuid()}"
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task PlaylistsRemoveTrack_SecondaryUser_PassesMediaAccess_ReturnsNotFoundForFakeIds()
    {
        HttpResponseMessage response = await _secondary.DeleteAsync(
            $"/api/v1/music/playlists/{Guid.NewGuid()}/tracks/{Guid.NewGuid()}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        AssertNotFoundDetail(doc.RootElement, "Track not found in playlist");
    }
}
