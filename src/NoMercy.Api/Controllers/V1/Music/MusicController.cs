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

using System.ComponentModel.DataAnnotations.Schema;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Music;

[ApiController]
[ApiVersion(1.0)]
[Tags("Music")]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/music")]
public class MusicController : BaseController
{
    private readonly IMusicRepository _musicRepository;

    public MusicController(IMusicRepository musicService)
    {
        _musicRepository = musicService;
    }

    [HttpGet]
    [Route("")]
    [Route("start")]
    public async Task<IActionResult> Index([FromQuery] PageRequestDto request)
    {
        Guid userId = User.UserId();

        // Run 3 groups of 3 queries in parallel using separate DbContext instances
        MusicStartPageData data = await _musicRepository.GetMusicStartPageAsync(userId);

        List<ComponentEnvelope> items = [];
        List<ComponentEnvelope> items2 = [];

        // Add favorite home cards
        if (data.TopArtist is not null && request.Version != "lolomo")
        {
            TopMusicDto favoriteArtist = new(data.TopArtist);
            items2.Add(
                Component
                    .MusicHomeCard(new(favoriteArtist))
                    .WithId("favorite-artist")
                    .WithTitle("Most listened artist".Localize())
            );
        }

        if (data.TopAlbum is not null && request.Version != "lolomo")
        {
            TopMusicDto favoriteAlbum = new(data.TopAlbum);
            items2.Add(
                Component
                    .MusicHomeCard(new(favoriteAlbum))
                    .WithId("favorite-album")
                    .WithTitle("Most listened album".Localize())
            );
        }

        if (data.TopPlaylist is not null && request.Version != "lolomo")
        {
            TopMusicDto favoritePlaylist = new(data.TopPlaylist);
            items2.Add(
                Component
                    .MusicHomeCard(new(favoritePlaylist))
                    .WithId("favorite-playlist")
                    .WithTitle("Most listened playlist".Localize())
            );
        }

        items.Add(Component.Container().WithItems(items2));

        // Add carousels
        items.Add(
            Component
                .Carousel()
                .WithId("favorite-artists")
                .WithTitle("Favorite Artists".Localize())
                .WithNavigation("", "favorite-albums")
                .WithItems(
                    data.FavoriteArtists.Select(item =>
                        Component.MusicCard(new MusicCardData(item))
                    )
                )
        );

        items.Add(
            Component
                .Carousel()
                .WithId("favorite-albums")
                .WithTitle("Favorite Albums".Localize())
                .WithNavigation("favorite-artists", "playlists")
                .WithItems(
                    data.FavoriteAlbums.Select(item => Component.MusicCard(new MusicCardData(item)))
                )
        );

        items.Add(
            Component
                .Carousel()
                .WithId("playlists")
                .WithTitle("Playlists".Localize())
                .WithMoreLink("/music/playlists")
                .WithNavigation("favorite-albums", "artists")
                .WithItems(
                    data.Playlists.Select(item => Component.MusicCard(new MusicCardData(item)))
                )
        );

        items.Add(
            Component
                .Carousel()
                .WithId("artists")
                .WithTitle("Artists".Localize())
                .WithMoreLink("/music/artists/letter/_")
                .WithNavigation("playlists", "albums")
                .WithItems(
                    data.LatestArtists.Select(item => Component.MusicCard(new MusicCardData(item)))
                )
        );

        items.Add(
            Component
                .Carousel()
                .WithId("albums")
                .WithTitle("Albums".Localize())
                .WithMoreLink("/music/albums/letter/_")
                .WithNavigation("artists", "genres")
                .WithItems(
                    data.LatestAlbums.Select(item => Component.MusicCard(new MusicCardData(item)))
                )
        );

        items.Add(
            Component
                .Carousel()
                .WithId("genres")
                .WithTitle("Genres".Localize())
                .WithMoreLink("/music/genres/letter/_")
                .WithNavigation("albums")
                .WithItems(
                    data.LatestGenres.Select(item => Component.MusicCard(new MusicCardData(item)))
                )
        );

        return Ok(ComponentResponse.From(items));
    }

