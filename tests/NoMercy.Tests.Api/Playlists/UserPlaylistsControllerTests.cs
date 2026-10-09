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

namespace NoMercy.Tests.Api.Playlists;

/// <summary>
/// Covers api/v1/playlists — user-created, ordered, VIDEO-ONLY playlists
/// (movies + tv shows + episodes + specials — never music tracks). Movie 129
/// ("Spirited Away") and episode 62085 ("Pilot", Breaking Bad) are pre-seeded
/// WITH video files (playable); special NoMercyApiFactory.FavoriteSpecialId is
/// a seeded special. See NoMercyApiFactory.SeedMediaData.
///
/// <see cref="Index_DoesNotReturnMusicPlaylist"/> is the leak-regression test:
/// NoMercyApiFactory.PlaylistId1 is a music playlist owned by the same default
/// test user this fixture's "_authed" client represents — proving it never
/// appears here is what distinguishes this slice's separate-container design
/// from the original (rejected) design that reused the music Playlist table.
/// </summary>
[Trait("Category", "Playlists")]
public class UserPlaylistsControllerTests : IClassFixture<NoMercyApiFactory>
{
    private const string BaseUrl = "/api/v1/playlists";

    private readonly HttpClient _authed;
    private readonly HttpClient _unauthed;
    private readonly HttpClient _secondaryUser;

    public UserPlaylistsControllerTests(NoMercyApiFactory factory)
    {
        _authed = factory.CreateClient().AsAuthenticated();
        _unauthed = factory.CreateClient().AsUnauthenticated();
        _secondaryUser = factory.CreateClient().AsSecondaryUser();
    }

