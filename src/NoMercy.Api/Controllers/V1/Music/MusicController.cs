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
using NoMercy.Api.DTOs.Common;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Authorization;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services.Music;
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

        List<ComponentEnvelope> favorites = [];
        if (request.Version != "lolomo")
        {
            AddFavorite(
                favorites,
                data.TopArtist,
                "favorite-artist",
                "Most listened artist".Localize()
            );
            AddFavorite(
                favorites,
                data.TopAlbum,
                "favorite-album",
                "Most listened album".Localize()
            );
            AddFavorite(
                favorites,
                data.TopPlaylist,
                "favorite-playlist",
                "Most listened playlist".Localize()
            );
        }

        List<ComponentEnvelope> items =
        [
            Component.Container().WithItems(favorites),
            MusicCarousel(
                "favorite-artists",
                "Favorite Artists".Localize(),
                "",
                "favorite-albums",
                null,
                data.FavoriteArtists.Select(item => new MusicCardData(item))
            ),
            MusicCarousel(
                "favorite-albums",
                "Favorite Albums".Localize(),
                "favorite-artists",
                "playlists",
                null,
                data.FavoriteAlbums.Select(item => new MusicCardData(item))
            ),
            MusicCarousel(
                "playlists",
                "Playlists".Localize(),
                "favorite-albums",
                "artists",
                "/music/playlists",
                data.Playlists.Select(item => new MusicCardData(item))
            ),
            MusicCarousel(
                "artists",
                "Artists".Localize(),
                "playlists",
                "albums",
                "/music/artists/letter/_",
                data.LatestArtists.Select(item => new MusicCardData(item))
            ),
            MusicCarousel(
                "albums",
                "Albums".Localize(),
                "artists",
                "genres",
                "/music/albums/letter/_",
                data.LatestAlbums.Select(item => new MusicCardData(item))
            ),
            MusicCarousel(
                "genres",
                "Genres".Localize(),
                "albums",
                null,
                "/music/genres/letter/_",
                data.LatestGenres.Select(item => new MusicCardData(item))
            ),
        ];

        return Ok(ComponentResponse.From(items));
    }

    private static void AddFavorite(
        List<ComponentEnvelope> favorites,
        TopMusicItemDto? top,
        string id,
        string title
    )
    {
        if (top is null)
            return;

        favorites.Add(
            Component.MusicHomeCard(new(new TopMusicDto(top))).WithId(id).WithTitle(title)
        );
    }

    private static ContainerComponentBuilder MusicCarousel(
        string id,
        string title,
        string previousId,
        string? nextId,
        string? moreLink,
        IEnumerable<MusicCardData> cards
    ) =>
        Component
            .Carousel()
            .WithId(id)
            .WithTitle(title)
            .WithMoreLink(moreLink)
            .WithNavigation(previousId, nextId)
            .WithItems(cards.Select(Component.MusicCard));

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

        MusicSearchCards cards = await MusicSearch.FindCardsAsync(
            _musicRepository,
            normalizedQuery,
            userId,
            country
        );
        if (cards.IsEmpty)
            return NotFoundResponse("No results found");

        List<ArtistCardDto> artists = cards.Artists;
        List<AlbumCardDto> albums = cards.Albums;
        List<PlaylistCardDto> playlistCards = cards.Playlists;
        List<SearchTrackCardDto> tracks = cards.Tracks;

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
