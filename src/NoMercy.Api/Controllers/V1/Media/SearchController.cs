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
using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Api.DTOs.Music;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.Controllers.V1.Media;

[ApiController]
[Tags("Media Search")]
[ApiVersion(1.0)]
[Authorize(Policy = "MediaAccess")]
[Route("api/v{version:apiVersion}/search")]
public class SearchController : BaseController
{
    private readonly IMusicRepository _musicRepository;
    private readonly ILibraryRepository _libraryRepository;

    public SearchController(IMusicRepository musicService, ILibraryRepository libraryRepository)
    {
        _musicRepository = musicService;
        _libraryRepository = libraryRepository;
    }

    [HttpGet("music")]
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> SearchMusic(
        [FromQuery] SearchQueryRequest request,
        CancellationToken ct = default
    )
    {
        string country = Country();
        string normalizedQuery = request.Query.NormalizeSearch();

        (List<Artist> artists, List<Album> albums, List<Playlist> playlists, List<Track> songs) =
            await FetchMusicSearchResultsAsync(normalizedQuery, ct);

        Track? topTrack = songs.FirstOrDefault();
        Artist? topArtist = artists.FirstOrDefault();
        Album? topAlbum = albums.FirstOrDefault();

        TopResultCardData? topResultData = TopResultCardData.FirstOf(topTrack, topArtist, topAlbum);

        List<TrackRowData> songResults =
        [
            .. songs.Take(6).Select(track => new TrackRowData(track)),
        ];

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
                            .WithProperties(
                                new()
                                {
                                    { "paddingTop", 0 },
                                    { "paddingBottom", 0 },
                                    { "paddingStart", 0 },
                                    { "paddingEnd", 0 },
                                }
                            )
                            .WithTitle("Tracks".Localize())
                            .WithItems(
                                songResults.Select(track =>
                                    Component
                                        .TrackRow(track)
                                        .WithProperties(
                                            new()
                                            {
                                                { "paddingTop", 0 },
                                                { "paddingBottom", 0 },
                                                { "paddingStart", 0 },
                                                { "paddingEnd", 0 },
                                            }
                                        )
                                        .WithDisplayList(songResults)
                                )
                            ),
                    ]),
                Component
                    .Carousel()
                    .WithId("artists")
                    .WithTitle("Artist".Localize())
                    .WithItems(
                        artists
                            .GroupBy(artist => artist.Id)
                            .Select(group => group.First())
                            .Select(item => Component.MusicCard(new ArtistsResponseItemDto(item)))
                    ),
                Component
                    .Carousel()
                    .WithId("albums")
                    .WithTitle("Albums".Localize())
                    .WithItems(
                        albums
                            .GroupBy(album => album.Id)
                            .Select(group => group.First())
                            .Select(item => Component.MusicCard(new ArtistsResponseItemDto(item)))
                    ),
                Component
                    .Carousel()
                    .WithId("playlists")
                    .WithTitle("Playlists".Localize())
                    .WithItems(
                        playlists
                            .GroupBy(playlist => playlist.Id)
                            .Select(group => group.First())
                            .Select(item => Component.MusicCard(new PlaylistResponseItemDto(item)))
                    ),
            ])
        );
    }

    [HttpGet("music/tv")]
    public async Task<IActionResult> SearchTvMusic(
        [FromQuery] SearchQueryRequest request,
        CancellationToken ct = default
    )
    {
        string normalizedQuery = request.Query.NormalizeSearch();

        (List<Artist> artists, List<Album> albums, List<Playlist> _, List<Track> _) =
            await FetchMusicSearchResultsAsync(normalizedQuery, ct);

        List<ComponentEnvelope> musicCards =
        [
            .. artists
                .GroupBy(artist => artist.Id)
                .Select(group => group.First())
                .OrderBy(artist => artist.Name)
                .Select(item => Component.MusicCard(new ArtistsResponseItemDto(item))),
            .. albums
                .GroupBy(album => album.Id)
                .Select(group => group.First())
                .OrderBy(album => album.Name)
                .Select(item => Component.MusicCard(new AlbumsResponseItemDto(item))),
        ];

        return Ok(
            ComponentResponse.From(
                Component
                    .Grid()
                    .WithId("tv-music-search")
                    .WithProperties(new() { { "columns", 4 }, { "spacing", 16 } })
                    .WithItems(musicCards)
                    .Build()
            )
        );
    }

    [HttpGet("video")]
    [ResponseCache(NoStore = true)]
    public async Task<IActionResult> SearchVideo(
        [FromQuery] SearchQueryRequest request,
        CancellationToken ct = default
    )
    {
        string country = Country();
        string normalizedQuery = request.Query.NormalizeSearch();

        return Ok(
            ComponentResponse.From(await BuildVideoSearchGridAsync(normalizedQuery, country, ct))
        );
    }

    [HttpGet("video/tv")]
    public Task<IActionResult> SearchTvVideo(
        [FromQuery] SearchQueryRequest request,
        CancellationToken ct = default
    ) => SearchVideo(request, ct);

    private async Task<(
        List<Artist> Artists,
        List<Album> Albums,
        List<Playlist> Playlists,
        List<Track> Songs
    )> FetchMusicSearchResultsAsync(string normalizedQuery, CancellationToken ct)
    {
        // Step 1: Get IDs sequentially (MusicRepository uses a single scoped DbContext, not thread-safe).
        // Cap each category: a broad query otherwise fans thousands of full entity graphs through
        // SearchMusicFullDataAsync. Only the top result, six tracks, and the carousels render.
        const int resultCap = UiLimits.SearchResultsPerCategory;
        List<Guid> artistIds =
        [
            .. (await _musicRepository.SearchArtistIdsAsync(normalizedQuery, ct)).Take(resultCap),
        ];
        List<Guid> albumIds =
        [
            .. (await _musicRepository.SearchAlbumIdsAsync(normalizedQuery, ct)).Take(resultCap),
        ];
        List<Guid> playlistIds =
        [
            .. (await _musicRepository.SearchPlaylistIdsAsync(normalizedQuery, ct)).Take(resultCap),
        ];
        List<Guid> trackIds =
        [
            .. (await _musicRepository.SearchTrackIdsAsync(normalizedQuery, ct)).Take(resultCap),
        ];

        // Step 2: Query full data using the IDs in parallel (repository owns the fan-out)
        MusicSearchFullData fullData = await _musicRepository.SearchMusicFullDataAsync(
            artistIds,
            albumIds,
            playlistIds,
            trackIds,
            ct
        );

        List<Artist> artists = fullData.Artists;
        List<Album> albums = fullData.Albums;
        List<Playlist> playlists = fullData.Playlists;
        List<Track> songs = fullData.Songs;

        if (albums.Count > 0)
            foreach (Album album in albums)
                if (album.AlbumTrack.Count > 0)
                    foreach (
                        IEnumerable<Artist> artist in album
                            .AlbumTrack.Select(albumTrack =>
                                albumTrack.Track.ArtistTrack.Select(artistTrack =>
                                    artistTrack.Artist
                                )
                            )
                            .ToList()
                    )
                        artists.AddRange(artist);

        if (playlists.Count > 0)
            foreach (Playlist playlist in playlists)
                if (playlist.Tracks.Count > 0)
                    foreach (
                        IEnumerable<Artist> artist in playlist
                            .Tracks.Select(playlistTrack =>
                                playlistTrack.Track.ArtistTrack.Select(artistTrack =>
                                    artistTrack.Artist
                                )
                            )
                            .ToList()
                    )
                        artists.AddRange(artist);

        if (songs.Count > 0)
            foreach (Track song in songs)
            {
                if (song.ArtistTrack.Count > 0)
                    artists.AddRange(song.ArtistTrack.Select(artistTrack => artistTrack.Artist));
                if (song.AlbumTrack.Count > 0)
                    albums.AddRange(song.AlbumTrack.Select(albumTrack => albumTrack.Album));
            }

        return (artists, albums, playlists, songs);
    }

    private async Task<ComponentEnvelope> BuildVideoSearchGridAsync(
        string normalizedQuery,
        string country,
        CancellationToken ct
    )
    {
        VideoSearchResults videoResults = await _libraryRepository.SearchVideoByTitleAsync(
            normalizedQuery,
            ct
        );

        List<CardData> cardItems =
        [
            .. videoResults
                .Tvs.Concat<dynamic>(videoResults.Movies)
                .OrderBy(item => item is Tv tv ? tv.Title : ((Movie)item).Title)
                .Select(item => new CardData(item, country)),
        ];

        return Component
            .Grid()
            .WithItems(cardItems.Select(item => Component.Card().WithData(item).Build()))
            .Build();
    }

    [NotMapped]
    public class SearchQueryRequest
    {
        [JsonProperty("query")]
        public string Query { get; set; } = string.Empty;

        [JsonProperty("type")]
        public string? Type { get; set; }
    }
}