    private static StringContent JsonBody(object obj) =>
        new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        string url,
        object body
    ) => client.PostAsync(url, JsonBody(body));

    private static Task<HttpResponseMessage> PatchAsync(
        HttpClient client,
        string url,
        object body
    ) => client.PatchAsync(url, JsonBody(body));

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string url, object body) =>
        client.PutAsync(url, JsonBody(body));

    private async Task<Guid> CreatePlaylistAsync(string name)
    {
        HttpResponseMessage response = await PostAsync(_authed, BaseUrl, new { name });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").GetProperty("id").GetGuid();
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
    public async Task Index_ReturnsEnvelopeWithDataArray_WhenAuthenticated()
    {
        HttpResponseMessage response = await _authed.GetAsync(BaseUrl);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        string body = await response.Content.ReadAsStringAsync();
        using JsonDocument doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("data", out JsonElement data)
            .Should()
            .BeTrue("playlists index must have a 'data' property");
        data.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task Index_DoesNotReturnMusicPlaylist()
    {
        // NoMercyApiFactory.PlaylistId1 is a MUSIC playlist owned by the very
        // same user this client authenticates as — it must never surface on
        // the video-only endpoint, proving the two features share no table.
        HttpResponseMessage response = await _authed.GetAsync(BaseUrl);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(p => p.GetProperty("id").GetString())
            .Should()
            .NotContain(NoMercyApiFactory.PlaylistId1.ToString());
    }

    [Fact]
    public async Task Show_ReturnsNotFound_ForMusicPlaylistId()
    {
        // Even knowing the music playlist's id, it must 404 on this endpoint —
        // it lives in a different table this controller never queries.
        HttpResponseMessage response = await _authed.GetAsync(
            $"{BaseUrl}/{NoMercyApiFactory.PlaylistId1}"
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_ReturnsUnauthorized_WhenAnonymous()
    {
        HttpResponseMessage response = await PostAsync(
            _unauthed,
            BaseUrl,
            new { name = "Anonymous Playlist" }
        );

        response
            .StatusCode.Should()
            .BeOneOf([HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden]);
    }

    [Fact]
    public async Task Create_ReturnsBadRequest_WhenNameMissing()
    {
        HttpResponseMessage response = await PostAsync(_authed, BaseUrl, new { });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Edit_ReturnsBadRequest_AndPreservesPlaylist_WhenNameIsBlank(string name)
    {
        Guid playlistId = await CreatePlaylistAsync("Original Name");

        HttpResponseMessage response = await PatchAsync(
            _authed,
            $"{BaseUrl}/{playlistId}",
            new { name, description = "Should not be saved" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using JsonDocument error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        error.RootElement.GetProperty("detail").GetString().Should().Be("Name is required");

        HttpResponseMessage afterEdit = await _authed.GetAsync($"{BaseUrl}/{playlistId}");
        afterEdit.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument playlist = JsonDocument.Parse(
            await afterEdit.Content.ReadAsStringAsync()
        );
        JsonElement data = playlist.RootElement.GetProperty("data");
        data.GetProperty("name").GetString().Should().Be("Original Name");
        data.GetProperty("description").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Edit_UpdatesDescription_WhenNameIsOmitted()
    {
        Guid playlistId = await CreatePlaylistAsync("Original Name");

        HttpResponseMessage response = await PatchAsync(
            _authed,
            $"{BaseUrl}/{playlistId}",
            new { description = "Updated description" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        HttpResponseMessage afterEdit = await _authed.GetAsync($"{BaseUrl}/{playlistId}");
        afterEdit.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument playlist = JsonDocument.Parse(
            await afterEdit.Content.ReadAsStringAsync()
        );
        JsonElement data = playlist.RootElement.GetProperty("data");
        data.GetProperty("name").GetString().Should().Be("Original Name");
        data.GetProperty("description").GetString().Should().Be("Updated description");
    }

    [Fact]
    public async Task Show_ReturnsNotFound_ForUnknownPlaylist()
    {
        HttpResponseMessage response = await _authed.GetAsync($"{BaseUrl}/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddItem_ReturnsBadRequest_ForInvalidKind()
    {
        Guid playlistId = await CreatePlaylistAsync("Invalid Kind Playlist");

        HttpResponseMessage response = await PostAsync(
            _authed,
            $"{BaseUrl}/{playlistId}/items",
            new { kind = "song", media_id = "129" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddItem_ReturnsBadRequest_ForTrackKind()
    {
        // "track" was a valid kind before this feature was made video-only —
        // it must now be rejected the same as any other unknown kind.
        Guid playlistId = await CreatePlaylistAsync("Track Kind Rejected Playlist");

        HttpResponseMessage response = await PostAsync(
            _authed,
            $"{BaseUrl}/{playlistId}/items",
            new { kind = "track", media_id = NoMercyApiFactory.TrackId1.ToString() }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddItem_ReturnsBadRequest_ForMalformedMediaId()
    {
        Guid playlistId = await CreatePlaylistAsync("Malformed Media Id Playlist");

        HttpResponseMessage response = await PostAsync(
            _authed,
            $"{BaseUrl}/{playlistId}/items",
            new { kind = "movie", media_id = "not-a-number" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_ForNonexistentMovie()
    {
        Guid playlistId = await CreatePlaylistAsync("Nonexistent Movie Playlist");

        HttpResponseMessage response = await PostAsync(
            _authed,
            $"{BaseUrl}/{playlistId}/items",
            new { kind = "movie", media_id = "999999" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddItem_ReturnsNotFound_WhenPlaylistDoesNotExist()
    {
        HttpResponseMessage response = await PostAsync(
            _authed,
            $"{BaseUrl}/{Guid.NewGuid()}/items",
            new { kind = "movie", media_id = "129" }
        );

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task FullLifecycle_CreateAddItemsGetEditReorderRemoveDelete()
    {
        Guid playlistId = await CreatePlaylistAsync("Video Playlist Test");

        // Add movie (playable), episode (playable), special — in that order.
        (
            await PostAsync(
                _authed,
                $"{BaseUrl}/{playlistId}/items",
                new { kind = "movie", media_id = "129" }
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        (
            await PostAsync(
                _authed,
                $"{BaseUrl}/{playlistId}/items",
                new { kind = "episode", media_id = "62085" }
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        (
            await PostAsync(
                _authed,
                $"{BaseUrl}/{playlistId}/items",
                new { kind = "special", media_id = NoMercyApiFactory.FavoriteSpecialId.ToString() }
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);

        HttpResponseMessage detail = await _authed.GetAsync($"{BaseUrl}/{playlistId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument detailDoc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        JsonElement data = detailDoc.RootElement.GetProperty("data");
        data.GetProperty("id").GetGuid().Should().Be(playlistId);

        JsonElement[] items = [.. data.GetProperty("items").EnumerateArray()];
        items.Should().HaveCount(3);

        items
            .Select(i => i.GetProperty("kind").GetString())
            .Should()
            .Equal(["movie", "episode", "special"]);

        JsonElement movieItem = items[0];
        movieItem.GetProperty("media_id").GetString().Should().Be("129");
        movieItem.GetProperty("title").GetString().Should().Be("Spirited Away");
        movieItem.GetProperty("play_link").GetString().Should().Be("/movie/129/watch");

        JsonElement episodeItem = items[1];
        episodeItem.GetProperty("media_id").GetString().Should().Be("62085");
        episodeItem
            .GetProperty("play_link")
            .GetString()
            .Should()
            .Be("/tv/1399/watch?season=1&episode=1");

        JsonElement specialItem = items[2];
        specialItem
            .GetProperty("media_id")
            .GetString()
            .Should()
            .Be(NoMercyApiFactory.FavoriteSpecialId.ToString());
        specialItem.GetProperty("title").GetString().Should().Be("Test Special");
        specialItem
            .GetProperty("play_link")
            .GetString()
            .Should()
            .Be($"/specials/{NoMercyApiFactory.FavoriteSpecialId}/watch");

        string movieItemId = movieItem.GetProperty("id").GetString()!;
        string episodeItemId = episodeItem.GetProperty("id").GetString()!;
        string specialItemId = specialItem.GetProperty("id").GetString()!;

        // Edit metadata (partial update — only name).
        HttpResponseMessage edit = await PatchAsync(
            _authed,
            $"{BaseUrl}/{playlistId}",
            new { name = "Renamed Video Playlist" }
        );
        edit.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage afterEdit = await _authed.GetAsync($"{BaseUrl}/{playlistId}");
        using JsonDocument afterEditDoc = JsonDocument.Parse(
            await afterEdit.Content.ReadAsStringAsync()
        );
        afterEditDoc
            .RootElement.GetProperty("data")
            .GetProperty("name")
            .GetString()
            .Should()
            .Be("Renamed Video Playlist");

        // Reorder: special, episode, movie.
        HttpResponseMessage reorder = await PutAsync(
            _authed,
            $"{BaseUrl}/{playlistId}/items/order",
            new { ordered_item_ids = new[] { specialItemId, episodeItemId, movieItemId } }
        );
        reorder.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage afterReorder = await _authed.GetAsync($"{BaseUrl}/{playlistId}");
        using JsonDocument afterReorderDoc = JsonDocument.Parse(
            await afterReorder.Content.ReadAsStringAsync()
        );
        afterReorderDoc
            .RootElement.GetProperty("data")
            .GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("kind").GetString())
            .Should()
            .Equal(["special", "episode", "movie"]);

        // Remove the special item.
        HttpResponseMessage remove = await _authed.DeleteAsync(
            $"{BaseUrl}/{playlistId}/items/{specialItemId}"
        );
        remove.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage afterRemove = await _authed.GetAsync($"{BaseUrl}/{playlistId}");
        using JsonDocument afterRemoveDoc = JsonDocument.Parse(
            await afterRemove.Content.ReadAsStringAsync()
        );
        afterRemoveDoc
            .RootElement.GetProperty("data")
            .GetProperty("items")
            .GetArrayLength()
            .Should()
            .Be(2);

        // Delete the whole playlist — cascades its remaining items.
        HttpResponseMessage delete = await _authed.DeleteAsync($"{BaseUrl}/{playlistId}");
        delete.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage afterDelete = await _authed.GetAsync($"{BaseUrl}/{playlistId}");
        afterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OwnershipIsolation_SecondaryUser_GetsNotFound_OnPrimaryUsersPlaylist()
    {
        Guid playlistId = await CreatePlaylistAsync("Owner-Only Playlist");

        (await _secondaryUser.GetAsync($"{BaseUrl}/{playlistId}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        (await PatchAsync(_secondaryUser, $"{BaseUrl}/{playlistId}", new { name = "Hijacked" }))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        (
            await PostAsync(
                _secondaryUser,
                $"{BaseUrl}/{playlistId}/items",
                new { kind = "movie", media_id = "129" }
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        (
            await PutAsync(
                _secondaryUser,
                $"{BaseUrl}/{playlistId}/items/order",
                new { ordered_item_ids = Array.Empty<string>() }
            )
        )
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        (await _secondaryUser.DeleteAsync($"{BaseUrl}/{playlistId}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.NotFound);

        // None of the secondary user's rejected mutation attempts touched it —
        // the owner can still see it afterward.
        (await _authed.GetAsync($"{BaseUrl}/{playlistId}"))
            .StatusCode.Should()
            .Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Index_SecondaryUser_DoesNotSeePrimaryUsersPlaylist()
    {
        Guid playlistId = await CreatePlaylistAsync("Primary-Only Playlist For Index Isolation");

        HttpResponseMessage response = await _secondaryUser.GetAsync(BaseUrl);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using JsonDocument doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("data")
            .EnumerateArray()
            .Select(p => p.GetProperty("id").GetString())
            .Should()
            .NotContain(playlistId.ToString());
    }
}
