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

using NoMercy.Api.DTOs.Media;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Api.Services.Video;

public class VideoPlaylistManager
{
    private readonly IMovieRepository _movieRepository;
    private readonly ITvShowRepository _tvShowRepository;
    private readonly ICollectionRepository _collectionRepository;
    private readonly ISpecialRepository _specialRepository;

    public VideoPlaylistManager(
        IMovieRepository movieRepository,
        ICollectionRepository collectionRepository,
        ISpecialRepository specialRepository,
        ITvShowRepository tvShowRepository
    )
    {
        _movieRepository = movieRepository;
        _tvShowRepository = tvShowRepository;
        _collectionRepository = collectionRepository;
        _specialRepository = specialRepository;
    }

    public async Task<(
        VideoPlaylistResponseDto? item,
        List<VideoPlaylistResponseDto> playlist
    )> GetPlaylist(
        Guid userId,
        string type,
        dynamic listId,
        int? itemId,
        string language,
        string country
    )
    {
        return type switch
        {
            MediaTypes.SpecialMediaType => await GetSpecialItems(
                userId,
                listId,
                itemId,
                language,
                country
            ),
            MediaTypes.CollectionMediaType => await GetCollectionItems(
                userId,
                listId,
                itemId,
                language,
                country
            ),
            MediaTypes.TvMediaType => await GetTvItems(userId, listId, itemId, language, country),
            MediaTypes.MovieMediaType => await GetMovieItems(
                userId,
                listId,
                itemId,
                language,
                country
            ),
            _ => throw new ArgumentException("Invalid playlist type", nameof(type)),
        };
    }

    private async Task<(
        VideoPlaylistResponseDto? item,
        List<VideoPlaylistResponseDto> playlist
    )> GetSpecialItems(Guid userId, dynamic listId, int? itemId, string language, string country)
    {
        Special? special = await _specialRepository.GetSpecialPlaylistAsync(
            userId,
            Ulid.Parse(listId),
            language,
            country
        );

        List<VideoPlaylistResponseDto> playlist =
            special
                ?.Items.OrderBy(item => item.Order)
                .Select(
                    (item, index) =>
                        item.EpisodeId is not null
                            ? (VideoPlaylistResponseDto?)
                                VideoPlaylistResponseDto.TryCreate(
                                    item.Episode ?? new Episode(),
                                    MediaTypes.SpecialMediaType,
                                    listId,
                                    country,
                                    index
                                )
                            : (VideoPlaylistResponseDto?)
                                VideoPlaylistResponseDto.TryCreate(
                                    item.Movie ?? new Movie(),
                                    MediaTypes.SpecialMediaType,
                                    listId,
                                    country,
                                    index
                                )
                )
                .Where(dto => dto is not null)
                .Select(dto => dto!)
                .ToList()
            ?? [];

        VideoPlaylistResponseDto? item = playlist.FirstOrDefault(p => p.Id == itemId);

        if (item is null && playlist.Any(p => p.Progress?.Date is not null))
        {
            item = playlist.OrderByDescending(p => p.Progress?.Date).FirstOrDefault();
        }
        if (item is null && playlist.Count != 0)
        {
            item = playlist.FirstOrDefault();
        }

        return (item, playlist);
    }

    private async Task<(
        VideoPlaylistResponseDto? item,
        List<VideoPlaylistResponseDto> playlist
    )> GetCollectionItems(Guid userId, dynamic listId, int? itemId, string language, string country)
    {
        Collection? collection = await _collectionRepository.GetCollectionPlaylistAsync(
            userId,
            int.Parse(listId),
            language,
            country
        );

        List<VideoPlaylistResponseDto> playlist =
            collection
                ?.CollectionMovies.Where(movie => movie.Movie.VideoFiles.Any(v => v.Folder != null))
                .Select(
                    (movie, index) =>
                        (VideoPlaylistResponseDto?)
                            VideoPlaylistResponseDto.TryCreate(
                                movie.Movie,
                                MediaTypes.CollectionMediaType,
                                listId,
                                country,
                                index + 1,
                                collection
                            )
                )
                .Where(dto => dto is not null)
                .Select(dto => dto!)
                .ToList()
            ?? [];

        VideoPlaylistResponseDto? item = playlist.FirstOrDefault(p => p.Id == itemId);

        if (item is null && playlist.Any(p => p.Progress?.Date is not null))
        {
            item = playlist.OrderByDescending(p => p.Progress?.Date).FirstOrDefault();
        }
        if (item is null && playlist.Count != 0)
        {
            item = playlist.FirstOrDefault();
        }

        return (item, playlist);
    }

    private async Task<(
        VideoPlaylistResponseDto? item,
        List<VideoPlaylistResponseDto> playlist
    )> GetTvItems(Guid userId, dynamic listId, int? itemId, string language, string country)
    {
        Tv? tv = await _tvShowRepository.GetPlaylistAsync(
            userId,
            int.Parse(listId),
            language,
            country
        );

        VideoPlaylistResponseDto[] episodes =
            tv?.Seasons.Where(season => season.SeasonNumber > 0)
                .SelectMany(season => season.Episodes)
                .Select(episode =>
                    (VideoPlaylistResponseDto?)
                        VideoPlaylistResponseDto.TryCreate(
                            episode,
                            MediaTypes.TvMediaType,
                            listId,
                            country
                        )
                )
                .Where(dto => dto is not null)
                .Select(dto => dto!)
                .ToArray()
            ?? [];

        VideoPlaylistResponseDto[] extras =
            tv?.Seasons.Where(season => season.SeasonNumber == 0)
                .SelectMany(season => season.Episodes)
                .Select(episode =>
                    (VideoPlaylistResponseDto?)
                        VideoPlaylistResponseDto.TryCreate(
                            episode,
                            MediaTypes.TvMediaType,
                            listId,
                            country
                        )
                )
                .Where(dto => dto is not null)
                .Select(dto => dto!)
                .ToArray()
            ?? [];

        List<VideoPlaylistResponseDto> playlist = episodes.Concat(extras).ToList();

        VideoPlaylistResponseDto? item = playlist.FirstOrDefault(p => p.Id == itemId);

        if (item is null && playlist.Any(p => p.Progress?.Date is not null))
        {
            item = playlist.OrderByDescending(p => p.Progress?.Date).FirstOrDefault();
        }
        if (item is null && playlist.Count != 0)
        {
            item = playlist.FirstOrDefault();
        }

        return (item, playlist);
    }

    private async Task<(
        VideoPlaylistResponseDto? item,
        List<VideoPlaylistResponseDto> playlist
    )> GetMovieItems(Guid userId, dynamic listId, int? itemId, string language, string country)
    {
        List<Movie> movies = await _movieRepository.GetMoviePlaylistAsync(
            userId,
            int.Parse(listId),
            language,
            country
        );
        List<VideoPlaylistResponseDto> playlist = movies
            .Select(movie =>
                (VideoPlaylistResponseDto?)
                    VideoPlaylistResponseDto.TryCreate(
                        movie,
                        MediaTypes.MovieMediaType,
                        int.Parse(listId),
                        country
                    )
            )
            .Where(dto => dto is not null)
            .Select(dto => dto!)
            .ToList();

        VideoPlaylistResponseDto? item =
            playlist.FirstOrDefault(p => p.Id == itemId) ?? playlist.FirstOrDefault();

        return (item, playlist);
    }
}
