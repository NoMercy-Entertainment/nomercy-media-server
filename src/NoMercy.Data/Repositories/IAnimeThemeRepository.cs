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

namespace NoMercy.Data.Repositories;

public interface IAnimeThemeRepository
{
    /// <summary>Whether any movie or show carries an anime theme.</summary>
    Task<bool> AnyThemedTitlesAsync(CancellationToken ct = default);

    Task<List<AnimeThemeWithCountsDto>> GetThemesWithCountsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    );

    /// <summary>The ids of the themes a carousel shows first, in that order.</summary>
    Task<List<int>> GetTopThemeIdsAsync(
        Guid userId,
        AnimeCarouselOrder order,
        DateOnly today,
        int take,
        CancellationToken ct = default
    );

    /// <summary>The cards of the given themes in the order of the ids; hidden themes are left out.</summary>
    Task<List<AnimeThemeWithCountsDto>> GetThemesWithCountsByIdsAsync(
        Guid userId,
        string language,
        IReadOnlyList<int> ids,
        CancellationToken ct = default
    );

    Task<(
        AnimeThemeDetailDto? Theme,
        List<HomeMovieCardDto> Movies,
        List<HomeTvCardDto> TvShows
    )> GetThemeCardsAsync(
        Guid userId,
        int id,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    );
}
