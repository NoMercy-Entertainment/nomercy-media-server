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

public interface IAnimeDemographicRepository
{
    Task<List<AnimeDemographicWithCountsDto>> GetDemographicsWithCountsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    );

    /// <summary>The ids of the demographics a carousel shows first, in that order.</summary>
    Task<List<int>> GetTopDemographicIdsAsync(
        Guid userId,
        AnimeCarouselOrder order,
        DateOnly today,
        int take,
        CancellationToken ct = default
    );

    /// <summary>The cards of the given demographics in the order of the ids; hidden ones are left out.</summary>
    Task<List<AnimeDemographicWithCountsDto>> GetDemographicsWithCountsByIdsAsync(
        Guid userId,
        string language,
        IReadOnlyList<int> ids,
        CancellationToken ct = default
    );

    Task<(
        AnimeDemographicDetailDto? Demographic,
        List<HomeMovieCardDto> Movies,
        List<HomeTvCardDto> TvShows
    )> GetDemographicCardsAsync(
        Guid userId,
        int id,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default
    );
}
