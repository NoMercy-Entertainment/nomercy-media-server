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

using NoMercy.Data.DTOs.Specials;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Data.Repositories;

public interface ISpecialRepository
{
    Task<List<Special>> GetSpecialsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<List<SpecialCardDto>> GetSpecialCardsAsync(
        Guid userId,
        string language,
        int take,
        int page,
        CancellationToken ct = default
    );

    Task<List<SpecialCardDto>> GetSpecialItemCardsAsync(
        Guid userId,
        string language,
        string country,
        int take = 1,
        int page = 0,
        CancellationToken ct = default
    );

    Task<SpecialDetailDto?> GetSpecialDetailAsync(
        Guid userId,
        Ulid id,
        CancellationToken ct = default
    );

    Task<Special?> GetSpecialAsync(Guid userId, Ulid id, CancellationToken ct = default);

    Task<Special?> GetSpecialAvailableAsync(Guid userId, Ulid id);

    Task<List<Special>> GetSpecialItems(
        Guid userId,
        string? language,
        string country,
        int take = 1,
        int page = 0,
        CancellationToken ct = default
    );

    Task<Special?> GetSpecialPlaylistAsync(
        Guid userId,
        Ulid id,
        string language,
        string country,
        CancellationToken ct = default
    );

    Task<bool> AddToWatchListAsync(
        Ulid specialId,
        Guid userId,
        bool add = true,
        CancellationToken ct = default
    );

    Task<SpecialItemProjections> GetSpecialItemProjectionsAsync(
        Guid userId,
        IEnumerable<int> movieIds,
        IEnumerable<int> tvIds,
        string country,
        CancellationToken ct = default
    );

    Task<Special?> LikeSpecialAsync(
        Ulid id,
        Guid userId,
        bool like,
        CancellationToken ct = default
    );

    // --- Dashboard / admin methods ---

    Task<List<Special>> GetAllSpecialsAdminAsync(CancellationToken ct = default);

    Task<Special> CreateSpecialAsync(Guid userId, CancellationToken ct = default);

    Task<Special?> GetSpecialWithTvAsync(Guid userId, Ulid id, CancellationToken ct = default);
    
    Task<Special?> GetSpecialByIdAsync(Ulid id, CancellationToken ct = default);
    
    Task<Special?> UpdateSpecialAsync(
        Ulid id,
        string? title,
        string? overview,
        string? poster,
        string? backdrop,
        string? logo,
        CancellationToken ct = default
    );

    Task<Special?> DeleteSpecialAsync(Ulid id, CancellationToken ct = default);

    Task<List<Special>> GetAllSpecialsSortableAsync(CancellationToken ct = default);

    Task ReorderSpecialsAsync(List<SpecialOrder> orders, CancellationToken ct = default);

    Task<List<Special>> GetAllSpecialsForRescanAsync(CancellationToken ct = default);

    Task<List<SpecialItem>> GetSpecialItemsAdminAsync(Ulid id, CancellationToken ct = default);

    Task<bool> ReplaceSpecialItemsAsync(
        Ulid id,
        List<SpecialItemReplacement> items,
        CancellationToken ct = default
    );

    Task<List<Movie>> SearchMoviesAsync(string query, int take, CancellationToken ct = default);

    Task<List<Episode>> SearchEpisodesAsync(string query, int take, CancellationToken ct = default);
}

public record SpecialItemProjections(
    List<SpecialMovieProjection> Movies,
    List<SpecialTvProjection> Tvs
);

public record SpecialItemReplacement(string MediaType, int MediaId, int Order);

public record SpecialOrder(Ulid Id, int Order);
