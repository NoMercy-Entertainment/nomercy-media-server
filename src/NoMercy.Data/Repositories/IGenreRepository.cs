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

using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Repositories;

public interface IGenreRepository
{
    Task<Genre?> GetGenreAsync(
        Guid userId,
        int id,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<(
        GenreDetailDto? Genre,
        List<HomeMovieCardDto> Movies,
        List<HomeTvCardDto> TvShows
    )> GetGenreCardsAsync(
        Guid userId,
        int id,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<List<Genre>> GetGenres(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<List<GenreWithCountsDto>> GetGenresWithCountsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<List<MusicGenre>> GetMusicGenresAsync(Guid userId, CancellationToken ct = default);

    Task<List<MusicGenreCardDto>> GetMusicGenreCardsAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<MusicGenre>> GetPaginatedMusicGenresAsync(
        Guid userId,
        string letter,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<List<MusicGenreCardDto>> GetPaginatedMusicGenreCardsAsync(
        Guid userId,
        string letter,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<MusicGenre?> GetMusicGenreAsync(Guid userId, Guid genreId, CancellationToken ct = default);

    /// <summary>The covers the genre's card shows, so its page shows the same.</summary>
    Task<GroupPoster[]> GetMusicGenreCoversAsync(
        Guid userId,
        Guid genreId,
        CancellationToken ct = default
    );
}