    [HttpPost]
    [Route("start/favorites")]
    public async Task<IActionResult> Favorites()
    {
        Guid userId = User.UserId();

        TopMusicItemDto? topArtist = await _musicRepository.GetTopArtistAsync(userId);
        TopMusicItemDto? topAlbum = await _musicRepository.GetTopAlbumAsync(userId);
        TopMusicItemDto? topPlaylist = await _musicRepository.GetTopPlaylistAsync(userId);

        List<ComponentEnvelope> favoriteItems = [];
        if (topArtist is not null)
            favoriteItems.Add(
                Component
                    .MusicHomeCard(new(new TopMusicDto(topArtist)))
                    .WithTitle("Most listened artist".Localize())
            );
        if (topAlbum is not null)
            favoriteItems.Add(
                Component
                    .MusicHomeCard(new(new TopMusicDto(topAlbum)))
                    .WithTitle("Most listened album".Localize())
            );
        if (topPlaylist is not null)
            favoriteItems.Add(
                Component
                    .MusicHomeCard(new(new TopMusicDto(topPlaylist)))
                    .WithTitle("Most listened playlist".Localize())
            );

        return Ok(
            ComponentResponse.From(
                Component
                    .Container()
                    .WithId("favorites")
                    .WithNavigation("favorites", "favorite-artists")
                    .WithUpdate("pageLoad", "/music/start/favorites")
                    .WithItems(favoriteItems)
            )
        );
    }

    [HttpPost]
    [Route("start/favorite-artists")]
    public async Task<IActionResult> FavoriteArtists([FromBody] CardRequestDto request)
    {
        Guid userId = User.UserId();

        List<ArtistCardDto> favoriteArtists = await _musicRepository.GetFavoriteArtistCardsAsync(
            userId
        );

        return Ok(
            ComponentResponse.From(
                Component
                    .Carousel()
                    .WithId("favorite-artists")
                    .WithNavigation("favorite-albums", "favorite-albums")
                    .WithTitle("Favorite Artists".Localize())
                    .WithUpdate("pageLoad", "/music/start/favorite-artists")
                    .WithReplacing(request.ReplaceId)
                    .WithItems(
                        favoriteArtists.Select(item => Component.MusicCard(new MusicCardData(item)))
                    )
            )
        );
    }

    [HttpPost]
    [Route("start/favorite-albums")]
    public async Task<IActionResult> FavoriteAlbums([FromBody] CardRequestDto request)
    {
        Guid userId = User.UserId();

        List<AlbumCardDto> favoriteAlbums = await _musicRepository.GetFavoriteAlbumCardsAsync(
            userId
        );

        return Ok(
            ComponentResponse.From(
                Component
                    .Carousel()
                    .WithId("favorite-albums")
                    .WithNavigation("favorite-artists", "playlists")
                    .WithTitle("Favorite Albums".Localize())
                    .WithUpdate("pageLoad", "/music/start/favorite-albums")
                    .WithReplacing(request.ReplaceId)
                    .WithItems(
                        favoriteAlbums.Select(item => Component.MusicCard(new MusicCardData(item)))
                    )
            )
        );
    }

    [HttpPost]
    [Route("start/playlists")]
    public async Task<IActionResult> Playlists([FromBody] CardRequestDto request)
    {
        Guid userId = User.UserId();

        List<PlaylistCardDto> playlists = await _musicRepository.GetPlaylistCardsAsync(userId);

        return Ok(
            ComponentResponse.From(
                Component
                    .Carousel()
                    .WithId("playlists")
                    .WithNavigation("favorite-albums", "artists")
                    .WithTitle("Playlists".Localize())
                    .WithMoreLink(new Uri("/music/start/playlists", UriKind.Relative))
                    .WithUpdate("pageLoad", "/music/start/playlists")
                    .WithReplacing(request.ReplaceId)
                    .WithItems(
                        playlists.Select(item => Component.MusicCard(new MusicCardData(item)))
                    )
            )
        );
    }

    [NotMapped]
    public class SearchQueryRequest
    {
        [JsonProperty("query")]
        public string Query { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string? Type { get; set; }
    }

