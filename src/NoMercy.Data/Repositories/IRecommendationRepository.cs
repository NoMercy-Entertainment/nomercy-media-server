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

using NoMercy.Data.Services.Recommendations;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Data.Repositories;

public interface IRecommendationRepository
{
    Task<List<RecommendationCandidateDto>> GetUnownedMovieRecommendationsAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<RecommendationCandidateDto>> GetUnownedTvRecommendationsAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<RecommendationCandidateDto>> GetUnownedAnimeRecommendationsAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<RecommendationCandidateDto>> GetUnownedMovieSimilarAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<RecommendationCandidateDto>> GetUnownedTvSimilarAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<RecommendationCandidateDto>> GetUnownedAnimeSimilarAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<RecommendationCandidateDto>> GetKeywordCrossTypeTvCandidatesAsync(
        Guid userId,
        Dictionary<SourceKey, List<int>> movieKeywordMap,
        int minSharedKeywords = 3,
        int maxCandidates = 100,
        int maxKeywordFrequency = 50,
        CancellationToken ct = default
    );

    Task<List<RecommendationCandidateDto>> GetKeywordCrossTypeAnimeCandidatesAsync(
        Guid userId,
        Dictionary<SourceKey, List<int>> movieKeywordMap,
        int minSharedKeywords = 3,
        int maxCandidates = 100,
        int maxKeywordFrequency = 50,
        CancellationToken ct = default
    );

    Task<List<RecommendationCandidateDto>> GetKeywordCrossTypeMovieCandidatesAsync(
        Guid userId,
        Dictionary<SourceKey, List<int>> tvKeywordMap,
        int minSharedKeywords = 3,
        int maxCandidates = 100,
        int maxKeywordFrequency = 50,
        CancellationToken ct = default
    );

    Task<List<UserAffinitySourceDto>> GetUserMovieAffinityDataAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<UserAffinitySourceDto>> GetUserTvAffinityDataAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<List<UserAffinitySourceDto>> GetUserAnimeAffinityDataAsync(
        Guid userId,
        CancellationToken ct = default
    );

    Task<Dictionary<int, List<int>>> GetGenresForMovieIdsAsync(
        List<int> movieIds,
        CancellationToken ct = default
    );

    Task<Dictionary<int, List<int>>> GetGenresForTvIdsAsync(
        List<int> tvIds,
        CancellationToken ct = default
    );

    Task<(List<Movie> Movies, string? ColorPalette)> GetSourceMoviesForMediaAsync(
        Guid userId,
        int mediaId,
        CancellationToken ct = default
    );

    Task<(List<Tv> TvShows, string? ColorPalette)> GetSourceTvShowsForMediaAsync(
        Guid userId,
        int mediaId,
        CancellationToken ct = default
    );

    Task<List<Movie>> GetKeywordMovieSourcesForMovieAsync(
        Guid userId,
        int movieId,
        HashSet<int> excludeIds,
        CancellationToken ct = default
    );

    Task<List<Tv>> GetKeywordTvSourcesForTvAsync(
        Guid userId,
        int tvId,
        HashSet<int> excludeIds,
        CancellationToken ct = default
    );

    Task<List<Movie>> GetCrossTypeMovieSourcesForTvAsync(
        Guid userId,
        int tvId,
        CancellationToken ct = default
    );

    Task<List<Tv>> GetCrossTypeTvSourcesForMovieAsync(
        Guid userId,
        int movieId,
        CancellationToken ct = default
    );

    Task<RecommendationDiagnosticsDto> GetDiagnosticsAsync(CancellationToken ct = default);
}