    [HttpGet]
    [Route("search")]
    public async Task<IActionResult> Search([FromQuery] SearchQueryRequest request)
    {
        Guid userId = User.UserId();
        string country = Country();
        string normalizedQuery = request.Query.NormalizeSearch();

        // Bound every category. Without this a broad query fans thousands of matches
        // through cross-reference and card projection into a multi-MB payload, while
        // the view only renders the top result, six tracks, and the carousels. Capping
        // the id lists keeps the rendered first-N identical and drops only the tail.
        const int resultCap = UiLimits.SearchResultsPerCategory;

        // Step 1: Get IDs using search methods
        List<Guid> artistIds = (await _musicRepository.SearchArtistIdsAsync(normalizedQuery))
            .Take(resultCap)
            .ToList();
        List<Guid> albumIds = (await _musicRepository.SearchAlbumIdsAsync(normalizedQuery))
            .Take(resultCap)
            .ToList();
        List<Guid> playlistIds = (await _musicRepository.SearchPlaylistIdsAsync(normalizedQuery))
            .Take(resultCap)
            .ToList();
        List<Guid> trackIds = (await _musicRepository.SearchTrackIdsAsync(normalizedQuery))
            .Take(resultCap)
            .ToList();

        // Step 2: Cross-reference to find additional artists/albums
        List<Guid> additionalArtistIds = [];
        if (albumIds.Count > 0)
            additionalArtistIds.AddRange(
                await _musicRepository.GetArtistIdsFromAlbumsAsync(albumIds)
            );
        if (playlistIds.Count > 0)
            additionalArtistIds.AddRange(
                await _musicRepository.GetArtistIdsFromPlaylistTracksAsync(playlistIds)
            );
        if (trackIds.Count > 0)
            additionalArtistIds.AddRange(
                await _musicRepository.GetArtistIdsFromTracksAsync(trackIds)
            );

        List<Guid> allArtistIds = artistIds
            .Union(additionalArtistIds)
            .Distinct()
            .Take(resultCap)
            .ToList();

        List<Guid> additionalAlbumIds = [];
        if (trackIds.Count > 0)
            additionalAlbumIds.AddRange(
                await _musicRepository.GetAlbumIdsFromTracksAsync(trackIds)
            );

        List<Guid> allAlbumIds = albumIds
            .Union(additionalAlbumIds)
            .Distinct()
            .Take(resultCap)
            .ToList();

        // Step 3: Get projection data
        List<ArtistCardDto> artists =
            allArtistIds.Count > 0
                ? await _musicRepository.GetArtistCardsByIdsAsync(allArtistIds)
                : [];
        List<AlbumCardDto> albums =
            allAlbumIds.Count > 0
                ? await _musicRepository.GetAlbumCardsByIdsAsync(allAlbumIds)
                : [];
        List<PlaylistCardDto> playlistCards =
            playlistIds.Count > 0
                ? await _musicRepository.GetPlaylistCardsByIdsAsync(playlistIds)
                : [];
        List<SearchTrackCardDto> tracks =
            trackIds.Count > 0
                ? await _musicRepository.SearchTrackCardsAsync(trackIds, userId, country)
                : [];

        if (
            artists.Count == 0
            && albums.Count == 0
            && playlistCards.Count == 0
            && tracks.Count == 0
        )
            return NotFoundResponse("No results found");

        SearchTrackCardDto? topTrack = tracks.FirstOrDefault();
        ArtistCardDto? topArtist = artists.FirstOrDefault();
        AlbumCardDto? topAlbum = albums.FirstOrDefault();

        // Build TopResultCardData from the first match
        TopResultCardData? topResultData = TopResultCardData.FirstOf(topTrack, topArtist, topAlbum);

        List<TrackRowData> songResults = tracks
            .Take(6)
            .Select(track => new TrackRowData(track))
            .ToList();

        return Ok(
            ComponentResponse.From([
                Component
                    .Container()
                    .WithId("search-results")
                    .WithItems([
                        Component
                            .TopResultCard(topResultData!)
                            .WithId("top-result")
                            .WithTitle("Top Result".Localize())
                            .Build(),
                        Component
                            .List()
                            .WithId("tracks")
                            .WithTitle("Tracks".Localize())
                            .WithItems(
                                songResults.Select(track =>
                                    Component.TrackRow(track).WithDisplayList(songResults)
                                )
                            ),
                    ])
                    .Build(),
                Component
                    .Carousel()
                    .WithId("artists")
                    .WithTitle("Artist".Localize())
                    .WithItems(artists.Select(item => Component.MusicCard(new MusicCardData(item))))
                    .Build(),
                Component
                    .Carousel()
                    .WithId("albums")
                    .WithTitle("Albums".Localize())
                    .WithItems(albums.Select(item => Component.MusicCard(new MusicCardData(item))))
                    .Build(),
                Component
                    .Carousel()
                    .WithId("playlists")
                    .WithTitle("Playlists".Localize())
                    .WithItems(
                        playlistCards.Select(item => Component.MusicCard(new MusicCardData(item)))
                    ),
            ])
        );
    }

    [HttpPost]
    [Route("search/{query}/{Type}")]
    public IActionResult TypeSearch(string query, string type)
    {
        return Ok(new PlaceholderResponse { Data = [] });
    }
}
